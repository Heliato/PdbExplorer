namespace PdbExplorer.Pdb;

/// <summary>
/// One stream of the MSF container: a list of pages that can be anywhere in the file.
/// Reads go straight to the memory-mapped file.
/// </summary>
internal sealed unsafe class MsfStream
{
    readonly byte* _file;
    readonly uint[] _pages;
    readonly int _pageShift;

#pragma warning disable CS0414, IDE0052 // never read: holding it keeps the mapping alive for as long as the stream is reachable
    readonly object _mapping;
#pragma warning restore CS0414, IDE0052

    public MsfStream(object mapping, byte* file, uint[] pages, uint size, int pageShift)
    {
        _mapping = mapping;
        _file = file;
        _pages = pages;
        _pageShift = pageShift;
        Size = size;
    }

    public uint Size { get; }

    public int PageCount => _pages.Length;

    /// <summary>
    /// Returns <paramref name="length"/> bytes at <paramref name="offset"/>. The span points into the file when the
    /// pages involved are contiguous, and into a copy otherwise. Reading outside the stream throws.
    /// </summary>
    public ReadOnlySpan<byte> Read(uint offset, int length)
    {
        if (length < 0 || (ulong)offset + (uint)length > Size)
        {
            throw new InvalidDataException($"Read past the end of the stream (0x{offset:X}+{length} > 0x{Size:X}).");
        }
        if (length == 0)
        {
            return default;
        }

        int firstPage = (int)(offset >> _pageShift);
        int lastPage = (int)(((ulong)offset + (uint)length - 1) >> _pageShift);
        int offsetInPage = (int)(offset & ((1u << _pageShift) - 1));

        int page = firstPage;
        while (page < lastPage && _pages[page + 1] == _pages[page] + 1)
        {
            page++;
        }
        if (page == lastPage)
        {
            return new ReadOnlySpan<byte>(PageAddress(firstPage) + offsetInPage, length);
        }

        var copy = new byte[length];
        int copied = 0;
        for (page = firstPage; copied < length; page++)
        {
            int count = Math.Min(length - copied, (1 << _pageShift) - offsetInPage);
            new ReadOnlySpan<byte>(PageAddress(page) + offsetInPage, count).CopyTo(copy.AsSpan(copied));
            copied += count;
            offsetInPage = 0;
        }
        return copy;
    }

    public ReadOnlySpan<byte> ReadAll() => Read(0, checked((int)Size));

    /// <summary>Bytes of the zero-terminated string at <paramref name="offset"/>, without the terminator.</summary>
    public ReadOnlySpan<byte> ReadCString(uint offset)
    {
        if (offset >= Size)
        {
            return default;
        }

        // The length is unknown: read a growing window until it contains the terminator.
        for (uint window = 256; ; window *= 4)
        {
            int length = (int)Math.Min(window, Size - offset);
            var bytes = Read(offset, length);
            int terminator = bytes.IndexOf((byte)0);
            if (terminator >= 0)
            {
                return bytes[..terminator];
            }
            if (length < window)
            {
                return bytes;
            }
        }
    }

    byte* PageAddress(int page) => _file + ((long)_pages[page] << _pageShift);
}
