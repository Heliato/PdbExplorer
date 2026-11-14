using System.Runtime.InteropServices;
using System.Text;
using PdbExplorer.Pdb;
using static PdbExplorer.Pdb.ByteReading;
using static PdbExplorer.Pdb.SymbolKind;

namespace PdbExplorer.Rendering;

// Global variables, constants, typedefs and public symbols: one record of the global symbol stream each.
internal sealed partial class DocumentRenderer
{
    void RenderGlobalSymbol(uint symbolOffset)
    {
        var cursor = _pdb.GlobalSymbol(symbolOffset);
        if (!cursor.MoveNext())
        {
            throw new InvalidDataException("Unreadable symbol record.");
        }

        switch (cursor.Kind)
        {
            case S_GDATA32:
            case S_LDATA32:
            case S_GTHREAD32:
            case S_LTHREAD32:
                RenderVariable(cursor.Kind, new DataSymbol(cursor.Record));
                break;
            case S_CONSTANT:
                RenderConstant(new ConstantSymbol(cursor.Record));
                break;
            case S_UDT:
                RenderTypedef(new TypedefSymbol(cursor.Record));
                break;
            case S_PUB32:
                RenderPublic(new PublicSymbol(cursor.Record));
                break;
            default:
                throw new InvalidDataException($"Symbol {cursor.Kind.DisplayName()} is not supported here.");
        }
    }

    void RenderVariable(SymbolKind kind, DataSymbol variable)
    {
        string name = Utf8(variable.Name);
        uint type = variable.TypeIndex;
        bool isStatic = kind.IsStaticData();
        bool isThreadLocal = kind.IsThreadLocalData();

        _document.Title = name;
        Property("Variable", name);
        Property("Kind", Lang.Tr(isStatic ? "static (internal linkage)" : "global") + (isThreadLocal ? ", thread_local" : ""));
        Property("Type", DescribeType(type));
        Property("Size", FormatSize(_pdb.SizeOf(type)));
        Property("Address", _pdb.FormatAddress(variable.Section, variable.Offset));
        if (_propertiesOnly)
        {
            return;
        }

        WriteHeader();
        if (isStatic)
        {
            _out.Keyword("static ");
        }
        if (isThreadLocal)
        {
            _out.Keyword("thread_local ");
        }
        WriteDeclaration(type, name, TokenKind.Member).Write(";").Line();
    }

    void RenderConstant(ConstantSymbol constant)
    {
        string name = Utf8(constant.Name);
        long value = constant.Value;

        _document.Title = name;
        Property("Constant", name);
        Property("Type", DescribeType(constant.TypeIndex));
        Property("Value", $"{value} (0x{value:X})");
        if (_propertiesOnly)
        {
            return;
        }

        WriteHeader();
        _out.Keyword("const ");
        WriteDeclaration(constant.TypeIndex, name, TokenKind.Enumerator)
            .Write(" = ").Number(value.ToString()).Write(";").Comment($"   // 0x{value:X}").Line();
    }

    void RenderTypedef(TypedefSymbol typedef)
    {
        string name = Utf8(typedef.Name);
        uint type = typedef.TypeIndex;

        _document.Title = name;
        Property("Typedef", name);
        Property("Target type", DescribeType(type));
        Property("Size", FormatSize(_pdb.SizeOf(type)));
        if (_propertiesOnly)
        {
            return;
        }

        WriteHeader();
        _out.Keyword("typedef ");
        WriteDeclaration(type, name, TokenKind.Type).Write(";").Line();
    }

    void RenderPublic(PublicSymbol symbol)
    {
        string decorated = Utf8(symbol.Name);
        string declaration = Undecorator.Undecorate(decorated, nameOnly: false);

        _document.Title = Undecorator.Undecorate(decorated, nameOnly: true);
        Property("Public symbol", declaration);
        Property("Decorated name", decorated);
        Property("Kind", Lang.Tr(symbol.IsFunction ? "function" : symbol.IsCode ? "code" : "data"));
        Property("Address", _pdb.FormatAddress(symbol.Section, symbol.Offset));
        if (_propertiesOnly)
        {
            return;
        }

        WriteHeader();
        _out.Write(declaration).Write(";").Line();
    }

    string DescribeType(uint type) => $"{_pdb.TypeName(type)}   (TypeIndex 0x{type:X})";
}

/// <summary>Turns MSVC decorated names ("?Foo@Bar@@QEAAXXZ") back into C++ declarations.</summary>
internal static class Undecorator
{
    const int NameOnly = 0x1000;   // UNDNAME_NAME_ONLY
    static readonly object Lock = new();

    public static string Undecorate(string name, bool nameOnly)
    {
        if (name.Length == 0 || name[0] != '?')
        {
            return name;
        }

        var buffer = new StringBuilder(8192);
        lock (Lock)   // dbghelp is not thread-safe
        {
            int length = UnDecorateSymbolName(name, buffer, buffer.Capacity, nameOnly ? NameOnly : 0);
            return length > 0 ? buffer.ToString() : name;
        }
    }

    [DllImport("dbghelp.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
    static extern int UnDecorateSymbolName(string name, StringBuilder output, int maxLength, int flags);
}
