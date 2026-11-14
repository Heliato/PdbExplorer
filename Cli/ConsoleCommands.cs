using System.Diagnostics;
using System.Text;
using PdbExplorer.Export;
using PdbExplorer.Pdb;

namespace PdbExplorer.Cli;

/// <summary>
/// Commands that run without the window and write to the console:
/// <code>
/// PdbExplorer.exe --export-all &lt;pdb&gt; &lt;folder&gt;            same as File > Export everything
/// PdbExplorer.exe --selftest &lt;pdb&gt; [type] [function]    checks the reader, the renderer and the exporter
/// </code>
/// </summary>
internal static class ConsoleCommands
{
    /// <summary>Runs the command named by the arguments. Returns its exit code, or null when there is none to run.</summary>
    public static int? TryRun(string[] arguments)
    {
        if (arguments.Length >= 3 && arguments[0] == "--export-all")
        {
            return ExportAll(arguments[1], arguments[2]);
        }
        if (arguments.Length >= 2 && arguments[0] == "--selftest")
        {
            string typeName = arguments.ElementAtOrDefault(2) ?? "AActor";
            string functionName = arguments.ElementAtOrDefault(3) ?? "AActor::BeginPlay";
            return new SelfTest(Open(arguments[1]), typeName, functionName).Run();
        }
        return null;
    }

    /// <summary>Opens a PDB for a console command: always in English, the loading steps going to the console.</summary>
    static PdbFile Open(string path)
    {
        Lang.Select("en");
        Console.OutputEncoding = Encoding.UTF8;
        return PdbFile.Open(path, new LoadProgress(Console.Out.WriteLine, (_, _) => { }));
    }

    static int ExportAll(string pdbPath, string folder)
    {
        var timer = Stopwatch.StartNew();
        var pdb = Open(pdbPath);
        pdb.BuildInlinedParameterIndex(CancellationToken.None);

        // Worker threads report after every file: print a line every five seconds at most.
        long lastReport = 0;
        void Report(string activity, double fraction)
        {
            long now = Environment.TickCount64;
            if (now - Interlocked.Read(ref lastReport) >= 5000)
            {
                Interlocked.Exchange(ref lastReport, now);
                Console.Out.WriteLine($"{fraction,6:P0}  {activity}");
            }
        }

        var result = Exporter.ExportEverything(pdb, folder, Report, CancellationToken.None);
        string firstError = result.FirstError == null ? "" : $" (first error: {result.FirstError})";
        Console.Out.WriteLine($"{result.Files:N0} files written, {result.Failed:N0} failed{firstError}, {timer.Elapsed.TotalSeconds:F0} s");
        Console.Out.Flush();
        return result.Failed == 0 ? 0 : 1;
    }
}
