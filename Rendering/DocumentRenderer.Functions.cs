using PdbExplorer.Pdb;
using static PdbExplorer.Pdb.ByteReading;

namespace PdbExplorer.Rendering;

// Functions: a signature, then what the symbols say about the body, grouped in sections.
internal sealed partial class DocumentRenderer
{
    /// <summary>Width of the offset column in front of the tree of inlined calls.</summary>
    const int OffsetColumnWidth = 10;

    bool _bodyHasSection;

    void RenderFunction(uint moduleIndex, uint symbolOffset)
    {
        if (moduleIndex >= _pdb.Modules.Count)
        {
            throw new InvalidDataException($"Module #{moduleIndex} does not exist.");
        }

        var module = _pdb.Modules[(int)moduleIndex];
        var cursor = _pdb.ModuleSymbols(module, symbolOffset);
        if (!cursor.MoveNext() || !cursor.Kind.IsProcedure())
        {
            throw new InvalidDataException($"No function at offset 0x{symbolOffset:X} of module #{moduleIndex}.");
        }

        var procedure = new ProcedureSymbol(cursor.Record);
        string name = Utf8(procedure.Name);
        uint type = _pdb.ProcedureType(cursor.Kind, procedure);
        var signature = _pdb.GetSignature(type);
        uint functionOffset = procedure.Offset;

        _document.Title = name;
        Property("Function", name);
        Property("Type", $"{_pdb.TypeName(type)}   (TypeIndex 0x{type:X})");
        Property("Scope", Lang.Tr(cursor.Kind.IsGlobalProcedure() ? "global" : "local (static / internal linkage)"));
        Property("Module", $"#{moduleIndex} {module.Name}");
        Property("Address", _pdb.FormatAddress(procedure.Section, functionOffset));
        Property("Size", FormatSize(procedure.CodeSize));
        Property("Body", Lang.Format("prologue ends at +0x{0:X}, epilogue starts at +0x{1:X}", procedure.PrologueEnd, procedure.EpilogueStart));
        if (signature is { IsMember: true })
        {
            string adjustment = signature.ThisAdjustment != 0
                ? "  (" + Lang.Format("this adjustment: {0}", signature.ThisAdjustment) + ")"
                : "";
            Property("Class", _pdb.TypeName(signature.ClassType) + adjustment);
        }

        string attributes = DescribeFlags(
            procedure.Flags,
            (0x01, "no FPO"),
            (0x08, "noreturn"),
            (0x10, "unreachable"),
            (0x20, "custom calling convention"),
            (0x40, "noinline"),
            (0x80, "optimized"));
        if (attributes.Length > 0)
        {
            Property("Attributes", attributes);
        }
        if (_pdb.FindSourceLine(module, procedure.Section, functionOffset) is { } line)
        {
            Property("Source", $"{line.File}({line.Line})");
        }
        if (_propertiesOnly)
        {
            return;
        }

        var parameterNames = ParameterNames.InScope(signature, cursor);
        var body = FunctionBody.Read(_pdb, cursor, functionOffset);
        foreach (string frame in body.Frames)
        {
            Property("Stack", frame);
        }
        MarkUnflaggedParameters(body.Root, signature, parameterNames);

        WriteHeader();
        WriteSignature(signature, name, parameterNames);
        _out.Line().Write("{").Line();
        WriteBody(body);
        _out.Write("}").Line();
    }

    /// <summary>Unoptimized code does not flag its parameters: they are the first stack variables of the function.</summary>
    static void MarkUnflaggedParameters(Scope root, FunctionSignature? signature, List<string>? parameterNames)
    {
        if (signature == null || parameterNames == null || root.Variables.Exists(variable => variable.IsParameter))
        {
            return;
        }

        foreach (var variable in root.Variables)
        {
            int position = parameterNames.IndexOf(variable.Name);
            bool isNamedParameter = position >= 0 && position < signature.Parameters.Length;
            variable.IsParameter = variable.Prefix == "" && (variable.Name == "this" || isNamedParameter);
        }
    }

    void WriteBody(FunctionBody body)
    {
        var root = body.Root;
        var parameters = root.Variables.FindAll(variable => variable.IsParameter);
        var locals = root.Variables.FindAll(variable => !variable.IsParameter);

        _bodyHasSection = false;
        if (parameters.Count > 0)
        {
            BeginBodySection(Lang.Tr("Parameters"));
            WriteVariables(parameters, lead: "", insideTree: false);
        }
        if (locals.Count > 0)
        {
            BeginBodySection(Lang.Tr("Local variables"));
            WriteVariables(locals, lead: "", insideTree: false);
        }
        if (body.LocalTypes.Count > 0)
        {
            BeginBodySection($"{Lang.Tr("Local types")} ({body.LocalTypes.Count})");
            foreach (var (type, name) in body.LocalTypes)
            {
                _out.Write(Indent).Keyword(_pdb.UdtKeyword(type)).Write(" ")
                    .Write(name, TokenKind.Type, new Target(ItemKind.Type, type)).Write(";").Line();
            }
        }
        if (root.Children.Count > 0)
        {
            BeginBodySection(Lang.Tr("Inlined code  (-> lists the functions it calls)"));
            WriteScopeTree(root.Children, depth: "", withOffsets: HasCodeOffsets(root.Children));
        }

        WriteList("Calls", root.Calls, areFunctions: true);
        WriteList("Indirect calls", body.IndirectCalls, areFunctions: false);
        WriteList("Heap allocations", body.HeapAllocations, areFunctions: false);
        WriteList("Called by", body.Callers, areFunctions: true);
        WriteList("Other records", root.Notes, areFunctions: false);

        if (!_bodyHasSection)
        {
            _out.Write(Indent).Comment("// " + Lang.Tr("no local symbols recorded for this function")).Line();
        }
    }

    /// <summary>Sections of the body are separated by an empty line.</summary>
    void BeginBodySection(string title)
    {
        if (_bodyHasSection)
        {
            _out.Line();
        }
        _bodyHasSection = true;
        _out.Write(Indent);
        WriteSectionTitle(title);
    }

    void WriteList(string title, List<string> items, bool areFunctions)
    {
        if (items.Count == 0)
        {
            return;
        }

        BeginBodySection($"{Lang.Tr(title)} ({items.Count})");
        foreach (string item in items)
        {
            _out.Write(Indent).Comment("//     ");
            if (areFunctions)
            {
                _out.Write(item, TokenKind.Function, search: item);
            }
            else
            {
                _out.Comment(item);
            }
            _out.Line();
        }
    }

    /// <summary>
    /// Declarations in aligned columns: type, name, then where the variable lives.
    /// Inside the tree of inlined calls every line is part of a comment, which <paramref name="lead"/> opens.
    /// </summary>
    void WriteVariables(List<Variable> variables, string lead, bool insideTree)
    {
        const int typeColumnLimit = 40;
        const int nameColumnLimit = 32;
        if (variables.Count == 0)
        {
            return;
        }

        var types = variables.ConvertAll(variable => _pdb.SplitType(variable.Type));
        int typeWidth = ColumnWidth(
            variables.Select((variable, i) => types[i].NameIsEnclosed ? 0 : variable.Prefix.Length + types[i].Prefix.Length),
            typeColumnLimit);
        int nameWidth = ColumnWidth(
            variables.Select((variable, i) => variable.Name.Length + types[i].Suffix.Length + variable.Suffix.Length),
            nameColumnLimit);

        for (int i = 0; i < variables.Count; i++)
        {
            var variable = variables[i];
            var type = types[i];

            _out.Write(Indent).Comment(lead).Keyword(variable.Prefix);
            WriteTypeText(type.Prefix, variable.Type);
            if (!type.NameIsEnclosed)
            {
                _out.Spaces(Math.Max(1, typeWidth - variable.Prefix.Length - type.Prefix.Length + 1));
            }

            string afterName = type.Suffix + variable.Suffix;
            _out.Write(variable.Name, variable.IsParameter ? TokenKind.Parameter : variable.NameKind).Write(afterName + ";");

            string[] details =
            [
                insideTree && variable.IsParameter ? Lang.Tr("param") : "",
                variable.Note,
                string.Join(", ", variable.Locations),
            ];
            string note = string.Join(" · ", details.Where(detail => detail.Length > 0));
            if (note.Length > 0)
            {
                _out.Spaces(Math.Max(1, nameWidth + 3 - variable.Name.Length - afterName.Length));
                _out.Comment(insideTree ? $"({note})" : "// " + note);
            }
            _out.Line();
        }
    }

    static bool HasCodeOffsets(List<Scope> scopes) =>
        scopes.Exists(scope => scope.CodeOffset != null || HasCodeOffsets(scope.Children));

    /// <summary>
    /// Inlined calls and blocks as an indented tree: one comment line per scope, its variables listed under it.
    /// When offsets are known, a first column says where the scope's code starts inside the function.
    /// </summary>
    void WriteScopeTree(List<Scope> scopes, string depth, bool withOffsets)
    {
        string offsetPadding = withOffsets ? new string(' ', OffsetColumnWidth) : "";
        if (withOffsets)
        {
            // In the order the code runs. The sort is stable, so scopes without an offset keep their order at the end.
            scopes = [.. scopes.OrderBy(scope => scope.CodeOffset ?? uint.MaxValue)];
        }

        for (int i = 0; i < scopes.Count; i++)
        {
            var scope = scopes[i];

            // Identical neighbours (the same small function inlined several times in a row) share one line.
            int repeats = 1;
            while (i + repeats < scopes.Count && scopes[i + repeats].Signature == scope.Signature)
            {
                repeats++;
            }
            i += repeats - 1;

            _out.Write(Indent).Comment("// ");
            if (withOffsets)
            {
                string offset = scope.CodeOffset is { } value ? $"+0x{value:X}" : "";
                _out.Number(offset.PadRight(OffsetColumnWidth));
            }
            _out.Comment(depth);
            if (scope.IsInlinedCall)
            {
                _out.Write(scope.Title, TokenKind.Function, search: scope.Title);
            }
            else
            {
                _out.Comment(scope.Title);
            }

            string suffix = (repeats > 1 ? $"   x{repeats}" : "")
                + (scope.Calls.Count > 0 ? "   -> " + string.Join(", ", scope.Calls) : "");
            _out.Comment(suffix).Line();

            string childLead = "// " + offsetPadding + depth + Indent;
            WriteVariables(scope.Variables, childLead, insideTree: true);
            foreach (string note in scope.Notes)
            {
                _out.Write(Indent).Comment(childLead + note).Line();
            }
            WriteScopeTree(scope.Children, depth + Indent, withOffsets);
        }
    }
}
