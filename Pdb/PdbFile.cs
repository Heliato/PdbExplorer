using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Numerics;
using static PdbExplorer.Pdb.ByteReading;

namespace PdbExplorer.Pdb;

/// <summary>
/// A PDB opened read-only through a memory mapping. Opening it builds name indexes for the types and the
/// global symbols; the records themselves are only decoded when something asks for them.
/// The other parts of this class are in the PdbFile.*.cs files.
/// </summary>
internal sealed unsafe partial class PdbFile
{
    const int PdbInfoStream = 1;

    MemoryMappedFile _mappedFile = null!;
    MemoryMappedViewAccessor _view = null!;
    byte* _file;
    int _pageShift;
    uint[] _streamSizes = [];
    uint[][] _streamPages = [];
    MsfStream?[] _streams = [];
    readonly Dictionary<string, int> _namedStreams = [];

    PdbFile(string path, LoadProgress progress)
    {
        var timer = Stopwatch.StartNew();
        void Step(string message) => progress.Log(Lang.Format("[{0,5:F1} s] ", timer.Elapsed.TotalSeconds) + message);

        FilePath = path;
        OpenContainer(path);
        Step(Lang.Format("MSF container: {0:N0} streams, {1}-byte pages", StreamCount, PageSize));

        ReadInfoStream();
        ReadDebugInfo();
        Step(Lang.Format("DBI: {0:N0} modules, {1} sections", Modules.Count, Sections.Length));

        IndexTypes(progress);
        Step(Lang.Format("TPI: {0:N0} types ({1:N0} distinct class/struct/union/enum) - IPI: {2:N0} ids", Tpi.Count, Types.Count, Ipi.Count));

        IndexGlobalSymbols(progress);
        Step(Lang.Format("Symbols: {0:N0} functions, {1:N0} variables, {2:N0} constants, {3:N0} typedefs, {4:N0} publics",
            Functions.Count, GlobalVariables.Count, Constants.Count, Typedefs.Count, Publics.Count));

        BuildSecondaryTables();
        progress.Report(Lang.Tr("Sorting..."), 1);
        Parallel.ForEach(AllTables, table => table.Sort());

        LoadTime = timer.Elapsed;
        Step(Lang.Tr("Index sorted - ready"));
    }

    public string FilePath { get; }
    public long FileSize { get; private set; }
    public int PageSize { get; private set; }
    public uint PageCount { get; private set; }
    public int StreamCount => _streamSizes.Length;
    public TimeSpan LoadTime { get; }

    public uint Version { get; private set; }

    /// <summary>Seconds since the Unix epoch at which the PDB was written.</summary>
    public uint Timestamp { get; private set; }
    public uint Age { get; private set; }
    public Guid Guid { get; private set; }
    public List<string> Features { get; } = [];

    public SymbolTable Types { get; } = new();
    public SymbolTable Typedefs { get; } = new();
    public SymbolTable Functions { get; } = new();
    public SymbolTable GlobalVariables { get; } = new();
    public SymbolTable Constants { get; } = new();
    public SymbolTable Publics { get; } = new();
    public SymbolTable ModuleNames { get; } = new();
    public SymbolTable SourceFiles { get; } = new();
    public SymbolTable SectionNames { get; } = new();
    public SymbolTable StreamList { get; } = new();

    public SymbolTable[] AllTables =>
        [Types, Typedefs, Functions, GlobalVariables, Constants, Publics, ModuleNames, SourceFiles, SectionNames, StreamList];

    public static PdbFile Open(string path, LoadProgress progress) => new(path, progress);

    public MsfStream GetStream(int index)
    {
        if ((uint)index >= (uint)_streamSizes.Length)
        {
            throw new InvalidDataException($"Stream {index} does not exist.");
        }
        return _streams[index] ??= new MsfStream(_view, _file, _streamPages[index], _streamSizes[index], _pageShift);
    }

    /// <summary>The stream, or null when the index is out of range or the stream is empty.</summary>
    MsfStream? FindStream(int index)
    {
        bool exists = (uint)index < (uint)_streamSizes.Length && _streamSizes[index] > 0;
        return exists ? GetStream(index) : null;
    }

    /// <summary>Maps the file and reads the MSF superblock and stream directory.</summary>
    void OpenContainer(string path)
    {
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        FileSize = file.Length;
        if (FileSize < 0x400)
        {
            throw new InvalidDataException("File too small to be a PDB.");
        }

        _mappedFile = MemoryMappedFile.CreateFromFile(file, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: false);
        _view = _mappedFile.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
        _file = (byte*)_view.SafeMemoryMappedViewHandle.DangerousGetHandle();

        var superBlock = new ReadOnlySpan<byte>(_file, 56);
        if (!superBlock[..32].SequenceEqual("Microsoft C/C++ MSF 7.00\r\n\u001ADS\0\0\0"u8))
        {
            throw new InvalidDataException(Lang.Tr("This file is not an MSF 7.00 PDB."));
        }

        PageSize = (int)U32(superBlock, 32);
        PageCount = U32(superBlock, 40);
        uint directorySize = U32(superBlock, 44);
        uint directoryMapPage = U32(superBlock, 52);
        if (PageSize < 512 || PageSize > 65536 || !BitOperations.IsPow2(PageSize))
        {
            throw new InvalidDataException($"Invalid MSF page size ({PageSize}).");
        }
        _pageShift = BitOperations.Log2((uint)PageSize);

        // Page numbers come from the file. One that points past its end would make a later read
        // fault outside the mapping, so each of them is checked here, once.
        long pagesInFile = FileSize >> _pageShift;
        uint Checked(uint page) => page < pagesInFile
            ? page
            : throw new InvalidDataException($"MSF page {page} is outside the file (truncated or corrupt PDB).");

        int directoryPageCount = PagesFor(directorySize);
        long directoryMapOffset = (long)Checked(directoryMapPage) << _pageShift;
        if (directoryMapOffset + 4L * directoryPageCount > FileSize)
        {
            throw new InvalidDataException("MSF directory map is outside the file.");
        }

        var directoryPages = new uint[directoryPageCount];
        for (int i = 0; i < directoryPageCount; i++)
        {
            directoryPages[i] = Checked(U32(new ReadOnlySpan<byte>(_file + directoryMapOffset + 4L * i, 4), 0));
        }

        // Directory layout: stream count, the size of every stream, then the page list of every stream.
        var directory = new MsfStream(_view, _file, directoryPages, directorySize, _pageShift).ReadAll();
        int streamCount = I32(directory, 0);
        if (streamCount < 0 || 4 + 4L * streamCount > directory.Length)
        {
            throw new InvalidDataException("Corrupt MSF directory.");
        }

        _streamSizes = new uint[streamCount];
        _streamPages = new uint[streamCount][];
        _streams = new MsfStream?[streamCount];
        for (int i = 0; i < streamCount; i++)
        {
            uint size = U32(directory, 4 + 4 * i);
            _streamSizes[i] = size == uint.MaxValue ? 0 : size;   // 0xFFFFFFFF marks a deleted stream
        }

        int offset = 4 + 4 * streamCount;
        for (int i = 0; i < streamCount; i++)
        {
            var pages = new uint[PagesFor(_streamSizes[i])];
            for (int page = 0; page < pages.Length; page++)
            {
                pages[page] = Checked(U32(directory, offset));
                offset += 4;
            }
            _streamPages[i] = pages;
        }
    }

    int PagesFor(uint byteCount) => (int)(((long)byteCount + PageSize - 1) >> _pageShift);

    /// <summary>The PDB info stream: identity of the PDB, named streams and feature codes.</summary>
    void ReadInfoStream()
    {
        var info = GetStream(PdbInfoStream).ReadAll();
        Version = U32(info, 0);
        Timestamp = U32(info, 4);
        Age = U32(info, 8);
        Guid = new Guid(info.Slice(12, 16));

        // Named stream map: a string buffer, then a hash table of (offset in the buffer, stream index).
        int offset = 28;
        int namesLength = I32(info, offset);
        var names = info.Slice(offset + 4, namesLength);
        offset += 4 + namesLength;

        int entryCount = I32(info, offset);
        offset += 8;                               // entry count and capacity
        offset += 4 + 4 * I32(info, offset);       // "present" bit vector
        offset += 4 + 4 * I32(info, offset);       // "deleted" bit vector
        for (int i = 0; i < entryCount; i++)
        {
            string name = Utf8(CString(names, I32(info, offset)));
            _namedStreams[name] = I32(info, offset + 4);
            offset += 8;
        }

        for (; offset + 4 <= info.Length; offset += 4)
        {
            uint code = U32(info, offset);
            if (code == 0)
            {
                continue;
            }
            Features.Add(code switch
            {
                20140508 => "VC140",
                20091201 => "VC110",
                0x4D544F4E => "NoTypeMerge",
                0x494E494D => "MinimalDebugInfo",
                _ => $"0x{code:X}",
            });
        }
    }
}
