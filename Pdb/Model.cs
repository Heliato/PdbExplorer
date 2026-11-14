namespace PdbExplorer.Pdb;

/// <summary>A compiled unit (usually one .obj) and where its symbols and line tables are.</summary>
internal sealed class Module
{
    public const ushort NoStream = 0xFFFF;

    public required int Index { get; init; }
    public required string Name { get; init; }
    public required string ObjectName { get; init; }
    public required ushort StreamIndex { get; init; }
    public required uint SymbolsSize { get; init; }
    public required uint C11LinesSize { get; init; }
    public required uint C13LinesSize { get; init; }

    /// <summary>First section contribution: section number (0 if none), offset and size.</summary>
    public required ushort Section { get; init; }
    public required uint Offset { get; init; }
    public required uint Size { get; init; }

    /// <summary>Range of this module's entries in the DBI file-name offset table.</summary>
    public int FirstSourceFile { get; set; }
    public int SourceFileCount { get; set; }

    public bool HasSymbols => StreamIndex != NoStream;
}

/// <summary>A PE section header.</summary>
internal readonly record struct Section(string Name, uint VirtualSize, uint VirtualAddress, uint RawSize, uint RawPointer, uint Characteristics);

/// <summary>
/// One entry of a field list. <see cref="Offset"/> holds the member offset, the enumerator value or the vbptr offset
/// depending on the kind; <see cref="Extra"/> holds a vtable offset or a vbtable index (-1 when absent).
/// </summary>
internal sealed record Member(LeafKind Kind, ushort Attributes, uint Type, long Offset, string Name, long Extra = -1)
{
    public const int Private = 1;
    public const int Protected = 2;
    public const int Public = 3;

    /// <summary>Kind of the placeholder entry added when a field list contains a sub-record this reader does not know.</summary>
    public const LeafKind Undecoded = 0;

    /// <summary>1 private, 2 protected, 3 public, 0 when the entry has no access.</summary>
    public int Access => Attributes & 3;

    public MethodKind MethodKind => (MethodKind)((Attributes >> 2) & 7);

    public bool IsCompilerGenerated => (Attributes & 0x100) != 0;
}

internal enum MethodKind
{
    Vanilla = 0,
    Virtual = 1,              // overrides a virtual of a base class
    Static = 2,
    Friend = 3,
    IntroducingVirtual = 4,   // adds a new vtable slot
    PureVirtual = 5,
    PureIntroducingVirtual = 6,
}

/// <summary>LF_PROCEDURE or LF_MFUNCTION. A zero in <see cref="Parameters"/> is the "..." of a variadic function.</summary>
internal sealed record FunctionSignature(
    uint ReturnType,
    uint ClassType,
    uint[] Parameters,
    bool IsConst,
    bool IsConstructor,
    int ThisAdjustment,
    bool IsMember)
{
    public int NamedParameterCount => Parameters.Count(type => type != 0);
}

/// <summary>A type split around the place where a declared name goes: "int (*" + name + ")[4]".</summary>
/// <param name="NameIsEnclosed">The prefix ends with an opening parenthesis, so no space goes before the name.</param>
internal readonly record struct TypeParts(string Prefix, string Suffix, bool NameIsEnclosed);

/// <summary>Callbacks used while a PDB loads: a line for the log, or the progress of the current step (0 to 1).</summary>
internal sealed record LoadProgress(Action<string> Log, Action<string, double> Report);
