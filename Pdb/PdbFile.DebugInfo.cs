using System.Runtime.InteropServices;
using System.Text;
using static PdbExplorer.Pdb.ByteReading;

namespace PdbExplorer.Pdb;

// The DBI stream: modules, their source files, and the streams that hold everything else.
internal sealed unsafe partial class PdbFile
{
    const int DbiStream = 3;
    const int DbiHeaderSize = 64;
    const int ModuleHeaderSize = 64;
    const int SectionHeaderSize = 40;

    /// <summary>Slot of the section-header stream in the optional debug header.</summary>
    const int SectionHeadersSlot = 5;

    static readonly string[] DebugStreamNames =
    [
        "FPO", "Exception", "Fixup", "OMAP to source", "OMAP from source", "Section headers",
        "Token RID map", "Xdata", "Pdata", "New FPO", "Original section headers",
    ];

    MsfStream? _dbi;
    ushort[] _debugStreams = [];
    int _globalsHashStream = -1;
    int _publicsHashStream = -1;

    // File Info substream: an array of name offsets (one run per module), then the names themselves.
    uint _fileOffsetsStart;
    uint _fileNamesStart;
    uint _fileOffsetCount;

    public ushort Machine { get; private set; }
    public int SymbolRecordStream { get; private set; } = -1;
    public List<Module> Modules { get; } = [];
    public Section[] Sections { get; private set; } = [];
    public string[] StreamNames { get; private set; } = [];

    public string SourceFileName(uint nameOffset) =>
        _dbi == null ? "?" : Utf8(_dbi.ReadCString(_fileNamesStart + nameOffset));

    public List<string> GetSourceFiles(Module module)
    {
        var files = new List<string>(module.SourceFileCount);
        foreach (uint nameOffset in SourceFileOffsets(module))
        {
            files.Add(SourceFileName(nameOffset));
        }
        return files;
    }

    /// <summary>Indices of the modules whose source file list contains this file.</summary>
    public List<int> FindModulesUsing(uint nameOffset)
    {
        var modules = new List<int>();
        foreach (var module in Modules)
        {
            if (SourceFileOffsets(module).Contains(nameOffset))
            {
                modules.Add(module.Index);
            }
        }
        return modules;
    }

    ReadOnlySpan<uint> SourceFileOffsets(Module module)
    {
        if (_dbi == null || module.SourceFileCount == 0)
        {
            return default;
        }
        var bytes = _dbi.Read(_fileOffsetsStart + 4 * (uint)module.FirstSourceFile, 4 * module.SourceFileCount);
        return MemoryMarshal.Cast<byte, uint>(bytes);
    }

    void ReadDebugInfo()
    {
        var dbi = FindStream(DbiStream);
        if (dbi == null || dbi.Size < DbiHeaderSize)
        {
            return;
        }
        _dbi = dbi;

        var header = dbi.Read(0, DbiHeaderSize);
        _globalsHashStream = U16(header, 12);
        _publicsHashStream = U16(header, 16);
        SymbolRecordStream = U16(header, 20);
        Machine = U16(header, 58);

        // The substreams follow the header in this order.
        uint modulesSize = U32(header, 24);
        uint sectionContributionsSize = U32(header, 28);
        uint sectionMapSize = U32(header, 32);
        uint fileInfoSize = U32(header, 36);
        uint typeServerMapSize = U32(header, 40);
        uint debugHeaderSize = U32(header, 48);
        uint editAndContinueSize = U32(header, 52);
        ulong total = (ulong)DbiHeaderSize + modulesSize + sectionContributionsSize + sectionMapSize
            + fileInfoSize + typeServerMapSize + debugHeaderSize + editAndContinueSize;
        if (total > dbi.Size)
        {
            throw new InvalidDataException("Inconsistent DBI header.");
        }

        uint modulesEnd = DbiHeaderSize + modulesSize;
        ReadModules(dbi, modulesEnd);

        uint fileInfoStart = modulesEnd + sectionContributionsSize + sectionMapSize;
        ReadFileInfo(dbi, fileInfoStart, fileInfoSize);

        uint debugHeaderStart = fileInfoStart + fileInfoSize + typeServerMapSize + editAndContinueSize;
        _debugStreams = MemoryMarshal.Cast<byte, ushort>(dbi.Read(debugHeaderStart, (int)(debugHeaderSize & ~1u))).ToArray();
        ReadSectionHeaders();
    }

    void ReadModules(MsfStream dbi, uint end)
    {
        uint offset = DbiHeaderSize;
        while (offset + ModuleHeaderSize <= end)
        {
            var header = dbi.Read(offset, ModuleHeaderSize);
            var name = dbi.ReadCString(offset + ModuleHeaderSize);
            var objectName = dbi.ReadCString(offset + ModuleHeaderSize + (uint)name.Length + 1);
            Modules.Add(new Module
            {
                Index = Modules.Count,
                Section = U16(header, 4),
                Offset = U32(header, 8),
                Size = U32(header, 12),
                StreamIndex = U16(header, 34),
                SymbolsSize = U32(header, 36),
                C11LinesSize = U32(header, 40),
                C13LinesSize = U32(header, 44),
                Name = Utf8(name),
                ObjectName = Utf8(objectName),
            });

            // Entries are padded to 4 bytes.
            uint entrySize = ModuleHeaderSize + (uint)name.Length + 1 + (uint)objectName.Length + 1;
            offset = (offset + entrySize + 3) & ~3u;
        }
    }

    void ReadFileInfo(MsfStream dbi, uint start, uint size)
    {
        if (size < 4)
        {
            return;
        }

        // The header stores the module count on 16 bits. If it does not match, the layout cannot be trusted.
        int moduleCount = U16(dbi.Read(start, 2), 0);
        if (moduleCount != Modules.Count || 4 + 4L * moduleCount > size)
        {
            return;
        }

        var fileCounts = dbi.Read(start + 4 + 2 * (uint)moduleCount, 2 * moduleCount);
        uint total = 0;
        for (int i = 0; i < moduleCount; i++)
        {
            ushort count = U16(fileCounts, 2 * i);
            Modules[i].FirstSourceFile = (int)total;
            Modules[i].SourceFileCount = count;
            total += count;
        }

        _fileOffsetsStart = start + 4 + 4 * (uint)moduleCount;
        _fileNamesStart = _fileOffsetsStart + 4 * total;
        _fileOffsetCount = total;
        if (_fileNamesStart > start + size)
        {
            _fileOffsetCount = 0;
            foreach (var module in Modules)
            {
                module.SourceFileCount = 0;
            }
        }
    }

    /// <summary>PE section headers: they turn the section:offset addresses of symbols into RVAs.</summary>
    void ReadSectionHeaders()
    {
        if (_debugStreams.Length <= SectionHeadersSlot || FindStream(_debugStreams[SectionHeadersSlot]) is not { } stream)
        {
            return;
        }

        var headers = stream.ReadAll();
        Sections = new Section[headers.Length / SectionHeaderSize];
        for (int i = 0; i < Sections.Length; i++)
        {
            var header = headers.Slice(i * SectionHeaderSize, SectionHeaderSize);
            Sections[i] = new Section(
                Name: Utf8(CString(header[..8], 0)),
                VirtualSize: U32(header, 8),
                VirtualAddress: U32(header, 12),
                RawSize: U32(header, 16),
                RawPointer: U32(header, 20),
                Characteristics: U32(header, 36));
        }
    }

    /// <summary>Tables for the modules, source files, sections and streams, so the explorer can list and search them.</summary>
    void BuildSecondaryTables()
    {
        foreach (var module in Modules)
        {
            ModuleNames.Add(Encoding.UTF8.GetBytes(module.Name), (uint)module.Index);
        }

        if (_dbi != null && _fileOffsetCount > 0)
        {
            // The same file is listed by every module that includes it.
            var distinctOffsets = new HashSet<uint>();
            const uint batch = 1 << 16;
            for (uint i = 0; i < _fileOffsetCount; i += batch)
            {
                int count = (int)Math.Min(batch, _fileOffsetCount - i);
                var offsets = MemoryMarshal.Cast<byte, uint>(_dbi.Read(_fileOffsetsStart + 4 * i, 4 * count));
                distinctOffsets.UnionWith(offsets.ToArray());
            }
            foreach (uint nameOffset in distinctOffsets)
            {
                SourceFiles.Add(_dbi.ReadCString(_fileNamesStart + nameOffset), nameOffset);
            }
        }

        for (int i = 0; i < Sections.Length; i++)
        {
            SectionNames.Add(Encoding.UTF8.GetBytes(Sections[i].Name), (uint)i);
        }

        NameStreams();
        for (int i = 0; i < StreamCount; i++)
        {
            StreamList.Add(Encoding.UTF8.GetBytes($"{i:D5}  {StreamNames[i]}"), (uint)i);
        }
    }

    /// <summary>Streams have no name of their own: label the ones whose role is known from elsewhere.</summary>
    void NameStreams()
    {
        var names = new string?[StreamCount];
        void Name(int index, string name)
        {
            if ((uint)index < (uint)names.Length)
            {
                names[index] ??= name;
            }
        }

        Name(0, "Old MSF directory");
        Name(PdbInfoStream, "PDB Info");
        Name(TpiStream, "TPI - types");
        Name(DbiStream, "DBI - debug info");
        Name(IpiStream, "IPI - ids");
        foreach (var (name, index) in _namedStreams)
        {
            Name(index, name);
        }
        Name(_globalsHashStream, "GSI - global symbol hash");
        Name(_publicsHashStream, "PSI - public symbol hash");
        Name(SymbolRecordStream, "Symbol records");
        Name(Tpi.HashStreamIndex, "TPI - hash");
        Name(Ipi.HashStreamIndex, "IPI - hash");
        for (int slot = 0; slot < Math.Min(DebugStreamNames.Length, _debugStreams.Length); slot++)
        {
            Name(_debugStreams[slot], DebugStreamNames[slot]);
        }
        foreach (var module in Modules)
        {
            Name(module.StreamIndex, "Module: " + Path.GetFileName(module.Name));
        }

        StreamNames = Array.ConvertAll(names, name => name ?? "(unknown)");
    }
}
