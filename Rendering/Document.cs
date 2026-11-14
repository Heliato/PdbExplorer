using System.Text;
using PdbExplorer.Pdb;

namespace PdbExplorer.Rendering;

internal enum ItemKind
{
    Info,
    Type,
    Function,
    GlobalVariable,
    Constant,
    Typedef,
    PublicSymbol,
    Module,
    SourceFile,
    Section,
    Stream,
}

/// <summary>
/// Something that can be opened as a document. <see cref="Id"/> is a type index, a symbol record offset,
/// a module index... depending on the kind. A function needs two values: its module (<see cref="Id"/>)
/// and the offset of its symbol in that module (<see cref="Offset"/>).
/// </summary>
internal readonly record struct Target(ItemKind Kind, uint Id = 0, uint Offset = 0)
{
    public static Target Function(PdbFile pdb, uint procedureReferenceOffset)
    {
        var (module, offset) = pdb.ResolveProcedureReference(procedureReferenceOffset);
        return new Target(ItemKind.Function, module, offset);
    }
}

/// <summary>Syntax classes. The numeric values index the color palette of the editor.</summary>
internal enum TokenKind : byte
{
    Text,
    Keyword,
    Type,
    Comment,
    Number,
    Function,
    Member,
    Enumerator,
    Parameter,
}

/// <summary>
/// A colored span of a document. It can also be a link: to another document (<see cref="Link"/>),
/// or to a search for a function by name (<see cref="Search"/>) when its exact symbol is not known.
/// </summary>
internal readonly record struct Segment(int Start, int Length, TokenKind Kind, Target? Link = null, string? Search = null)
{
    public int End => Start + Length;
}

internal sealed class Document
{
    public required Target Target { get; init; }
    public string Title { get; set; } = "";
    public string Text { get; set; } = "";

    /// <summary>Sorted by position, never overlapping.</summary>
    public List<Segment> Segments { get; } = [];

    /// <summary>Name / value pairs shown in the Properties panel and as the header comment of the document.</summary>
    public List<KeyValuePair<string, string>> Properties { get; } = [];
}

/// <summary>Builds the text of a document together with its colored segments.</summary>
internal sealed class CodeWriter
{
    readonly StringBuilder _text = new();
    readonly List<Segment> _segments = [];

    public CodeWriter Write(string text)
    {
        _text.Append(text);
        return this;
    }

    public CodeWriter Write(string text, TokenKind kind, Target? link = null, string? search = null)
    {
        if (text.Length > 0)
        {
            _segments.Add(new Segment(_text.Length, text.Length, kind, link, search));
        }
        _text.Append(text);
        return this;
    }

    public CodeWriter Keyword(string text) => Write(text, TokenKind.Keyword);

    public CodeWriter Comment(string text) => Write(text, TokenKind.Comment);

    public CodeWriter Number(string text) => Write(text, TokenKind.Number);

    public CodeWriter Spaces(int count) => Write(new string(' ', count));

    public CodeWriter Line()
    {
        _text.Append('\n');
        return this;
    }

    /// <summary>Removes the last line break when the text ends with an empty line.</summary>
    public void TrimEmptyLine()
    {
        if (_text.Length >= 2 && _text[^1] == '\n' && _text[^2] == '\n')
        {
            _text.Length--;
        }
    }

    /// <summary>Runs <paramref name="write"/> and places its output before everything written so far.</summary>
    public void Prepend(Action write)
    {
        string body = _text.ToString();
        Segment[] bodySegments = [.. _segments];
        _text.Clear();
        _segments.Clear();

        write();

        int shift = _text.Length;
        _text.Append(body);
        foreach (var segment in bodySegments)
        {
            _segments.Add(segment with { Start = segment.Start + shift });
        }
    }

    public void CopyTo(Document document)
    {
        document.Text = _text.ToString();
        document.Segments.AddRange(_segments);
    }
}
