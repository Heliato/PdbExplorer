using static PdbExplorer.Pdb.ByteReading;

namespace PdbExplorer.Pdb;

// Views over symbol records. Every span starts at the record-kind word (the length prefix is stripped).

/// <summary>S_GPROC32, S_LPROC32 and their _ID / _DPC variants.</summary>
internal readonly ref struct ProcedureSymbol(ReadOnlySpan<byte> record)
{
    readonly ReadOnlySpan<byte> _record = record;

    /// <summary>Stream offset of the S_END record that closes the procedure.</summary>
    public uint End => U32(_record, 6);
    public uint CodeSize => U32(_record, 14);
    public uint PrologueEnd => U32(_record, 18);
    public uint EpilogueStart => U32(_record, 22);

    /// <summary>A TPI type index, or an IPI id for the _ID variants.</summary>
    public uint TypeOrId => U32(_record, 26);
    public uint Offset => U32(_record, 30);
    public ushort Section => U16(_record, 34);
    public byte Flags => _record[36];
    public ReadOnlySpan<byte> Name => CString(_record, 37);
}

/// <summary>S_GDATA32, S_LDATA32, S_GTHREAD32 and S_LTHREAD32.</summary>
internal readonly ref struct DataSymbol(ReadOnlySpan<byte> record)
{
    readonly ReadOnlySpan<byte> _record = record;

    public uint TypeIndex => U32(_record, 2);
    public uint Offset => U32(_record, 6);
    public ushort Section => U16(_record, 10);
    public ReadOnlySpan<byte> Name => CString(_record, 12);
}

/// <summary>S_PUB32.</summary>
internal readonly ref struct PublicSymbol(ReadOnlySpan<byte> record)
{
    readonly ReadOnlySpan<byte> _record = record;

    uint Flags => U32(_record, 2);

    public bool IsCode => (Flags & 1) != 0;
    public bool IsFunction => (Flags & 2) != 0;
    public uint Offset => U32(_record, 6);
    public ushort Section => U16(_record, 10);
    public ReadOnlySpan<byte> Name => CString(_record, 12);
}

/// <summary>S_CONSTANT.</summary>
internal readonly ref struct ConstantSymbol
{
    public readonly uint TypeIndex;
    public readonly long Value;
    public readonly ReadOnlySpan<byte> Name;

    public ConstantSymbol(ReadOnlySpan<byte> record)
    {
        TypeIndex = U32(record, 2);
        int offset = 6;
        Value = Numeric(record, ref offset);
        Name = CString(record, offset);
    }
}

/// <summary>S_UDT: a typedef, or the name of a class / struct / enum.</summary>
internal readonly ref struct TypedefSymbol(ReadOnlySpan<byte> record)
{
    readonly ReadOnlySpan<byte> _record = record;

    public uint TypeIndex => U32(_record, 2);
    public ReadOnlySpan<byte> Name => CString(_record, 6);
}

/// <summary>S_PROCREF / S_LPROCREF: points from the global symbols to a procedure in a module stream.</summary>
internal readonly ref struct ProcedureReferenceSymbol(ReadOnlySpan<byte> record)
{
    readonly ReadOnlySpan<byte> _record = record;

    public uint SymbolOffset => U32(_record, 6);

    /// <summary>One-based index of the module.</summary>
    public ushort Module => U16(_record, 10);
    public ReadOnlySpan<byte> Name => CString(_record, 12);
}

/// <summary>S_LOCAL: a parameter or local variable of optimized code; its location follows in S_DEFRANGE_* records.</summary>
internal readonly ref struct LocalSymbol(ReadOnlySpan<byte> record)
{
    readonly ReadOnlySpan<byte> _record = record;

    ushort Flags => U16(_record, 6);

    public uint TypeIndex => U32(_record, 2);
    public bool IsParameter => (Flags & 1) != 0;
    public bool IsOptimizedAway => (Flags & 0x100) != 0;
    public ReadOnlySpan<byte> Name => CString(_record, 8);
}

/// <summary>S_REGREL32: a variable at a fixed offset from a register (usually the stack pointer).</summary>
internal readonly ref struct RegisterRelativeSymbol(ReadOnlySpan<byte> record)
{
    readonly ReadOnlySpan<byte> _record = record;

    public int Offset => I32(_record, 2);
    public uint TypeIndex => U32(_record, 6);
    public ushort Register => U16(_record, 10);
    public ReadOnlySpan<byte> Name => CString(_record, 12);
}

/// <summary>S_BPREL32: a variable at a fixed offset from the frame pointer.</summary>
internal readonly ref struct FrameRelativeSymbol(ReadOnlySpan<byte> record)
{
    readonly ReadOnlySpan<byte> _record = record;

    public int Offset => I32(_record, 2);
    public uint TypeIndex => U32(_record, 6);
    public ReadOnlySpan<byte> Name => CString(_record, 10);
}

/// <summary>S_REGISTER: a variable that lives in a register.</summary>
internal readonly ref struct RegisterSymbol(ReadOnlySpan<byte> record)
{
    readonly ReadOnlySpan<byte> _record = record;

    public uint TypeIndex => U32(_record, 2);
    public ushort Register => U16(_record, 6);
    public ReadOnlySpan<byte> Name => CString(_record, 8);
}

/// <summary>S_BLOCK32: a nested lexical block.</summary>
internal readonly ref struct BlockSymbol(ReadOnlySpan<byte> record)
{
    readonly ReadOnlySpan<byte> _record = record;

    public uint CodeSize => U32(_record, 10);
    public uint Offset => U32(_record, 14);
}

/// <summary>S_INLINESITE / S_INLINESITE2: a function inlined into the enclosing one.</summary>
internal readonly ref struct InlineSiteSymbol(ReadOnlySpan<byte> record, SymbolKind kind)
{
    readonly ReadOnlySpan<byte> _record = record;

    /// <summary>IPI id of the inlined function.</summary>
    public uint Inlinee => U32(_record, 10);

    /// <summary>S_INLINESITE2 stores an invocation count before the annotations.</summary>
    ReadOnlySpan<byte> Annotations => _record[(kind == SymbolKind.S_INLINESITE2 ? 18 : 14)..];

    /// <summary>First code offset in the binary annotations, relative to the start of the enclosing function.</summary>
    public uint? CodeOffset
    {
        get
        {
            var annotations = Annotations;
            int position = 0;
            while (position < annotations.Length)
            {
                switch (CompressedInteger(annotations, ref position))
                {
                    case 1:   // CodeOffset
                    case 3:   // ChangeCodeOffset
                        return CompressedInteger(annotations, ref position);
                    case 11:  // ChangeCodeOffsetAndLineOffset: the code delta is the low nibble
                        return CompressedInteger(annotations, ref position) & 0xF;
                    case 12:  // ChangeCodeLengthAndCodeOffset
                        CompressedInteger(annotations, ref position);
                        return CompressedInteger(annotations, ref position);
                    case 2 or 4 or 5 or 6 or 7 or 8 or 9 or 10 or 13:   // opcodes with one operand that do not move the code offset
                        CompressedInteger(annotations, ref position);
                        break;
                    default:  // 0 ends the annotations (the rest is padding)
                        return null;
                }
            }
            return null;
        }
    }

    /// <summary>CodeView compressed unsigned integer: 1, 2 or 4 bytes depending on the top bits of the first one.</summary>
    static uint CompressedInteger(ReadOnlySpan<byte> data, ref int position)
    {
        if (position >= data.Length)
        {
            return 0;
        }

        uint first = data[position++];
        if ((first & 0x80) == 0)
        {
            return first;
        }
        if ((first & 0xC0) == 0x80 && position < data.Length)
        {
            return (first & 0x3F) << 8 | data[position++];
        }
        if ((first & 0xE0) == 0xC0 && position + 2 < data.Length)
        {
            uint value = (first & 0x1F) << 24 | (uint)data[position] << 16 | (uint)data[position + 1] << 8 | data[position + 2];
            position += 3;
            return value;
        }

        position = data.Length;
        return 0;
    }
}

/// <summary>S_THUNK32.</summary>
internal readonly ref struct ThunkSymbol(ReadOnlySpan<byte> record)
{
    readonly ReadOnlySpan<byte> _record = record;

    public uint End => U32(_record, 6);
    public uint Offset => U32(_record, 14);
    public ushort Section => U16(_record, 18);
    public ReadOnlySpan<byte> Name => CString(_record, 23);
}

/// <summary>S_LABEL32.</summary>
internal readonly ref struct LabelSymbol(ReadOnlySpan<byte> record)
{
    readonly ReadOnlySpan<byte> _record = record;

    public uint Offset => U32(_record, 2);
    public ReadOnlySpan<byte> Name => CString(_record, 9);
}

/// <summary>S_CALLSITEINFO and S_HEAPALLOCSITE share this layout: a code offset and the type involved.</summary>
internal readonly ref struct CallSiteSymbol(ReadOnlySpan<byte> record)
{
    readonly ReadOnlySpan<byte> _record = record;

    public uint Offset => U32(_record, 2);
    public uint TypeIndex => U32(_record, 10);
}

/// <summary>S_FRAMEPROC.</summary>
internal readonly ref struct FrameSymbol(ReadOnlySpan<byte> record)
{
    readonly ReadOnlySpan<byte> _record = record;

    public uint FrameSize => U32(_record, 2);
    public uint SavedRegistersSize => U32(_record, 14);
}

/// <summary>S_FILESTATIC.</summary>
internal readonly ref struct FileStaticSymbol(ReadOnlySpan<byte> record)
{
    readonly ReadOnlySpan<byte> _record = record;

    public uint TypeIndex => U32(_record, 2);
    public ReadOnlySpan<byte> Name => CString(_record, 12);
}

/// <summary>S_CALLEES, S_CALLERS and S_INLINEES: a list of IPI function ids.</summary>
internal readonly ref struct FunctionListSymbol(ReadOnlySpan<byte> record)
{
    readonly ReadOnlySpan<byte> _record = record;

    public int Count => (int)Math.Min(U32(_record, 2), (uint)(_record.Length - 6) / 4);

    public uint this[int index] => U32(_record, 6 + 4 * index);
}

/// <summary>S_COMPILE3: the compiler that produced a module.</summary>
internal readonly ref struct CompilerSymbol(ReadOnlySpan<byte> record)
{
    readonly ReadOnlySpan<byte> _record = record;

    public byte Language => _record[2];
    public string BackendVersion => $"{U16(_record, 16)}.{U16(_record, 18)}.{U16(_record, 20)}.{U16(_record, 22)}";
    public ReadOnlySpan<byte> Name => CString(_record, 24);
}

/// <summary>S_SECTION (linker module): a section of the image.</summary>
internal readonly ref struct ImageSectionSymbol(ReadOnlySpan<byte> record)
{
    readonly ReadOnlySpan<byte> _record = record;

    public uint Rva => U32(_record, 6);
    public uint Size => U32(_record, 10);
    public ReadOnlySpan<byte> Name => CString(_record, 18);
}

/// <summary>S_COFFGROUP (linker module): a named group inside a section.</summary>
internal readonly ref struct CoffGroupSymbol(ReadOnlySpan<byte> record)
{
    readonly ReadOnlySpan<byte> _record = record;

    public uint Size => U32(_record, 2);
    public uint Offset => U32(_record, 10);
    public ushort Section => U16(_record, 14);
    public ReadOnlySpan<byte> Name => CString(_record, 16);
}

/// <summary>
/// Walks the symbol records of a stream between two offsets.
/// It is a value type: copying it is a cheap way to look ahead without moving the original.
/// </summary>
internal ref struct SymbolCursor(MsfStream stream, uint position, uint end)
{
    /// <summary>Offset of the next record. Assign it to skip ahead (past a procedure body, for instance).</summary>
    public uint Position = position;

    /// <summary>Offset of the current record.</summary>
    public uint RecordOffset;
    public SymbolKind Kind;
    public ReadOnlySpan<byte> Record;

    public readonly uint End => end;

    public bool MoveNext()
    {
        if ((ulong)Position + 4 > end)
        {
            return false;
        }

        var header = stream.Read(Position, 4);
        int length = U16(header, 0);
        if (length < 2 || (ulong)Position + 2 + (uint)length > end)
        {
            return false;
        }

        RecordOffset = Position;
        Kind = (SymbolKind)U16(header, 2);
        Record = stream.Read(Position + 2, length);
        Position += 2 + (uint)length;
        return true;
    }
}
