using System.Buffers.Binary;
using System.Text;

namespace PdbExplorer.Pdb;

internal static class ByteReading
{
    public static ushort U16(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);

    public static uint U32(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);

    public static int I32(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadInt32LittleEndian(data[offset..]);

    public static string Utf8(ReadOnlySpan<byte> bytes) => Encoding.UTF8.GetString(bytes);

    /// <summary>Bytes of the zero-terminated string starting at <paramref name="offset"/>, without the terminator.</summary>
    public static ReadOnlySpan<byte> CString(ReadOnlySpan<byte> data, int offset)
    {
        var tail = data[offset..];
        int end = tail.IndexOf((byte)0);
        return end < 0 ? tail : tail[..end];
    }

    /// <summary>
    /// Reads a CodeView numeric leaf and advances <paramref name="offset"/> past it. Values below 0x8000
    /// are stored inline; otherwise the word names the type of the value that follows.
    /// </summary>
    public static long Numeric(ReadOnlySpan<byte> data, ref int offset)
    {
        ushort leaf = U16(data, offset);
        offset += 2;
        if (leaf < 0x8000)
        {
            return leaf;
        }

        int start = offset;
        switch (leaf)
        {
            case 0x8000: // LF_CHAR
                offset += 1;
                return (sbyte)data[start];
            case 0x8001: // LF_SHORT
                offset += 2;
                return (short)U16(data, start);
            case 0x8002: // LF_USHORT
                offset += 2;
                return U16(data, start);
            case 0x8003: // LF_LONG
                offset += 4;
                return I32(data, start);
            case 0x8004: // LF_ULONG
                offset += 4;
                return U32(data, start);
            case 0x8009: // LF_QUADWORD
            case 0x800a: // LF_UQUADWORD
                offset += 8;
                return BinaryPrimitives.ReadInt64LittleEndian(data[start..]);

            // Floating-point leaves never describe an offset, a size or an enumerator: skip them.
            case 0x8005:
                offset += 4;
                return 0;
            case 0x8006:
                offset += 8;
                return 0;
            case 0x8007:
                offset += 10;
                return 0;
            case 0x8008:
                offset += 16;
                return 0;
            default:
                throw new InvalidDataException($"Unknown numeric leaf 0x{leaf:X}.");
        }
    }
}
