using System.Text;
using PdbExplorer.Pdb;
using static PdbExplorer.Pdb.ByteReading;
using static PdbExplorer.Pdb.SymbolKind;

namespace PdbExplorer.Rendering;

// Modules, source files, sections and raw streams.
internal sealed partial class DocumentRenderer
{
    const int MaxModuleSymbols = 100_000;
    const int HexDumpLimit = 0x4000;

    /// <summary>Indexed by the language byte of S_COMPILE3.</summary>
    static readonly string[] Languages =
    [
        "C", "C++", "Fortran", "MASM", "Pascal", "Basic", "COBOL", "LINK", "CVTRES", "CVTPGD", "C#", "VB",
        "ILASM", "Java", "JScript", "MSIL", "HLSL", "Objective-C", "Objective-C++", "Swift", "ALIASOBJ", "Rust", "Go",
    ];

    void RenderModule(uint moduleIndex)
    {
        if (moduleIndex >= _pdb.Modules.Count)
        {
            throw new InvalidDataException($"Module #{moduleIndex} does not exist.");
        }

        var module = _pdb.Modules[(int)moduleIndex];
        _document.Title = Path.GetFileName(module.Name);
        Property("Module", $"#{moduleIndex} {module.Name}");
        if (module.ObjectName != module.Name)
        {
            Property("Library / object", module.ObjectName);
        }
        Property("Stream", module.HasSymbols ? module.StreamIndex.ToString() : Lang.Tr("none (no symbols)"));
        Property("Symbols", FormatSize(module.SymbolsSize));
        Property("Line tables", FormatSize(module.C13LinesSize));
        Property("Source files", module.SourceFileCount.ToString());
        if (module.Section != 0)
        {
            Property("First contribution", $"{_pdb.FormatAddress(module.Section, module.Offset)}, {FormatSize(module.Size)}");
        }
        if (_propertiesOnly)
        {
            return;
        }

        WriteSectionTitle($"{Lang.Tr("Source files")} ({module.SourceFileCount})");
        foreach (string file in _pdb.GetSourceFiles(module))
        {
            _out.Comment("//   " + file).Line();
        }
        _out.Line();
        WriteSectionTitle(Lang.Tr("Symbols"));
        if (module.HasSymbols)
        {
            WriteModuleSymbols(module);
        }

        // The compiler records met while listing the symbols became properties: the header is written last.
        WriteHeader();
    }

    void WriteModuleSymbols(Module module)
    {
        var cursor = _pdb.ModuleSymbols(module);
        int count = 0;
        while (cursor.MoveNext())
        {
            count++;
            if (count > MaxModuleSymbols)
            {
                _out.Comment("// ... " + Lang.Tr("list truncated (more than 100,000 symbols)")).Line();
                break;
            }
            WriteModuleSymbol(module, ref cursor);
        }
    }

    void WriteModuleSymbol(Module module, ref SymbolCursor cursor)
    {
        var record = cursor.Record;
        var kind = cursor.Kind;
        switch (kind)
        {
            case S_OBJNAME:
                Property("Object file", Utf8(CString(record, 6)));
                break;

            case S_COMPILE3:
                {
                    var compiler = new CompilerSymbol(record);
                    string language = compiler.Language < Languages.Length ? Languages[compiler.Language] : "?";
                    Property("Compiler", $"{Utf8(compiler.Name)} - {language} - backend {compiler.BackendVersion}");
                    break;
                }

            case S_ENVBLOCK:
                AddEnvironmentProperties(record);
                break;

            case S_THUNK32:
                {
                    var thunk = new ThunkSymbol(record);
                    _out.Comment($"// thunk {Utf8(thunk.Name)} - {_pdb.FormatAddress(thunk.Section, thunk.Offset)}").Line();
                    PdbFile.SkipTo(ref cursor, thunk.End);
                    break;
                }

            case S_GDATA32:
            case S_LDATA32:
            case S_GTHREAD32:
            case S_LTHREAD32:
                {
                    var data = new DataSymbol(record);
                    if (kind.IsStaticData())
                    {
                        _out.Keyword("static ");
                    }
                    if (kind.IsThreadLocalData())
                    {
                        _out.Keyword("thread_local ");
                    }
                    WriteDeclaration(data.TypeIndex, Utf8(data.Name), TokenKind.Member).Write(";")
                        .Comment($"   // RVA 0x{_pdb.Rva(data.Section, data.Offset):X8}").Line();
                    break;
                }

            case S_UDT:
                {
                    var typedef = new TypedefSymbol(record);
                    _out.Keyword("typedef ");
                    WriteDeclaration(typedef.TypeIndex, Utf8(typedef.Name), TokenKind.Type).Write(";").Line();
                    break;
                }

            case S_CONSTANT:
                {
                    var constant = new ConstantSymbol(record);
                    _out.Keyword("const ");
                    WriteDeclaration(constant.TypeIndex, Utf8(constant.Name), TokenKind.Enumerator)
                        .Write(" = ").Number(constant.Value.ToString()).Write(";").Line();
                    break;
                }

            case S_UNAMESPACE:
                _out.Keyword("using namespace ").Write(Utf8(CString(record, 2))).Write(";").Line();
                break;

            case S_SECTION:
                {
                    var section = new ImageSectionSymbol(record);
                    _out.Comment($"// section {Utf8(section.Name)} - RVA 0x{section.Rva:X8}, {FormatSize(section.Size)}").Line();
                    break;
                }

            case S_COFFGROUP:
                {
                    var group = new CoffGroupSymbol(record);
                    string address = _pdb.FormatAddress(group.Section, group.Offset);
                    _out.Comment($"// {Lang.Tr("COFF group")} {Utf8(group.Name)} - {address}, {FormatSize(group.Size)}").Line();
                    break;
                }

            case S_EXPORT:
                _out.Comment($"// export {Utf8(CString(record, 6))} (ordinal {U16(record, 2)})").Line();
                break;

            case S_END:
            case S_PROC_ID_END:
            case S_BUILDINFO:
                break;

            default:
                if (kind.IsProcedure())
                {
                    WriteModuleProcedure(module, ref cursor);
                }
                else
                {
                    _out.Comment("// " + kind.DisplayName()).Line();
                }
                break;
        }
    }

    /// <summary>One line per function: its declaration, linked to the function's own document.</summary>
    void WriteModuleProcedure(Module module, ref SymbolCursor cursor)
    {
        var procedure = new ProcedureSymbol(cursor.Record);
        var signature = _pdb.GetSignature(_pdb.ProcedureType(cursor.Kind, procedure));
        var link = new Target(ItemKind.Function, (uint)module.Index, cursor.RecordOffset);

        if (!cursor.Kind.IsGlobalProcedure())
        {
            _out.Keyword("static ");
        }
        WriteSignature(signature, Utf8(procedure.Name), ParameterNames.InScope(signature, cursor), link);

        uint rva = _pdb.Rva(procedure.Section, procedure.Offset);
        _out.Write(";").Comment($"   // RVA 0x{rva:X8}, {FormatSize(procedure.CodeSize)}").Line();

        // Locals and nested scopes belong to the function's document, not to this list.
        PdbFile.SkipTo(ref cursor, procedure.End);
    }

    /// <summary>S_ENVBLOCK: a flag byte, then zero-terminated name / value pairs (cwd, compiler, command line...).</summary>
    void AddEnvironmentProperties(ReadOnlySpan<byte> record)
    {
        int offset = 3;
        while (offset < record.Length)
        {
            var name = CString(record, offset);
            if (name.Length == 0)
            {
                break;
            }
            offset += name.Length + 1;

            var value = offset < record.Length ? CString(record, offset) : default;
            offset += value.Length + 1;
            Property("env · " + Utf8(name), Utf8(value));
        }
    }

    void RenderSourceFile(uint nameOffset)
    {
        string path = _pdb.SourceFileName(nameOffset);
        _document.Title = Path.GetFileName(path);
        Property("Source file", path);
        if (_propertiesOnly)
        {
            return;
        }

        var modules = _pdb.FindModulesUsing(nameOffset);
        Property("Used by", Lang.Format("{0:N0} module(s)", modules.Count));
        WriteHeader();
        _out.Comment("// " + Lang.Tr("Modules compiled with this file (Ctrl+Click to open):")).Line();
        foreach (int index in modules)
        {
            _out.Write(Indent).Number($"#{index,-6}")
                .Write(_pdb.Modules[index].Name, TokenKind.Function, new Target(ItemKind.Module, (uint)index)).Line();
        }
    }

    void RenderSection(uint index)
    {
        var section = _pdb.Sections[index];
        _document.Title = section.Name;
        Property("Section", $"{index + 1:X4}  {section.Name}");
        Property("RVA", $"0x{section.VirtualAddress:X8}");
        Property("Virtual size", FormatSize(section.VirtualSize));
        Property("Size in file", FormatSize(section.RawSize));
        Property("File offset", $"0x{section.RawPointer:X}");

        string characteristics = DescribeFlags(
            section.Characteristics,
            (0x00000020, "code"),
            (0x00000040, "initialized data"),
            (0x00000080, "uninitialized data"),
            (0x02000000, "discardable"),
            (0x20000000, "executable"),
            (0x40000000, "readable"),
            (0x80000000, "writable"));
        Property("Characteristics", $"0x{section.Characteristics:X8} - {characteristics}");
        if (!_propertiesOnly)
        {
            WriteHeader();
        }
    }

    void RenderStream(uint index)
    {
        var stream = _pdb.GetStream((int)index);
        _document.Title = Lang.Format("Stream {0}", index);
        Property("Stream", $"{index} - {_pdb.StreamNames[index]}");
        Property("Size", FormatSize(stream.Size));
        Property("Pages", Lang.Format("{0:N0} x {1} bytes", stream.PageCount, _pdb.PageSize));
        if (_propertiesOnly)
        {
            return;
        }

        WriteHeader();
        var data = stream.Read(0, (int)Math.Min(stream.Size, HexDumpLimit));
        string truncated = stream.Size > data.Length ? " (" + Lang.Format("first {0:N0} bytes", data.Length) + ")" : "";
        _out.Comment("// " + Lang.Tr("Hex view") + truncated).Line();

        const int bytesPerRow = 16;
        for (int offset = 0; offset < data.Length; offset += bytesPerRow)
        {
            var row = data.Slice(offset, Math.Min(bytesPerRow, data.Length - offset));
            var hex = new StringBuilder();
            var text = new StringBuilder();
            foreach (byte value in row)
            {
                hex.Append(value.ToString("X2")).Append(' ');
                text.Append(value is >= 32 and < 127 ? (char)value : '.');
            }
            _out.Number($"{offset:X8}  ").Write(hex.ToString().PadRight(50)).Comment(text.ToString()).Line();
        }
    }
}
