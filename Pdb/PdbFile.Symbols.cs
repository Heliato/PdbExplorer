using System.Text;
using static PdbExplorer.Pdb.ByteReading;
using static PdbExplorer.Pdb.LeafKind;
using static PdbExplorer.Pdb.SymbolKind;

namespace PdbExplorer.Pdb;

/// <summary>Kinds stored with the items of the symbol tables.</summary>
internal static class SymbolKinds
{
    public const byte GlobalFunction = 0;
    public const byte StaticFunction = 1;

    public const byte GlobalVariable = 0;
    public const byte StaticVariable = 1;
    public const byte ThreadLocalVariable = 2;

    public const byte PublicData = 0;
    public const byte PublicFunction = 1;
}

// Global symbols, ids, names and addresses.
internal sealed unsafe partial class PdbFile
{
    const ushort MachineAmd64 = 0x8664;

    /// <summary>Number of global symbol records of each kind, indexed by the kind value.</summary>
    public int[] SymbolRecordCounts { get; } = new int[0x10000];

    /// <summary>A cursor over the symbols of a module, starting at <paramref name="offset"/> (4 skips the stream signature).</summary>
    public SymbolCursor ModuleSymbols(Module module, uint offset = 4)
    {
        var stream = GetStream(module.StreamIndex);
        return new SymbolCursor(stream, offset, Math.Min(module.SymbolsSize, stream.Size));
    }

    /// <summary>A cursor positioned on a record of the global symbol stream.</summary>
    public SymbolCursor GlobalSymbol(uint offset)
    {
        var stream = GetStream(SymbolRecordStream);
        return new SymbolCursor(stream, offset, stream.Size);
    }

    /// <summary>Module index and symbol offset of the procedure an S_PROCREF record points to.</summary>
    public (uint Module, uint Offset) ResolveProcedureReference(uint symbolOffset)
    {
        var record = GetStream(SymbolRecordStream).Read(symbolOffset + 2, 12);
        var reference = new ProcedureReferenceSymbol(record);
        return ((uint)(reference.Module - 1), reference.SymbolOffset);
    }

    /// <summary>Offsets of every procedure record of a module.</summary>
    public List<uint> GetProcedureOffsets(Module module)
    {
        var offsets = new List<uint>();
        if (!module.HasSymbols)
        {
            return offsets;
        }

        var cursor = ModuleSymbols(module);
        while (cursor.MoveNext())
        {
            if (!cursor.Kind.IsProcedure())
            {
                continue;
            }
            offsets.Add(cursor.RecordOffset);
            SkipTo(ref cursor, new ProcedureSymbol(cursor.Record).End);
        }
        return offsets;
    }

    /// <summary>Jumps to the record that closes a scope, ignoring an offset that would go backwards or out of range.</summary>
    public static void SkipTo(ref SymbolCursor cursor, uint scopeEnd)
    {
        if (scopeEnd >= cursor.Position && scopeEnd < cursor.End)
        {
            cursor.Position = scopeEnd;
        }
    }

    /// <summary>Type index of a procedure symbol: read directly, or through the function id for the _ID variants.</summary>
    public uint ProcedureType(SymbolKind kind, ProcedureSymbol procedure) =>
        kind.HasIdType() ? FunctionTypeOfId(procedure.TypeOrId) : procedure.TypeOrId;

    public uint FunctionTypeOfId(uint id)
    {
        if (!Ipi.Contains(id))
        {
            return 0;
        }
        var record = Ipi.Record(id);
        bool isFunctionId = (LeafKind)U16(record, 0) is LF_FUNC_ID or LF_MFUNC_ID;
        return isFunctionId ? new FunctionIdRecord(record).FunctionType : 0;
    }

    /// <summary>Readable name of an IPI record: a qualified function name, or a string.</summary>
    public string IdName(uint id, int depth = 0)
    {
        const int maxDepth = 16;
        if (!Ipi.Contains(id) || depth > maxDepth)
        {
            return $"<id 0x{id:X}>";
        }

        var record = Ipi.Record(id);
        var kind = (LeafKind)U16(record, 0);
        switch (kind)
        {
            case LF_FUNC_ID:
                {
                    var function = new FunctionIdRecord(record);
                    string name = Utf8(function.Name);
                    return function.Scope != 0 ? IdName(function.Scope, depth + 1) + "::" + name : name;
                }

            case LF_MFUNC_ID:
                {
                    var method = new FunctionIdRecord(record);
                    return TypeName(method.Scope) + "::" + Utf8(method.Name);
                }

            case LF_STRING_ID:
                return StringIdText(record, depth);

            default:
                return $"<{kind.DisplayName()}>";
        }
    }

    /// <summary>LF_STRING_ID: long strings are split, the leading pieces being listed in an LF_SUBSTR_LIST.</summary>
    string StringIdText(ReadOnlySpan<byte> record, int depth)
    {
        uint substringList = U32(record, 2);
        string text = Utf8(CString(record, 6));
        if (substringList == 0 || !Ipi.Contains(substringList))
        {
            return text;
        }

        var list = Ipi.Record(substringList);
        var result = new StringBuilder();
        if ((LeafKind)U16(list, 0) == LF_SUBSTR_LIST)
        {
            int count = (int)Math.Min(U32(list, 2), (uint)(list.Length - 6) / 4);
            for (int i = 0; i < count; i++)
            {
                result.Append(IdName(U32(list, 6 + 4 * i), depth + 1));
            }
        }
        return result.Append(text).ToString();
    }

    /// <summary>A string of the /names table (source file names are stored there).</summary>
    public string GetName(uint offset)
    {
        const uint headerSize = 12;
        if (!_namedStreams.TryGetValue("/names", out int index)
            || FindStream(index) is not { } stream
            || (ulong)headerSize + offset >= stream.Size)
        {
            return $"<names+0x{offset:X}>";
        }
        return Utf8(stream.ReadCString(headerSize + offset));
    }

    /// <summary>Relative virtual address of a section:offset pair, or 0 when the section is unknown.</summary>
    public uint Rva(ushort section, uint offset) =>
        IsKnownSection(section) ? Sections[section - 1].VirtualAddress + offset : 0;

    public string FormatAddress(ushort section, uint offset) =>
        IsKnownSection(section)
            ? $"RVA 0x{Rva(section, offset):X8}  -  {Sections[section - 1].Name} ({section:X4}:{offset:X8})"
            : $"{section:X4}:{offset:X8}";

    public string RegisterName(ushort register) =>
        Machine == MachineAmd64 ? Registers.Amd64(register) : "reg" + register;

    bool IsKnownSection(ushort section) => section >= 1 && section <= Sections.Length;

    /// <summary>Walks the global symbol stream once and fills the function, variable, constant, typedef and public tables.</summary>
    void IndexGlobalSymbols(LoadProgress progress)
    {
        var stream = FindStream(SymbolRecordStream);
        if (stream == null)
        {
            return;
        }

        uint offset = 0;
        int count = 0;
        while (offset + 4 <= stream.Size)
        {
            var header = stream.Read(offset, 4);
            int length = U16(header, 0);
            var kind = (SymbolKind)U16(header, 2);
            if (length < 2 || (ulong)offset + 2 + (uint)length > stream.Size)
            {
                break;
            }

            SymbolRecordCounts[(ushort)kind]++;
            IndexGlobalSymbol(kind, offset, stream, length);

            offset += 2 + (uint)length;
            count++;
            if ((count & 0x7FFFF) == 0)
            {
                progress.Report(Lang.Tr("Reading symbols..."), (double)offset / stream.Size);
            }
        }
    }

    void IndexGlobalSymbol(SymbolKind kind, uint offset, MsfStream stream, int length)
    {
        switch (kind)
        {
            case S_PUB32:
                {
                    var symbol = new PublicSymbol(stream.Read(offset + 2, length));
                    Publics.Add(symbol.Name, offset, symbol.IsFunction ? SymbolKinds.PublicFunction : SymbolKinds.PublicData);
                    break;
                }

            case S_GDATA32:
            case S_LDATA32:
            case S_GTHREAD32:
            case S_LTHREAD32:
                {
                    byte variableKind = kind switch
                    {
                        S_GDATA32 => SymbolKinds.GlobalVariable,
                        S_LDATA32 => SymbolKinds.StaticVariable,
                        _ => SymbolKinds.ThreadLocalVariable,
                    };
                    GlobalVariables.Add(new DataSymbol(stream.Read(offset + 2, length)).Name, offset, variableKind);
                    break;
                }

            case S_CONSTANT:
                Constants.Add(new ConstantSymbol(stream.Read(offset + 2, length)).Name, offset);
                break;

            case S_UDT:
                {
                    // Every class, struct and enum also gets an S_UDT with its own name: only real aliases are typedefs.
                    var symbol = new TypedefSymbol(stream.Read(offset + 2, length));
                    bool namesItsOwnType = Tpi.Contains(symbol.TypeIndex)
                        && UdtRecord.TryRead(Tpi.Record(symbol.TypeIndex), out var udt)
                        && udt.Name.SequenceEqual(symbol.Name);
                    if (!namesItsOwnType)
                    {
                        Typedefs.Add(symbol.Name, offset);
                    }
                    break;
                }

            case S_PROCREF:
            case S_LPROCREF:
                {
                    var reference = new ProcedureReferenceSymbol(stream.Read(offset + 2, length));
                    Functions.Add(reference.Name, offset, kind == S_PROCREF ? SymbolKinds.GlobalFunction : SymbolKinds.StaticFunction);
                    break;
                }
        }
    }
}
