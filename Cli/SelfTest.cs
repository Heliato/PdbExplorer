using System.Diagnostics;
using System.Text;
using PdbExplorer.Export;
using PdbExplorer.Pdb;
using PdbExplorer.Rendering;

namespace PdbExplorer.Cli;

/// <summary>
/// End-to-end checks against a real PDB: the reader walks every stream, a type and a function are rendered,
/// searched for and exported. Prints what it rendered so the output can also be compared between versions.
/// The exit code is the number of failed checks.
/// </summary>
internal sealed class SelfTest(PdbFile pdb, string typeName, string functionName)
{
    readonly TextWriter _out = Console.Out;
    readonly List<Target> _toExport = [];
    int _failures;

    public int Run()
    {
        CheckIndexes();

        var timer = Stopwatch.StartNew();
        pdb.BuildInlinedParameterIndex(CancellationToken.None);
        _out.WriteLine($"Inline index: parameter names for {pdb.InlinedFunctionCount:N0} always-inlined functions in {timer.ElapsedMilliseconds} ms");

        Show(new Target(ItemKind.Info), maxLines: 200);
        CheckType();
        CheckFunction();
        CheckSearch();
        ShowSamples();
        CheckExport();

        _out.WriteLine(_failures == 0 ? "SELF-TEST PASSED" : $"SELF-TEST: {_failures} failure(s)");
        _out.Flush();
        return _failures;
    }

    void Check(bool passed, string what)
    {
        _out.WriteLine($"{(passed ? "OK  " : "FAIL")} {what}");
        if (!passed)
        {
            _failures++;
        }
    }

    Document Show(Target target, int maxLines)
    {
        var document = DocumentRenderer.Render(pdb, target);
        string[] lines = document.Text.Split('\n');
        _out.WriteLine($"----- {document.Title} ({lines.Length} lines, {document.Segments.Count} segments) -----");
        _out.WriteLine(string.Join('\n', lines.Take(maxLines)));

        Check(!document.Text.Contains("Decoding error"), $"rendered without error: {document.Title}");
        Check(document.Segments.All(segment => segment.Start >= 0 && segment.End <= document.Text.Length), "segments within the text bounds");
        return document;
    }

    void CheckIndexes()
    {
        Check(pdb.Tpi.Count == pdb.Tpi.EndIndex - pdb.Tpi.FirstIndex, $"TPI fully walked ({pdb.Tpi.Count:N0} types)");
        Check(pdb.Ipi.Count == pdb.Ipi.EndIndex - pdb.Ipi.FirstIndex, $"IPI fully walked ({pdb.Ipi.Count:N0} ids)");
        Check(pdb.Types.Count > 0 && pdb.Functions.Count > 0 && pdb.Publics.Count > 0, "types, functions and publics found");
        Check(pdb.Modules.Count > 0 && pdb.Sections.Length > 0, "modules and sections found");
        foreach (var table in pdb.AllTables)
        {
            Check(table.SortedItems.Length == table.Count, $"table sorted ({table.Count:N0} items)");
        }
    }

    void CheckType()
    {
        var types = pdb.Types;
        var (start, end) = types.EqualRange(Encoding.UTF8.GetBytes(typeName));
        Check(end > start, $"type \"{typeName}\" found by exact name");
        Check(end - start == 1, $"\"{typeName}\" listed once (ODR variants merged): {end - start}");
        if (end == start)
        {
            return;
        }

        var target = new Target(ItemKind.Type, types.Reference(types.SortedItems[start]));
        _toExport.Add(target);

        var timer = Stopwatch.StartNew();
        string text = Show(target, maxLines: 60).Text;
        _out.WriteLine($"Class rendered in {timer.ElapsedMilliseconds} ms");
        _out.WriteLine(string.Join('\n', text.Split('\n').SkipWhile(line => !line.Contains("Methods (")).Take(60)));

        Lang.Select("fr");
        string french = DocumentRenderer.Render(pdb, target).Text;
        Lang.Select("en");
        Check(french.Contains("// Nom ") && (french.Contains("// ── Champs") || french.Contains("// ── Méthodes")), "the same class renders in French");

        // These three depend on the content of the type: they are written for Unreal Engine's AActor.
        if (typeName == "AActor")
        {
            Check(text.Contains("Tick(float DeltaSeconds)"), "methods show parameter names taken from the function symbols");
            Check(text.Contains(") override;"), "virtual methods that override a base method are marked override");
            Check(text.Contains(";\n\nprivate:\n    ") || text.Contains(";\n\nprotected:\n    "), "a group of members ends with an empty line before the next access label");
            Check(!text.Contains("public:\n\n"), "no empty line right under an access label");
        }
    }

    void CheckFunction()
    {
        var timer = Stopwatch.StartNew();
        int[] hits = pdb.Functions.Search(functionName, SymbolTable.AllKinds, CancellationToken.None);
        _out.WriteLine($"Search \"{functionName}\": {hits.Length:N0} functions in {timer.ElapsedMilliseconds} ms");
        Check(hits.Length > 0, "function search is not empty");
        if (hits.Length == 0)
        {
            return;
        }

        Check(pdb.Functions.Name(hits[0]).Contains(functionName, StringComparison.OrdinalIgnoreCase), "first result contains the query");
        var target = Target.Function(pdb, pdb.Functions.Reference(hits[0]));
        _toExport.Add(target);

        string text = Show(target, maxLines: 110).Text;
        Check(text.Contains("// ── Parameters ──") || text.Contains("// ── Local variables ──"), "function body is laid out in sections");
    }

    void CheckSearch()
    {
        (string Name, SymbolTable Table)[] tables = [("types", pdb.Types), ("publics", pdb.Publics), ("constants", pdb.Constants)];
        foreach (var (name, table) in tables)
        {
            var timer = Stopwatch.StartNew();
            int count = table.Search("player", SymbolTable.AllKinds, CancellationToken.None).Length;
            _out.WriteLine($"Search \"player\" in {name}: {count:N0} in {timer.ElapsedMilliseconds} ms");
        }
    }

    /// <summary>One item of each remaining kind, taken from the middle of its table.</summary>
    void ShowSamples()
    {
        static uint Middle(SymbolTable table) => table.Reference(table.SortedItems[table.Count / 2]);

        if (pdb.GlobalVariables.Count > 0)
        {
            Show(new Target(ItemKind.GlobalVariable, Middle(pdb.GlobalVariables)), maxLines: 12);
        }
        if (pdb.Publics.Count > 0)
        {
            Show(new Target(ItemKind.PublicSymbol, Middle(pdb.Publics)), maxLines: 12);
        }
        if (pdb.Modules.Count > 0)
        {
            Show(new Target(ItemKind.Module, (uint)(pdb.Modules.Count / 2)), maxLines: 12);
        }
        if (pdb.SourceFiles.Count > 0)
        {
            Show(new Target(ItemKind.SourceFile, Middle(pdb.SourceFiles)), maxLines: 12);
        }
        Show(new Target(ItemKind.Stream, 1), maxLines: 12);
    }

    void CheckExport()
    {
        string folder = Path.Combine(Path.GetTempPath(), "PdbExplorer-selftest-" + Guid.NewGuid().ToString("N"));
        try
        {
            int files = Exporter.ExportItems(pdb, _toExport, folder, (_, _) => { }, CancellationToken.None);
            string[] written = Directory.GetFiles(folder);
            var sizes = written.Select(file => $"{Path.GetFileName(file)} ({new FileInfo(file).Length:N0} bytes)");
            _out.WriteLine($"Export: {files} files: {string.Join(", ", sizes)}");

            bool complete = files == written.Length
                && written.Any(file => file.EndsWith(".h"))
                && written.Any(file => file.EndsWith(".cpp"))
                && written.All(file => new FileInfo(file).Length > 0);
            Check(complete, "export writes non-empty .h and .cpp files");
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }

        // Source paths come from the PDB and become output paths: they must not be able to leave the export folder.
        string hostile = Exporter.MirrorPath(@"C:\a\..\..\Windows\con.h:evil");
        bool contained = !hostile.Contains("..") && !hostile.Contains(':') && !Path.IsPathRooted(hostile);
        Check(contained, $"source paths cannot escape the export folder ({hostile})");
    }
}
