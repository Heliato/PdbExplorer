using static PdbExplorer.Pdb.ByteReading;

namespace PdbExplorer.Pdb;

// Views over type records. Every span starts at the leaf-kind word (the length prefix is stripped).

/// <summary>LF_CLASS, LF_STRUCTURE, LF_INTERFACE, LF_UNION or LF_ENUM.</summary>
internal ref struct UdtRecord
{
    public LeafKind Kind;
    public ushort Properties;
    public int MemberCount;
    public uint FieldList;
    public uint UnderlyingType;   // enums only
    public long Size;
    public ReadOnlySpan<byte> Name;
    public ReadOnlySpan<byte> UniqueName;

    /// <summary>The record only declares the type; the definition is another record with the same name.</summary>
    public readonly bool IsForwardReference => (Properties & 0x80) != 0;

    public static bool TryRead(ReadOnlySpan<byte> record, out UdtRecord udt)
    {
        udt = default;
        udt.Kind = (LeafKind)U16(record, 0);

        int nameOffset;
        switch (udt.Kind)
        {
            case LeafKind.LF_CLASS:
            case LeafKind.LF_STRUCTURE:
            case LeafKind.LF_INTERFACE:
                udt.FieldList = U32(record, 6);
                nameOffset = 18;
                udt.Size = Numeric(record, ref nameOffset);
                break;
            case LeafKind.LF_UNION:
                udt.FieldList = U32(record, 6);
                nameOffset = 10;
                udt.Size = Numeric(record, ref nameOffset);
                break;
            case LeafKind.LF_ENUM:
                udt.UnderlyingType = U32(record, 6);
                udt.FieldList = U32(record, 10);
                nameOffset = 14;
                break;
            default:
                return false;
        }

        udt.MemberCount = U16(record, 2);
        udt.Properties = U16(record, 4);
        udt.Name = CString(record, nameOffset);

        const ushort hasUniqueName = 0x200;
        int uniqueNameOffset = nameOffset + udt.Name.Length + 1;
        if ((udt.Properties & hasUniqueName) != 0 && uniqueNameOffset < record.Length)
        {
            udt.UniqueName = CString(record, uniqueNameOffset);
        }
        return true;
    }
}

/// <summary>LF_MODIFIER: const / volatile applied to another type.</summary>
internal readonly ref struct ModifierRecord(ReadOnlySpan<byte> record)
{
    readonly ReadOnlySpan<byte> _record = record;

    public uint UnderlyingType => U32(_record, 2);
    public bool IsConst => (U16(_record, 6) & 1) != 0;
    public bool IsVolatile => (U16(_record, 6) & 2) != 0;
}

/// <summary>LF_POINTER: pointers, references and pointers to members.</summary>
internal readonly ref struct PointerRecord(ReadOnlySpan<byte> record)
{
    readonly ReadOnlySpan<byte> _record = record;

    public const uint LValueReference = 1;
    public const uint PointerToDataMember = 2;
    public const uint PointerToMemberFunction = 3;
    public const uint RValueReference = 4;

    uint Attributes => U32(_record, 6);

    public uint UnderlyingType => U32(_record, 2);
    public uint Mode => (Attributes >> 5) & 7;
    public bool IsVolatile => (Attributes & 0x200) != 0;
    public bool IsConst => (Attributes & 0x400) != 0;
    public uint Size => (Attributes >> 13) & 0x3F;

    /// <summary>Class a pointer to member belongs to, or null when the record is too short to hold it.</summary>
    public uint? ContainingClass => _record.Length >= 14 ? U32(_record, 10) : null;
}

/// <summary>LF_ARRAY. The record stores the total size in bytes, not the element count.</summary>
internal readonly ref struct ArrayRecord(ReadOnlySpan<byte> record)
{
    readonly ReadOnlySpan<byte> _record = record;

    public uint ElementType => U32(_record, 2);

    public long Size
    {
        get
        {
            int offset = 10;
            return Numeric(_record, ref offset);
        }
    }
}

/// <summary>LF_BITFIELD.</summary>
internal readonly ref struct BitfieldRecord(ReadOnlySpan<byte> record)
{
    readonly ReadOnlySpan<byte> _record = record;

    public uint Type => U32(_record, 2);
    public byte Length => _record[6];
    public byte Position => _record[7];
}

/// <summary>LF_FUNC_ID or LF_MFUNC_ID (IPI stream): the identity of a function, used by inline sites.</summary>
internal readonly ref struct FunctionIdRecord(ReadOnlySpan<byte> record)
{
    readonly ReadOnlySpan<byte> _record = record;

    /// <summary>LF_FUNC_ID: id of the enclosing namespace (0 if none). LF_MFUNC_ID: type index of the class.</summary>
    public uint Scope => U32(_record, 2);
    public uint FunctionType => U32(_record, 6);
    public ReadOnlySpan<byte> Name => CString(_record, 10);
}

/// <summary>LF_UDT_SRC_LINE or LF_UDT_MOD_SRC_LINE (IPI stream): where a type is defined.</summary>
internal readonly ref struct UdtSourceLineRecord(ReadOnlySpan<byte> record)
{
    readonly ReadOnlySpan<byte> _record = record;

    public uint TypeIndex => U32(_record, 2);
    public uint SourceFile => U32(_record, 6);
    public uint Line => U32(_record, 10);

    /// <summary>The source file is an offset in the /names table (otherwise it is an IPI string id).</summary>
    public bool SourceIsNameOffset => (LeafKind)U16(_record, 0) == LeafKind.LF_UDT_MOD_SRC_LINE;
}
