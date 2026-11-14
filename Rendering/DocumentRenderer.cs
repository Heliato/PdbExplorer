using PdbExplorer.Pdb;

namespace PdbExplorer.Rendering;

/// <summary>
/// Turns an item of the PDB into a document: a list of properties and, unless only those were asked for,
/// a pseudo-C++ view. The other parts of this class (one per kind of item) are in DocumentRenderer.*.cs.
/// </summary>
internal sealed partial class DocumentRenderer
{
    const string Indent = "    ";

    readonly PdbFile _pdb;
    readonly Document _document;
    readonly CodeWriter _out = new();
    readonly bool _propertiesOnly;

    DocumentRenderer(PdbFile pdb, Target target, bool propertiesOnly)
    {
        _pdb = pdb;
        _document = new Document { Target = target };
        _propertiesOnly = propertiesOnly;
    }

    public static Document Render(PdbFile pdb, Target target) => Run(pdb, target, propertiesOnly: false);

    /// <summary>Only the properties: cheap enough to run on every selection change.</summary>
    public static Document Describe(PdbFile pdb, Target target) => Run(pdb, target, propertiesOnly: true);

    static Document Run(PdbFile pdb, Target target, bool propertiesOnly)
    {
        var renderer = new DocumentRenderer(pdb, target, propertiesOnly);
        try
        {
            renderer.RenderTarget(target);
        }
        catch (Exception error) when (error is InvalidDataException or IndexOutOfRangeException or ArgumentException or OverflowException)
        {
            // The record is malformed or uses something this reader does not know: show what was decoded so far.
            renderer.Property("Decoding error", error.Message);
            renderer._out.Line().Comment("// " + Lang.Tr("Decoding error") + ": " + error.Message).Line();
        }
        renderer._out.CopyTo(renderer._document);
        return renderer._document;
    }

    void RenderTarget(Target target)
    {
        switch (target.Kind)
        {
            case ItemKind.Type:
                RenderType(target.Id);
                break;
            case ItemKind.Function:
                RenderFunction(target.Id, target.Offset);
                break;
            case ItemKind.GlobalVariable:
            case ItemKind.Constant:
            case ItemKind.Typedef:
            case ItemKind.PublicSymbol:
                RenderGlobalSymbol(target.Id);
                break;
            case ItemKind.Module:
                RenderModule(target.Id);
                break;
            case ItemKind.SourceFile:
                RenderSourceFile(target.Id);
                break;
            case ItemKind.Section:
                RenderSection(target.Id);
                break;
            case ItemKind.Stream:
                RenderStream(target.Id);
                break;
            default:
                RenderInfo();
                break;
        }
    }

    void Property(string name, string value) => _document.Properties.Add(new(Lang.Tr(name), value));

    /// <summary>Writes the properties as a comment block at the top of the document, even if text was already written.</summary>
    void WriteHeader()
    {
        _out.Prepend(() =>
        {
            var properties = _document.Properties;
            int nameWidth = properties.Count == 0 ? 0 : properties.Max(property => property.Key.Length);
            foreach (var (name, value) in properties)
            {
                _out.Comment($"// {name.PadRight(nameWidth)} : {value}").Line();
            }
            _out.Line();
        });
    }

    void WriteSectionTitle(string title) => _out.Comment($"// ── {title} ──").Line();

    /// <summary>A piece of a type name: a link to the user-defined type it is built on, or a keyword for built-in types.</summary>
    CodeWriter WriteTypeText(string text, uint typeIndex)
    {
        uint udt = _pdb.UnderlyingUdt(typeIndex);
        return udt != 0
            ? _out.Write(text, TokenKind.Type, new Target(ItemKind.Type, udt))
            : _out.Write(text, TokenKind.Keyword);
    }

    CodeWriter WriteType(uint typeIndex) => WriteTypeText(_pdb.TypeName(typeIndex), typeIndex);

    /// <summary>Type and name in declaration order: the name goes inside the type for arrays and function pointers.</summary>
    CodeWriter WriteDeclaration(uint typeIndex, string name, TokenKind nameKind)
    {
        var parts = _pdb.SplitType(typeIndex);
        WriteTypeText(parts.Prefix, typeIndex);
        return _out.Write(parts.NameIsEnclosed ? "" : " ").Write(name, nameKind).Write(parts.Suffix);
    }

    void WriteSignature(FunctionSignature? signature, string name, List<string>? parameterNames, Target? link = null, string? search = null)
    {
        if (signature == null)
        {
            _out.Write(name, TokenKind.Function, link, search).Write("()");
            return;
        }

        bool isDestructor = name.Contains('~') && !name.Contains("operator");
        if (!signature.IsConstructor && !isDestructor)
        {
            WriteType(signature.ReturnType).Write(" ");
        }

        _out.Write(name, TokenKind.Function, link, search).Write("(");
        for (int i = 0; i < signature.Parameters.Length; i++)
        {
            uint type = signature.Parameters[i];
            if (i > 0)
            {
                _out.Write(", ");
            }

            if (type == 0)
            {
                _out.Write("...");
            }
            else if (parameterNames != null && i < parameterNames.Count)
            {
                WriteDeclaration(type, parameterNames[i], TokenKind.Parameter);
            }
            else
            {
                WriteType(type);
            }
        }
        _out.Write(")");
        if (signature.IsConst)
        {
            _out.Keyword(" const");
        }
    }

    static string FormatSize(long bytes) => Lang.Format("0x{0:X} ({1:N0} bytes)", bytes, bytes);

    static string SignedHex(int value) => value < 0 ? $"-0x{-(long)value:X}" : $"+0x{value:X}";

    static string AccessKeyword(int access) => access switch
    {
        Member.Private => "private",
        Member.Protected => "protected",
        _ => "public",
    };

    /// <summary>Names of the flags set in <paramref name="value"/>, translated and comma-separated.</summary>
    static string DescribeFlags(uint value, params (uint Bit, string Name)[] flags) =>
        string.Join(", ", flags.Where(flag => (value & flag.Bit) != 0).Select(flag => Lang.Tr(flag.Name)));

    /// <summary>
    /// Width of an aligned column: the widest entry that fits in <paramref name="limit"/>.
    /// The few entries beyond it overflow instead of pushing the whole column to the right.
    /// </summary>
    static int ColumnWidth(IEnumerable<int> widths, int limit) =>
        widths.Where(width => width <= limit).DefaultIfEmpty(0).Max();
}
