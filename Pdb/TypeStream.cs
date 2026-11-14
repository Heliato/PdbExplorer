using static PdbExplorer.Pdb.ByteReading;

namespace PdbExplorer.Pdb;

internal delegate void TypeRecordVisitor(uint typeIndex, LeafKind kind, uint offset, int length);

/// <summary>
/// The TPI (types) or IPI (ids) stream. Records have variable sizes, so the constructor walks them once
/// and remembers where each one starts; after that any record can be read by its index.
/// </summary>
internal sealed class TypeStream
{
    const int HeaderSize = 56;

    readonly MsfStream? _stream;
    readonly uint[] _recordOffsets = [];

    /// <param name="recordCounts">Incremented at the index of each record kind met (65536 entries).</param>
    /// <param name="visit">Called for every record, in index order.</param>
    public TypeStream(MsfStream? stream, int[] recordCounts, TypeRecordVisitor visit, Action<double>? progress = null)
    {
        if (stream == null || stream.Size < HeaderSize)
        {
            return;
        }

        _stream = stream;
        var header = stream.Read(0, HeaderSize);
        uint headerSize = U32(header, 4);
        uint recordBytes = U32(header, 16);
        FirstIndex = U32(header, 8);
        EndIndex = U32(header, 12);
        HashStreamIndex = U16(header, 20);

        // A record takes at least 4 bytes: more indices than that means a corrupt header, not a huge allocation.
        if (EndIndex < FirstIndex || (ulong)headerSize + recordBytes > stream.Size || EndIndex - FirstIndex > recordBytes / 4 + 1)
        {
            throw new InvalidDataException("Inconsistent TPI/IPI header.");
        }

        _recordOffsets = new uint[EndIndex - FirstIndex];
        uint offset = headerSize;
        uint end = headerSize + recordBytes;
        int count = 0;
        while (offset + 4 <= end && count < _recordOffsets.Length)
        {
            var recordHeader = stream.Read(offset, 4);
            int length = U16(recordHeader, 0);
            var kind = (LeafKind)U16(recordHeader, 2);
            if (length < 2 || offset + 2 + (uint)length > end)
            {
                break;   // truncated stream: keep the records read so far
            }

            _recordOffsets[count] = offset;
            recordCounts[(ushort)kind]++;
            visit(FirstIndex + (uint)count, kind, offset, length);

            count++;
            offset += 2 + (uint)length;
            if ((count & 0x3FFFF) == 0)
            {
                progress?.Invoke((double)(offset - headerSize) / recordBytes);
            }
        }
        Count = count;
    }

    /// <summary>First index of the stream; lower values are built-in types.</summary>
    public uint FirstIndex { get; } = 0x1000;

    /// <summary>One past the last index, as announced by the header.</summary>
    public uint EndIndex { get; } = 0x1000;

    /// <summary>Number of records actually read.</summary>
    public int Count { get; }

    public int HashStreamIndex { get; } = Module.NoStream;

    public bool Contains(uint index) => index >= FirstIndex && index - FirstIndex < (uint)Count;

    public ReadOnlySpan<byte> Record(uint index) => RecordAt(_recordOffsets[index - FirstIndex]);

    /// <summary>The record whose length prefix is at <paramref name="offset"/>, starting at its kind word.</summary>
    public ReadOnlySpan<byte> RecordAt(uint offset)
    {
        int length = U16(_stream!.Read(offset, 2), 0);
        return _stream.Read(offset + 2, length);
    }
}
