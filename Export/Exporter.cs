using System.Text;
using PdbExplorer.Pdb;
using PdbExplorer.Rendering;
using static PdbExplorer.Pdb.ByteReading;

namespace PdbExplorer.Export;

/// <param name="Failed">Files that could not be written (the export carries on without them).</param>
internal sealed record ExportResult(int Files, int Failed, string? FirstError);

/// <summary>Writes rendered documents to disk: a list of items (one file each) or the whole PDB.</summary>
internal static class Exporter
{
    static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static string Extension(ItemKind kind) => kind switch
    {
        ItemKind.Type => ".h",
        ItemKind.Function => ".cpp",
        _ => ".txt",
    };

    /// <summary>
    /// A name usable as a file name: invalid characters are replaced and long names (templates) are cut,
    /// with a hash of the full name appended so that different names stay different.
    /// </summary>
    public static string SafeFileName(string name)
    {
        const int maxLength = 100;
        const int keptLength = 90;

        var safe = new StringBuilder(name.Length);
        foreach (char character in name)
        {
            bool invalid = character < 32 || "<>:\"/\\|?*".Contains(character);
            safe.Append(invalid ? '_' : character);
        }

        string result = safe.ToString().TrimEnd('.', ' ');
        if (result.Length > maxLength)
        {
            // FNV-1a rather than string.GetHashCode: the same name must give the same file on every run.
            uint hash = 2166136261;
            foreach (char character in name)
            {
                hash = (hash ^ character) * 16777619;
            }
            result = result[..keptLength] + "_" + hash.ToString("X8");
        }

        bool unusable = result.Length == 0 || ReservedNames.Contains(result.Split('.')[0]);
        return unusable ? "_" + result : result;
    }

    /// <summary>
    /// Turns the path of an original source file into a relative path under the export folder.
    /// The path comes from the PDB: the drive colon and names like ".." are neutralized so that
    /// nothing can be written outside that folder.
    /// </summary>
    internal static string MirrorPath(string originalPath)
    {
        string[] parts = originalPath
            .Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries)
            .Select(part => SafeFileName(part.TrimEnd(':')))
            .ToArray();
        return parts.Length == 0 ? "_unnamed.h" : Path.Combine(parts);
    }

    /// <summary>
    /// One file per item. A class also gets a .cpp when some of its methods have a body.
    /// Returns the number of files written.
    /// </summary>
    public static int ExportItems(PdbFile pdb, IReadOnlyList<Target> items, string folder, Action<string, double> report, CancellationToken cancellation)
    {
        Directory.CreateDirectory(folder);
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int files = 0;
        int done = 0;

        Parallel.For(0, items.Count, new ParallelOptions { CancellationToken = cancellation }, i =>
        {
            var target = items[i];
            var document = DocumentRenderer.Render(pdb, target);

            string name = SafeFileName(document.Title);
            string uniqueName = name;
            lock (usedNames)
            {
                for (int suffix = 2; !usedNames.Add(uniqueName); suffix++)
                {
                    uniqueName = $"{name} ({suffix})";
                }
            }

            File.WriteAllText(Path.Combine(folder, uniqueName + Extension(target.Kind)), document.Text, Utf8NoBom);
            Interlocked.Increment(ref files);

            if (target.Kind == ItemKind.Type)
            {
                string implementation = RenderClassImplementation(pdb, target.Id);
                if (implementation.Length > 0)
                {
                    File.WriteAllText(Path.Combine(folder, uniqueName + ".cpp"), implementation, Utf8NoBom);
                    Interlocked.Increment(ref files);
                }
            }

            int finished = Interlocked.Increment(ref done);
            report(Lang.Format("Exporting... {0:N0} / {1:N0}", finished, items.Count), (double)finished / items.Count);
        });
        return files;
    }

    /// <summary>
    /// Everything in the PDB: types as headers that mirror the original source tree, functions as one .cpp
    /// per module, and the global symbols as flat lists.
    /// </summary>
    public static ExportResult ExportEverything(PdbFile pdb, string folder, Action<string, double> report, CancellationToken cancellation)
    {
        var export = new FullExport(pdb, folder, report, cancellation);
        export.WriteTypes();
        export.WriteFunctions();
        export.WriteGlobals();
        return new ExportResult(export.Files, export.Failed, export.FirstError);
    }

    /// <summary>The .cpp side of a class: the function document of every method that has a symbol of its own.</summary>
    static string RenderClassImplementation(PdbFile pdb, uint typeIndex)
    {
        typeIndex = pdb.ResolveForwardReference(typeIndex);
        if (!pdb.Tpi.Contains(typeIndex)
            || !UdtRecord.TryRead(pdb.Tpi.Record(typeIndex), out var udt)
            || udt.Kind == LeafKind.LF_ENUM)
        {
            return "";
        }

        string className = Utf8(udt.Name);
        var methodNames = pdb.GetMembers(udt.FieldList)
            .Where(member => member.Kind == LeafKind.LF_ONEMETHOD)
            .Select(member => member.Name)
            .Distinct();

        var functions = pdb.Functions;
        var written = new HashSet<Target>();
        var text = new StringBuilder();
        foreach (string methodName in methodNames)
        {
            // Every overload has the same name, so one lookup finds them all.
            var (start, end) = functions.EqualRange(Encoding.UTF8.GetBytes(className + "::" + methodName));
            for (int position = start; position < end; position++)
            {
                var target = Target.Function(pdb, functions.Reference(functions.SortedItems[position]));
                if (written.Add(target))
                {
                    text.Append(DocumentRenderer.Render(pdb, target).Text).Append('\n');
                }
            }
        }
        return text.ToString();
    }

    /// <summary>State of a full export. A file that cannot be written is counted and skipped: one bad path must not abort hours of work.</summary>
    sealed class FullExport(PdbFile pdb, string folder, Action<string, double> report, CancellationToken cancellation)
    {
        readonly ParallelOptions _parallel = new() { CancellationToken = cancellation };
        int _files;
        int _failed;
        string? _firstError;

        public int Files => _files;
        public int Failed => _failed;
        public string? FirstError => _firstError;

        /// <summary>Types grouped by the header that defines them, in line order; the folder tree mirrors the original sources.</summary>
        public void WriteTypes()
        {
            var types = pdb.Types;
            var relativePaths = new List<string>();
            var groupOfPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var groupOfSource = new Dictionary<(uint, bool), int>();

            int GroupFor(string relativePath)
            {
                if (!groupOfPath.TryGetValue(relativePath, out int group))
                {
                    group = relativePaths.Count;
                    groupOfPath[relativePath] = group;
                    relativePaths.Add(relativePath);
                }
                return group;
            }

            // Sort key of each type: its group in the high half, its line in the low half.
            var keys = new ulong[types.Count];
            var items = new int[types.Count];
            for (int item = 0; item < types.Count; item++)
            {
                int group;
                uint line = 0;
                if (pdb.FindSourceRecord(types.Reference(item), out var source))
                {
                    line = source.Line;
                    var sourceKey = (source.SourceFile, source.SourceIsNameOffset);
                    if (!groupOfSource.TryGetValue(sourceKey, out group))
                    {
                        group = GroupFor(MirrorPath(pdb.SourceName(source.SourceFile, source.SourceIsNameOffset)));
                        groupOfSource[sourceKey] = group;
                    }
                }
                else
                {
                    group = GroupFor(Path.Combine("_NoSourceInfo", FirstLetterFile(types.NameBytes(item))));
                }
                keys[item] = (ulong)group << 32 | line;
                items[item] = item;
            }
            Array.Sort(keys, items);

            var ranges = new List<(int Start, int End)>();
            for (int start = 0; start < items.Length;)
            {
                int end = start;
                while (end < items.Length && keys[end] >> 32 == keys[start] >> 32)
                {
                    end++;
                }
                ranges.Add((start, end));
                start = end;
            }

            int done = 0;
            Parallel.ForEach(ranges, _parallel, range => Guard(() =>
            {
                string path = Path.Combine(folder, "Types", relativePaths[(int)(keys[range.Start] >> 32)]);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using (var writer = new StreamWriter(path, append: false, Utf8NoBom))
                {
                    for (int i = range.Start; i < range.End; i++)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        var target = new Target(ItemKind.Type, types.Reference(items[i]));
                        writer.Write(DocumentRenderer.Render(pdb, target).Text);
                        writer.Write('\n');
                    }
                }

                int finished = Interlocked.Add(ref done, range.End - range.Start);
                report(Lang.Format("Exporting types... {0:N0} / {1:N0}", finished, types.Count), 0.5 * finished / Math.Max(1, types.Count));
            }));
        }

        /// <summary>One .cpp per module (a module is one compiled unit), functions in the order of its symbols.</summary>
        public void WriteFunctions()
        {
            Directory.CreateDirectory(Path.Combine(folder, "Functions"));
            int done = 0;
            Parallel.ForEach(pdb.Modules, _parallel, module =>
            {
                var procedures = pdb.GetProcedureOffsets(module);
                if (procedures.Count > 0)
                {
                    Guard(() =>
                    {
                        string path = Path.Combine(folder, "Functions", $"{module.Index:D4}_{SafeFileName(SourceLikeName(module))}");
                        using var writer = new StreamWriter(path, append: false, Utf8NoBom);
                        foreach (uint offset in procedures)
                        {
                            cancellation.ThrowIfCancellationRequested();
                            var target = new Target(ItemKind.Function, (uint)module.Index, offset);
                            writer.Write(DocumentRenderer.Render(pdb, target).Text);
                            writer.Write('\n');
                        }
                    });
                }
                pdb.ReleaseLineIndex(module);

                int finished = Interlocked.Increment(ref done);
                report(
                    Lang.Format("Exporting functions... module {0:N0} / {1:N0}", finished, pdb.Modules.Count),
                    0.5 + 0.45 * finished / pdb.Modules.Count);
            });
        }

        /// <summary>Global variables, typedefs, constants and public symbols: one line each, in alphabetical order.</summary>
        public void WriteGlobals()
        {
            report(Lang.Tr("Exporting globals..."), 0.96);
            Directory.CreateDirectory(Path.Combine(folder, "Globals"));

            (string File, SymbolTable Table)[] lists =
            [
                ("GlobalVariables.h", pdb.GlobalVariables),
                ("Typedefs.h", pdb.Typedefs),
                ("Constants.h", pdb.Constants),
                ("PublicSymbols.txt", pdb.Publics),
            ];
            foreach (var (file, table) in lists)
            {
                Guard(() =>
                {
                    using var writer = new StreamWriter(Path.Combine(folder, "Globals", file), append: false, Utf8NoBom);
                    foreach (int item in table.SortedItems)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        writer.Write(DeclareGlobal(pdb, table.Reference(item)));
                        writer.Write('\n');
                    }
                });
            }
        }

        void Guard(Action write)
        {
            try
            {
                write();
                Interlocked.Increment(ref _files);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                Interlocked.Increment(ref _failed);
                _firstError ??= error.Message;
            }
        }
    }

    /// <summary>Types without source information go to one file per first letter.</summary>
    static string FirstLetterFile(ReadOnlySpan<byte> name)
    {
        int letter = name.Length > 0 ? (name[0] | 0x20) - 'a' : -1;
        return (letter is >= 0 and < 26 ? ((char)('A' + letter)).ToString() : "_other") + ".h";
    }

    /// <summary>"Module.Engine.3_of_9.cpp.obj" -> "Module.Engine.3_of_9.cpp".</summary>
    static string SourceLikeName(Module module)
    {
        string name = Path.GetFileName(module.Name);
        if (name.EndsWith(".obj", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }

        bool isSourceName = name.EndsWith(".cpp", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".c", StringComparison.OrdinalIgnoreCase);
        return isSourceName ? name : name + ".cpp";
    }

    /// <summary>One-line declaration of a record of the global symbol stream.</summary>
    static string DeclareGlobal(PdbFile pdb, uint symbolOffset)
    {
        var cursor = pdb.GlobalSymbol(symbolOffset);
        if (!cursor.MoveNext())
        {
            return "";
        }

        var kind = cursor.Kind;
        switch (kind)
        {
            case SymbolKind.S_GDATA32:
            case SymbolKind.S_LDATA32:
            case SymbolKind.S_GTHREAD32:
            case SymbolKind.S_LTHREAD32:
                {
                    var data = new DataSymbol(cursor.Record);
                    string storage = (kind.IsStaticData() ? "static " : "") + (kind.IsThreadLocalData() ? "thread_local " : "");
                    string declaration = pdb.Declaration(data.TypeIndex, Utf8(data.Name));
                    return $"{storage}{declaration};   // RVA 0x{pdb.Rva(data.Section, data.Offset):X8}";
                }

            case SymbolKind.S_CONSTANT:
                {
                    var constant = new ConstantSymbol(cursor.Record);
                    return $"const {pdb.Declaration(constant.TypeIndex, Utf8(constant.Name))} = {constant.Value};";
                }

            case SymbolKind.S_UDT:
                {
                    var typedef = new TypedefSymbol(cursor.Record);
                    return $"typedef {pdb.Declaration(typedef.TypeIndex, Utf8(typedef.Name))};";
                }

            case SymbolKind.S_PUB32:
                {
                    var symbol = new PublicSymbol(cursor.Record);
                    return $"0x{pdb.Rva(symbol.Section, symbol.Offset):X8}  {Utf8(symbol.Name)}";
                }

            default:
                return "";
        }
    }
}
