using System.Collections.Concurrent;
using static PdbExplorer.Pdb.ByteReading;
using static PdbExplorer.Pdb.LeafKind;

namespace PdbExplorer.Pdb;

/// <summary>Kinds stored with each item of <see cref="PdbFile.Types"/>; the explorer filters on them.</summary>
internal static class TypeKinds
{
    public const byte Class = 0;
    public const byte Struct = 1;
    public const byte Union = 2;
    public const byte Enum = 3;
    public const byte Interface = 4;
}

// The TPI and IPI streams: type names, sizes, members and signatures.
internal sealed unsafe partial class PdbFile
{
    const int TpiStream = 2;
    const int IpiStream = 4;
    const int MaxTypeDepth = 32;

    // Type indices that have an LF_UDT_*SRC_LINE record (sorted), and the IPI offset of that record.
    uint[] _sourceLineTypes = [];
    uint[] _sourceLineRecords = [];

    readonly ConcurrentDictionary<uint, uint> _definitions = new();

    public TypeStream Tpi { get; private set; } = null!;
    public TypeStream Ipi { get; private set; } = null!;

    /// <summary>Number of records of each kind, indexed by the kind value.</summary>
    public int[] TypeRecordCounts { get; } = new int[0x10000];
    public int[] IdRecordCounts { get; } = new int[0x10000];

    public string TypeName(uint typeIndex, int depth = 0)
    {
        var parts = SplitType(typeIndex, depth);
        return parts.Prefix + parts.Suffix;
    }

    /// <summary>The type and a name in declaration order: "int values[4]", "void (*callback)(int)".</summary>
    public string Declaration(uint typeIndex, string name)
    {
        var parts = SplitType(typeIndex);
        string space = parts.NameIsEnclosed || name.Length == 0 ? "" : " ";
        return parts.Prefix + space + name + parts.Suffix;
    }

    /// <summary>Splits a type around the position of the declared name (see <see cref="TypeParts"/>).</summary>
    public TypeParts SplitType(uint typeIndex, int depth = 0)
    {
        if (typeIndex < Tpi.FirstIndex)
        {
            return new TypeParts(BuiltInTypes.Name(typeIndex), "", false);
        }
        if (!Tpi.Contains(typeIndex) || depth > MaxTypeDepth)
        {
            return new TypeParts($"<type 0x{typeIndex:X}>", "", false);
        }

        var record = Tpi.Record(typeIndex);
        var kind = (LeafKind)U16(record, 0);
        switch (kind)
        {
            case LF_MODIFIER:
                {
                    var modifier = new ModifierRecord(record);
                    var inner = SplitType(modifier.UnderlyingType, depth + 1);
                    string qualifiers = (modifier.IsConst ? "const " : "") + (modifier.IsVolatile ? "volatile " : "");
                    return inner with { Prefix = qualifiers + inner.Prefix };
                }

            case LF_POINTER:
                return SplitPointer(new PointerRecord(record), depth);

            case LF_ARRAY:
                {
                    var array = new ArrayRecord(record);
                    long elementSize = SizeOf(array.ElementType, depth + 1);
                    string count = elementSize > 0 ? (array.Size / elementSize).ToString() : "";
                    var element = SplitType(array.ElementType, depth + 1);
                    return element with { Suffix = "[" + count + "]" + element.Suffix };
                }

            case LF_PROCEDURE:
            case LF_MFUNCTION:
                {
                    var signature = GetSignature(typeIndex)!;
                    var parameters = signature.Parameters.Select(type => type == 0 ? "..." : TypeName(type, depth + 1));
                    string suffix = "(" + string.Join(", ", parameters) + ")" + (signature.IsConst ? " const" : "");
                    return new TypeParts(TypeName(signature.ReturnType, depth + 1), suffix, false);
                }

            case LF_BITFIELD:
                return SplitType(new BitfieldRecord(record).Type, depth + 1);

            default:
                string name = UdtRecord.TryRead(record, out var udt) ? Utf8(udt.Name) : $"<{kind.DisplayName()}>";
                return new TypeParts(name, "", false);
        }
    }

    TypeParts SplitPointer(PointerRecord pointer, int depth)
    {
        string symbol = pointer.Mode switch
        {
            PointerRecord.LValueReference => "&",
            PointerRecord.RValueReference => "&&",
            PointerRecord.PointerToDataMember or PointerRecord.PointerToMemberFunction =>
                (pointer.ContainingClass is { } owner ? TypeName(owner, depth + 1) : "?") + "::*",
            _ => "*",
        };
        symbol += (pointer.IsConst ? " const" : "") + (pointer.IsVolatile ? " volatile" : "");

        var target = SplitType(pointer.UnderlyingType, depth + 1);
        if (target.Suffix.Length == 0)
        {
            return new TypeParts(target.Prefix + symbol, "", false);
        }
        if (target.NameIsEnclosed)
        {
            return new TypeParts(target.Prefix + symbol, target.Suffix, true);
        }

        // Pointer to a function or to an array: the symbol and the name go inside parentheses.
        return new TypeParts(target.Prefix + " (" + symbol, ")" + target.Suffix, true);
    }

    public FunctionSignature? GetSignature(uint typeIndex)
    {
        if (!Tpi.Contains(typeIndex))
        {
            return null;
        }

        var record = Tpi.Record(typeIndex);
        const byte constructorAttribute = 2;
        switch ((LeafKind)U16(record, 0))
        {
            case LF_PROCEDURE:
                return new FunctionSignature(
                    ReturnType: U32(record, 2),
                    ClassType: 0,
                    Parameters: ReadArgumentList(U32(record, 10)),
                    IsConst: false,
                    IsConstructor: (record[7] & constructorAttribute) != 0,
                    ThisAdjustment: 0,
                    IsMember: false);

            case LF_MFUNCTION:
                return new FunctionSignature(
                    ReturnType: U32(record, 2),
                    ClassType: U32(record, 6),
                    Parameters: ReadArgumentList(U32(record, 18)),
                    IsConst: PointsToConst(thisType: U32(record, 10)),
                    IsConstructor: (record[15] & constructorAttribute) != 0,
                    ThisAdjustment: I32(record, 22),
                    IsMember: true);

            default:
                return null;
        }
    }

    uint[] ReadArgumentList(uint typeIndex)
    {
        if (!Tpi.Contains(typeIndex))
        {
            return [];
        }

        var record = Tpi.Record(typeIndex);
        if ((LeafKind)U16(record, 0) != LF_ARGLIST)
        {
            return [];
        }

        var arguments = new uint[Math.Min(U32(record, 2), (uint)(record.Length - 6) / 4)];
        for (int i = 0; i < arguments.Length; i++)
        {
            arguments[i] = U32(record, 6 + 4 * i);
        }
        return arguments;
    }

    /// <summary>A member function is const when its "this" is a pointer to a const object.</summary>
    bool PointsToConst(uint thisType)
    {
        if (!Tpi.Contains(thisType))
        {
            return false;
        }

        var pointer = Tpi.Record(thisType);
        if ((LeafKind)U16(pointer, 0) != LF_POINTER)
        {
            return false;
        }

        uint pointee = new PointerRecord(pointer).UnderlyingType;
        if (!Tpi.Contains(pointee))
        {
            return false;
        }

        var record = Tpi.Record(pointee);
        return (LeafKind)U16(record, 0) == LF_MODIFIER && new ModifierRecord(record).IsConst;
    }

    /// <summary>Size in bytes, or 0 when it is unknown (functions, undefined forward references).</summary>
    public long SizeOf(uint typeIndex, int depth = 0)
    {
        if (typeIndex < Tpi.FirstIndex)
        {
            return BuiltInTypes.Size(typeIndex);
        }
        if (!Tpi.Contains(typeIndex) || depth > MaxTypeDepth)
        {
            return 0;
        }

        var record = Tpi.Record(typeIndex);
        switch ((LeafKind)U16(record, 0))
        {
            case LF_MODIFIER:
            case LF_BITFIELD:
                return SizeOf(U32(record, 2), depth + 1);

            case LF_POINTER:
                uint size = new PointerRecord(record).Size;
                return size != 0 ? size : 8;

            case LF_ARRAY:
                return new ArrayRecord(record).Size;
        }

        if (!UdtRecord.TryRead(record, out var udt))
        {
            return 0;
        }
        if (udt.Kind == LF_ENUM)
        {
            return SizeOf(udt.UnderlyingType, depth + 1);
        }
        if (!udt.IsForwardReference)
        {
            return udt.Size;
        }

        uint definition = ResolveForwardReference(typeIndex);
        if (definition == typeIndex || !UdtRecord.TryRead(Tpi.Record(definition), out var defined))
        {
            return 0;
        }
        return defined.Kind == LF_ENUM ? SizeOf(defined.UnderlyingType, depth + 1) : defined.Size;
    }

    /// <summary>
    /// The class, struct, union or enum reached by removing pointers, qualifiers and array dimensions,
    /// or 0 when the type is built on something else.
    /// </summary>
    public uint UnderlyingUdt(uint typeIndex)
    {
        for (int depth = 0; depth < MaxTypeDepth && Tpi.Contains(typeIndex); depth++)
        {
            var record = Tpi.Record(typeIndex);
            var kind = (LeafKind)U16(record, 0);
            if (kind.IsUdt())
            {
                return typeIndex;
            }
            if (kind is not (LF_MODIFIER or LF_POINTER or LF_ARRAY or LF_BITFIELD))
            {
                return 0;
            }
            typeIndex = U32(record, 2);
        }
        return 0;
    }

    /// <summary>
    /// Members usually refer to a forward declaration of their type. This returns the full definition
    /// with the same name, or the index itself when there is none (or when it is not a forward declaration).
    /// </summary>
    public uint ResolveForwardReference(uint typeIndex)
    {
        if (!Tpi.Contains(typeIndex)
            || !UdtRecord.TryRead(Tpi.Record(typeIndex), out var udt)
            || !udt.IsForwardReference)
        {
            return typeIndex;
        }
        if (_definitions.TryGetValue(typeIndex, out uint known))
        {
            return known;
        }

        // Unrelated types can share a name (<unnamed-tag>, lambdas): the decorated name tells them apart.
        // Without one, the first definition with that name is used.
        uint definition = typeIndex;
        var (start, end) = Types.EqualRange(udt.Name);
        for (int position = start; position < end; position++)
        {
            uint candidate = Types.Reference(Types.SortedItems[position]);
            if (definition == typeIndex)
            {
                definition = candidate;
            }
            if (udt.UniqueName.Length == 0)
            {
                break;
            }
            if (UdtRecord.TryRead(Tpi.Record(candidate), out var other) && other.UniqueName.SequenceEqual(udt.UniqueName))
            {
                definition = candidate;
                break;
            }
        }
        return _definitions[typeIndex] = definition;
    }

    public (byte Length, byte Position)? GetBitfield(uint typeIndex)
    {
        if (!Tpi.Contains(typeIndex))
        {
            return null;
        }

        var record = Tpi.Record(typeIndex);
        if ((LeafKind)U16(record, 0) != LF_BITFIELD)
        {
            return null;
        }

        var bitfield = new BitfieldRecord(record);
        return (bitfield.Length, bitfield.Position);
    }

    /// <summary>"class", "struct", "union", "enum" or "interface" for a user-defined type.</summary>
    public string UdtKeyword(uint typeIndex) =>
        Tpi.Contains(typeIndex) ? ((LeafKind)U16(Tpi.Record(typeIndex), 0)).Keyword() : "struct";

    /// <summary>Source file and line of the definition of a type, when the PDB recorded it.</summary>
    public (string File, uint Line)? FindSource(uint typeIndex)
    {
        if (FindSourceRecord(typeIndex, out var source))
        {
            return (SourceName(source.SourceFile, source.SourceIsNameOffset), source.Line);
        }
        return null;
    }

    public bool FindSourceRecord(uint typeIndex, out UdtSourceLineRecord source)
    {
        int position = Array.BinarySearch(_sourceLineTypes, typeIndex);
        source = position < 0 ? default : new UdtSourceLineRecord(Ipi.RecordAt(_sourceLineRecords[position]));
        return position >= 0;
    }

    public string SourceName(uint sourceFile, bool isNameOffset) =>
        isNameOffset ? GetName(sourceFile) : IdName(sourceFile);

    /// <summary>
    /// Every member of a field list. Continuation records (LF_INDEX) are followed and overloaded
    /// methods (LF_METHOD) are expanded into one LF_ONEMETHOD entry per overload.
    /// </summary>
    public List<Member> GetMembers(uint fieldList)
    {
        const int maxContinuations = 4096;
        var members = new List<Member>();
        for (int guard = 0; fieldList != 0 && Tpi.Contains(fieldList) && guard < maxContinuations; guard++)
        {
            var record = Tpi.Record(fieldList);
            if ((LeafKind)U16(record, 0) != LF_FIELDLIST)
            {
                break;
            }

            uint continuation = 0;
            int offset = 2;
            while (offset + 4 <= record.Length)
            {
                // Sub-records are padded with LF_PAD bytes whose low nibble is the distance to the next one.
                if (record[offset] >= 0xF0)
                {
                    offset += record[offset] & 0xF;
                    continue;
                }
                offset = ReadMember(record, offset, members, ref continuation);
            }
            fieldList = continuation;
        }
        return members;
    }

    /// <summary>Decodes the sub-record at <paramref name="start"/> and returns the offset of the next one.</summary>
    int ReadMember(ReadOnlySpan<byte> list, int start, List<Member> members, ref uint continuation)
    {
        var kind = (LeafKind)U16(list, start);
        ushort attributes = U16(list, start + 2);
        int offset;
        ReadOnlySpan<byte> name;

        switch (kind)
        {
            case LF_BCLASS:
                {
                    offset = start + 8;
                    long baseOffset = Numeric(list, ref offset);
                    members.Add(new Member(kind, attributes, U32(list, start + 4), baseOffset, ""));
                    return offset;
                }

            case LF_VBCLASS:
            case LF_IVBCLASS:
                {
                    offset = start + 12;
                    long vbptrOffset = Numeric(list, ref offset);
                    long vbtableIndex = Numeric(list, ref offset);
                    members.Add(new Member(kind, attributes, U32(list, start + 4), vbptrOffset, "", vbtableIndex));
                    return offset;
                }

            case LF_INDEX:
                continuation = U32(list, start + 4);
                return start + 8;

            case LF_VFUNCTAB:
                members.Add(new Member(kind, 0, U32(list, start + 4), 0, ""));
                return start + 8;

            case LF_VFUNCOFF:
                members.Add(new Member(kind, 0, U32(list, start + 4), U32(list, start + 8), ""));
                return start + 12;

            case LF_ENUMERATE:
                {
                    offset = start + 4;
                    long value = Numeric(list, ref offset);
                    name = CString(list, offset);
                    members.Add(new Member(kind, attributes, 0, value, Utf8(name)));
                    return offset + name.Length + 1;
                }

            case LF_NESTTYPE:
            case LF_NESTTYPEEX:
                name = CString(list, start + 8);
                members.Add(new Member(LF_NESTTYPE, attributes, U32(list, start + 4), 0, Utf8(name)));
                return start + 8 + name.Length + 1;

            case LF_MEMBER:
                {
                    offset = start + 8;
                    long memberOffset = Numeric(list, ref offset);
                    name = CString(list, offset);
                    members.Add(new Member(kind, attributes, U32(list, start + 4), memberOffset, Utf8(name)));
                    return offset + name.Length + 1;
                }

            case LF_STMEMBER:
                name = CString(list, start + 8);
                members.Add(new Member(kind, attributes, U32(list, start + 4), 0, Utf8(name)));
                return start + 8 + name.Length + 1;

            case LF_ONEMETHOD:
                {
                    offset = start + 8;
                    long vtableOffset = -1;
                    if (IntroducesVirtual(attributes))
                    {
                        vtableOffset = U32(list, offset);
                        offset += 4;
                    }
                    name = CString(list, offset);
                    members.Add(new Member(kind, attributes, U32(list, start + 4), 0, Utf8(name), vtableOffset));
                    return offset + name.Length + 1;
                }

            case LF_METHOD:
                name = CString(list, start + 8);
                AddOverloads(methodList: U32(list, start + 4), Utf8(name), members);
                return start + 8 + name.Length + 1;

            default:
                // Unknown sub-record: its size is unknown too, so the rest of this list cannot be read.
                members.Add(new Member(Member.Undecoded, 0, 0, 0, $"<{kind.DisplayName()} not decoded>"));
                return list.Length;
        }
    }

    /// <summary>Only methods that add a vtable slot store its offset.</summary>
    static bool IntroducesVirtual(ushort attributes) =>
        (MethodKind)((attributes >> 2) & 7) is MethodKind.IntroducingVirtual or MethodKind.PureIntroducingVirtual;

    void AddOverloads(uint methodList, string name, List<Member> members)
    {
        if (!Tpi.Contains(methodList))
        {
            return;
        }

        var list = Tpi.Record(methodList);
        if ((LeafKind)U16(list, 0) != LF_METHODLIST)
        {
            return;
        }

        int offset = 2;
        while (offset + 8 <= list.Length)
        {
            ushort attributes = U16(list, offset);
            uint type = U32(list, offset + 4);
            offset += 8;

            long vtableOffset = -1;
            if (IntroducesVirtual(attributes) && offset + 4 <= list.Length)
            {
                vtableOffset = U32(list, offset);
                offset += 4;
            }
            members.Add(new Member(LF_ONEMETHOD, attributes, type, 0, name, vtableOffset));
        }
    }

    /// <summary>Walks the TPI and IPI streams (in parallel) and fills <see cref="Types"/>.</summary>
    void IndexTypes(LoadProgress progress)
    {
        var typeStream = FindStream(TpiStream);
        var idStream = FindStream(IpiStream);
        var sourceLines = new List<(uint TypeIndex, uint RecordOffset)>();

        // The same type is often defined in several translation units with small differences. The table keeps
        // one per decorated name: the definition with the most members. Key: hash of the decorated name.
        var kept = new Dictionary<ulong, (int Item, int MemberCount)>();

        TypeStream? tpi = null;
        TypeStream? ipi = null;
        Parallel.Invoke(
            () => tpi = new TypeStream(
                typeStream,
                TypeRecordCounts,
                (typeIndex, kind, offset, length) =>
                {
                    if (kind.IsUdt())
                    {
                        AddTypeDefinition(typeIndex, typeStream!.Read(offset + 2, length), kept);
                    }
                },
                fraction => progress.Report(Lang.Tr("Reading types (TPI)..."), fraction)),
            () => ipi = new TypeStream(
                idStream,
                IdRecordCounts,
                (_, kind, offset, length) =>
                {
                    if (kind is LF_UDT_SRC_LINE or LF_UDT_MOD_SRC_LINE && length >= 14)
                    {
                        var source = new UdtSourceLineRecord(idStream!.Read(offset + 2, length));
                        sourceLines.Add((source.TypeIndex, offset));
                    }
                }));
        Tpi = tpi!;
        Ipi = ipi!;

        sourceLines.Sort();
        _sourceLineTypes = sourceLines.ConvertAll(line => line.TypeIndex).ToArray();
        _sourceLineRecords = sourceLines.ConvertAll(line => line.RecordOffset).ToArray();
    }

    void AddTypeDefinition(uint typeIndex, ReadOnlySpan<byte> record, Dictionary<ulong, (int Item, int MemberCount)> kept)
    {
        if (!UdtRecord.TryRead(record, out var udt) || udt.IsForwardReference)
        {
            return;
        }

        byte kind = udt.Kind switch
        {
            LF_CLASS => TypeKinds.Class,
            LF_STRUCTURE => TypeKinds.Struct,
            LF_UNION => TypeKinds.Union,
            LF_ENUM => TypeKinds.Enum,
            _ => TypeKinds.Interface,
        };
        if (udt.UniqueName.Length == 0)
        {
            Types.Add(udt.Name, typeIndex, kind);
            return;
        }

        ulong key = Fnv1a.Hash(udt.UniqueName);
        if (kept.TryGetValue(key, out var existing) && Types.NameBytes(existing.Item).SequenceEqual(udt.Name))
        {
            if (udt.MemberCount > existing.MemberCount)
            {
                Types.SetReference(existing.Item, typeIndex);
                kept[key] = (existing.Item, udt.MemberCount);
            }
        }
        else
        {
            kept[key] = (Types.Count, udt.MemberCount);
            Types.Add(udt.Name, typeIndex, kind);
        }
    }
}

internal static class Fnv1a
{
    const ulong OffsetBasis = 14695981039346656037;
    const ulong Prime = 1099511628211;

    public static ulong Hash(ReadOnlySpan<byte> data, ulong seed = OffsetBasis)
    {
        foreach (byte value in data)
        {
            seed = (seed ^ value) * Prime;
        }
        return seed;
    }
}
