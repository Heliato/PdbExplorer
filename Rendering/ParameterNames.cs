using System.Text;
using PdbExplorer.Pdb;
using static PdbExplorer.Pdb.ByteReading;
using static PdbExplorer.Pdb.SymbolKind;

namespace PdbExplorer.Rendering;

/// <summary>
/// A type record only knows the types of a function's parameters. Their names are in the symbols of the
/// function body, so they have to be looked up there.
/// </summary>
internal static class ParameterNames
{
    /// <summary>
    /// Names declared directly inside a procedure or inline site. <paramref name="cursor"/> must be positioned
    /// on that record. Returns null unless the names line up with the signature.
    /// </summary>
    public static List<string>? InScope(FunctionSignature? signature, SymbolCursor cursor)
    {
        int needed = signature?.NamedParameterCount ?? 0;
        if (needed == 0)
        {
            return null;
        }

        // Optimized code flags its parameters (S_LOCAL). Unoptimized code has no flag, but declares
        // them first, as stack variables (S_REGREL32).
        var flagged = new List<string>();
        var stackVariables = new List<string>();
        int depth = 1;
        while (depth > 0 && cursor.MoveNext())
        {
            if (cursor.Kind.ClosesScope())
            {
                depth--;
            }
            else if (cursor.Kind.OpensScope())
            {
                depth++;
            }
            else if (depth != 1)
            {
                continue;
            }
            else if (cursor.Kind == S_LOCAL)
            {
                var local = new LocalSymbol(cursor.Record);
                if (local.IsParameter)
                {
                    flagged.Add(Utf8(local.Name));
                }
            }
            else if (cursor.Kind == S_REGREL32)
            {
                stackVariables.Add(Utf8(new RegisterRelativeSymbol(cursor.Record).Name));
            }
        }

        bool hasFlags = flagged.Count > 0;
        var names = hasFlags ? flagged : stackVariables;
        if (names.Count > 0 && names[0] == "this")
        {
            names.RemoveAt(0);
        }

        // Flagged parameters must match exactly: one that was optimized away is simply missing, and the
        // remaining names could not be attributed safely. Stack variables continue with the locals.
        bool linesUp = hasFlags ? names.Count == needed : names.Count >= needed;
        return linesUp ? names : null;
    }

    /// <summary>
    /// Names for a method declared in a class: taken from the function of that name with the same signature,
    /// or, when the method was always inlined, from one of its inline sites.
    /// </summary>
    public static List<string>? OfMethod(PdbFile pdb, string qualifiedName, string methodName, uint methodType)
    {
        if (pdb.GetSignature(methodType) is not { } signature || signature.NamedParameterCount == 0)
        {
            return null;
        }

        byte[] key = Encoding.UTF8.GetBytes(qualifiedName);
        var functions = pdb.Functions;
        var (start, end) = functions.EqualRange(key);
        for (int position = start; position < end; position++)
        {
            var (moduleIndex, offset) = pdb.ResolveProcedureReference(functions.Reference(functions.SortedItems[position]));
            if (moduleIndex >= pdb.Modules.Count || !pdb.Modules[(int)moduleIndex].HasSymbols)
            {
                continue;
            }

            var cursor = pdb.ModuleSymbols(pdb.Modules[(int)moduleIndex], offset);
            if (!cursor.MoveNext() || !cursor.Kind.IsProcedure())
            {
                continue;
            }

            // Overloads share the name: keep the one with the same parameters.
            uint type = pdb.ProcedureType(cursor.Kind, new ProcedureSymbol(cursor.Record));
            bool sameSignature = type == methodType
                || (pdb.GetSignature(type) is { } other
                    && other.IsConst == signature.IsConst
                    && other.Parameters.AsSpan().SequenceEqual(signature.Parameters));
            if (sameSignature && InScope(signature, cursor) is { } names)
            {
                return names;
            }
        }

        if (pdb.FindInlineSite(methodType, Encoding.UTF8.GetBytes(methodName)) is { } site && pdb.Modules[site.Module].HasSymbols)
        {
            var cursor = pdb.ModuleSymbols(pdb.Modules[site.Module], site.Offset);
            if (cursor.MoveNext() && cursor.Kind.IsInlineSite())
            {
                return InScope(signature, cursor);
            }
        }
        return null;
    }
}
