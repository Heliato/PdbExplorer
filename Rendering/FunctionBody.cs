using PdbExplorer.Pdb;
using static PdbExplorer.Pdb.ByteReading;
using static PdbExplorer.Pdb.SymbolKind;

namespace PdbExplorer.Rendering;

/// <summary>A parameter, local variable, static, typedef or constant declared in a function.</summary>
internal sealed class Variable
{
    public required uint Type { get; init; }
    public required string Name { get; init; }

    /// <summary>"static ", "typedef " or "const ", written before the type.</summary>
    public string Prefix { get; init; } = "";

    /// <summary>Written after the name: the value of a constant.</summary>
    public string Suffix { get; init; } = "";
    public TokenKind NameKind { get; init; } = TokenKind.Member;
    public bool IsParameter { get; set; }
    public string Note { get; set; } = "";

    /// <summary>Registers and stack slots the variable lives in.</summary>
    public List<string> Locations { get; } = [];
}

/// <summary>The function itself, one of its lexical blocks, or a call that was inlined into it.</summary>
internal sealed class Scope
{
    string? _signature;

    public string Title { get; init; } = "";
    public bool IsInlinedCall { get; init; }

    /// <summary>Where the scope's code starts, relative to the function, when the PDB says so.</summary>
    public uint? CodeOffset { get; init; }
    public List<Variable> Variables { get; } = [];
    public List<Scope> Children { get; } = [];

    /// <summary>Functions called from this scope.</summary>
    public List<string> Calls { get; } = [];
    public List<string> Notes { get; } = [];

    /// <summary>Two scopes with the same signature render identically, so neighbours can be folded into one line.</summary>
    public string Signature => _signature ??=
        Title + "@" + CodeOffset + ">" + string.Join(",", Calls)
        + "|" + string.Join(",", Variables.Select(variable => variable.Type + variable.Name + string.Concat(variable.Locations)))
        + "|" + string.Join(",", Notes)
        + "{" + string.Concat(Children.Select(child => child.Signature)) + "}";
}

/// <summary>Everything the symbols say about the inside of a function, as a tree of scopes.</summary>
internal sealed class FunctionBody
{
    readonly PdbFile _pdb;
    readonly uint _functionOffset;
    readonly Stack<Scope> _open = new();

    FunctionBody(PdbFile pdb, uint functionOffset)
    {
        _pdb = pdb;
        _functionOffset = functionOffset;
        _open.Push(Root);
    }

    public Scope Root { get; } = new();
    public List<string> IndirectCalls { get; } = [];
    public List<string> HeapAllocations { get; } = [];
    public List<string> Callers { get; } = [];
    public List<(uint Type, string Name)> LocalTypes { get; } = [];

    /// <summary>Descriptions of the stack frame (one per S_FRAMEPROC record).</summary>
    public List<string> Frames { get; } = [];

    /// <param name="cursor">Positioned on the procedure record.</param>
    /// <param name="functionOffset">Section offset of the function, to express code offsets relative to it.</param>
    public static FunctionBody Read(PdbFile pdb, SymbolCursor cursor, uint functionOffset)
    {
        var body = new FunctionBody(pdb, functionOffset);
        while (body._open.Count > 0 && cursor.MoveNext())
        {
            body.Add(ref cursor);
        }
        return body;
    }

    void Add(ref SymbolCursor cursor)
    {
        var scope = _open.Peek();
        var record = cursor.Record;
        switch (cursor.Kind)
        {
            case S_END:
            case S_INLINESITE_END:
            case S_PROC_ID_END:
                _open.Pop();
                break;

            case S_BLOCK32:
                {
                    var block = new BlockSymbol(record);
                    Open(new Scope
                    {
                        Title = Lang.Format("block ({0:N0} bytes)", block.CodeSize),
                        CodeOffset = block.Offset - _functionOffset,
                    });
                    break;
                }

            case S_INLINESITE:
            case S_INLINESITE2:
                {
                    var site = new InlineSiteSymbol(record, cursor.Kind);
                    Open(new Scope { Title = _pdb.IdName(site.Inlinee), IsInlinedCall = true, CodeOffset = site.CodeOffset });
                    break;
                }

            case S_THUNK32:
            case S_SEPCODE:
            case S_WITH32:
                Open(new Scope { Title = cursor.Kind.DisplayName() });
                break;

            case S_LOCAL:
                scope.Variables.Add(ReadLocal(ref cursor));
                break;

            case S_REGREL32:
                {
                    var variable = new RegisterRelativeSymbol(record);
                    string location = $"[{_pdb.RegisterName(variable.Register)}{SignedHex(variable.Offset)}]";
                    AddLocation(scope, variable.TypeIndex, Utf8(variable.Name), location);
                    break;
                }

            case S_BPREL32:
                {
                    var variable = new FrameRelativeSymbol(record);
                    AddLocation(scope, variable.TypeIndex, Utf8(variable.Name), $"[rbp{SignedHex(variable.Offset)}]");
                    break;
                }

            case S_REGISTER:
                {
                    var variable = new RegisterSymbol(record);
                    AddLocation(scope, variable.TypeIndex, Utf8(variable.Name), _pdb.RegisterName(variable.Register));
                    break;
                }

            case S_LDATA32:
            case S_GDATA32:
            case S_LTHREAD32:
            case S_GTHREAD32:
                {
                    var data = new DataSymbol(record);
                    scope.Variables.Add(new Variable
                    {
                        Type = data.TypeIndex,
                        Name = Utf8(data.Name),
                        Prefix = "static ",
                        Note = _pdb.FormatAddress(data.Section, data.Offset),
                    });
                    break;
                }

            case S_FILESTATIC:
                {
                    var data = new FileStaticSymbol(record);
                    scope.Variables.Add(new Variable
                    {
                        Type = data.TypeIndex,
                        Name = Utf8(data.Name),
                        Prefix = "static ",
                        Note = Lang.Tr("file static"),
                    });
                    break;
                }

            case S_UDT:
                {
                    // "typedef X X" is how a type declared inside the function (a lambda, a local struct) shows up.
                    var typedef = new TypedefSymbol(record);
                    string name = Utf8(typedef.Name);
                    if (_pdb.TypeName(typedef.TypeIndex) == name)
                    {
                        LocalTypes.Add((typedef.TypeIndex, name));
                    }
                    else
                    {
                        scope.Variables.Add(new Variable { Type = typedef.TypeIndex, Name = name, Prefix = "typedef ", NameKind = TokenKind.Type });
                    }
                    break;
                }

            case S_CONSTANT:
                {
                    var constant = new ConstantSymbol(record);
                    scope.Variables.Add(new Variable
                    {
                        Type = constant.TypeIndex,
                        Name = Utf8(constant.Name),
                        Prefix = "const ",
                        Suffix = " = " + constant.Value,
                        NameKind = TokenKind.Enumerator,
                    });
                    break;
                }

            case S_LABEL32:
                {
                    var label = new LabelSymbol(record);
                    scope.Notes.Add(Lang.Format("label {0} at +0x{1:X}", Utf8(label.Name), label.Offset - _functionOffset));
                    break;
                }

            case S_CALLSITEINFO:
                IndirectCalls.Add(DescribeCallSite(new CallSiteSymbol(record)));
                break;

            case S_HEAPALLOCSITE:
                HeapAllocations.Add(DescribeCallSite(new CallSiteSymbol(record)));
                break;

            case S_CALLEES:
            case S_CALLERS:
                {
                    var functions = new FunctionListSymbol(record);
                    var names = cursor.Kind == S_CALLEES ? scope.Calls : Callers;
                    for (int i = 0; i < functions.Count; i++)
                    {
                        names.Add(_pdb.IdName(functions[i]));
                    }
                    break;
                }

            case S_FRAMEPROC:
                {
                    var frame = new FrameSymbol(record);
                    Frames.Add(Lang.Format("frame of 0x{0:X} bytes, saved registers 0x{1:X} bytes", frame.FrameSize, frame.SavedRegistersSize));
                    break;
                }

            case S_FRAMECOOKIE:
                Root.Notes.Add(Lang.Tr("stack security cookie (/GS)"));
                break;

            case S_INLINEES:
                // Redundant: every inlined function already appears as a scope.
                break;

            default:
                scope.Notes.Add(cursor.Kind.DisplayName());
                break;
        }
    }

    void Open(Scope scope)
    {
        _open.Peek().Children.Add(scope);
        _open.Push(scope);
    }

    /// <summary>An S_LOCAL and the S_DEFRANGE_* records after it, which say where the variable lives.</summary>
    Variable ReadLocal(ref SymbolCursor cursor)
    {
        var local = new LocalSymbol(cursor.Record);
        var variable = new Variable { Type = local.TypeIndex, Name = Utf8(local.Name), IsParameter = local.IsParameter };
        bool optimizedAway = local.IsOptimizedAway;

        var ahead = cursor;
        while (ahead.MoveNext() && ahead.Kind.IsDefinitionRange())
        {
            string? location = DescribeLocation(ahead.Kind, ahead.Record);
            if (location != null && !variable.Locations.Contains(location))
            {
                variable.Locations.Add(location);
            }
            cursor = ahead;
        }

        if (variable.Locations.Count == 0 && optimizedAway)
        {
            variable.Note = Lang.Tr("optimized away");
        }
        return variable;
    }

    string? DescribeLocation(SymbolKind kind, ReadOnlySpan<byte> range) => kind switch
    {
        S_DEFRANGE_REGISTER => _pdb.RegisterName(U16(range, 2)),
        S_DEFRANGE_SUBFIELD_REGISTER => $"{_pdb.RegisterName(U16(range, 2))} (field +0x{U32(range, 6) & 0xFFF:X})",
        S_DEFRANGE_FRAMEPOINTER_REL or S_DEFRANGE_FRAMEPOINTER_REL_FULL_SCOPE => $"[frame{SignedHex(I32(range, 2))}]",
        S_DEFRANGE_REGISTER_REL => $"[{_pdb.RegisterName(U16(range, 2))}{SignedHex(I32(range, 6))}]",
        _ => null,
    };

    /// <summary>
    /// The same variable is often described twice (in a register, then in its home slot on the stack):
    /// both locations go on one declaration instead of two.
    /// </summary>
    static void AddLocation(Scope scope, uint type, string name, string location)
    {
        var variable = scope.Variables.Find(known => known.Name == name && known.Type == type && known.Prefix == "");
        if (variable == null)
        {
            variable = new Variable { Type = type, Name = name };
            scope.Variables.Add(variable);
        }
        if (!variable.Locations.Contains(location))
        {
            variable.Locations.Add(location);
        }
    }

    string DescribeCallSite(CallSiteSymbol site) =>
        $"+0x{site.Offset - _functionOffset:X}".PadRight(10) + _pdb.TypeName(site.TypeIndex);

    static string SignedHex(int value) => value < 0 ? $"-0x{-(long)value:X}" : $"+0x{value:X}";
}
