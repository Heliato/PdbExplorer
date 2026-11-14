using System.Diagnostics;
using System.Windows;
using Microsoft.Win32;
using PdbExplorer.Export;
using PdbExplorer.Rendering;

namespace PdbExplorer.UI;

// Export commands. Anything longer than writing one file runs in the background and can be cancelled with Esc.
public partial class MainWindow
{
    /// <summary>Above this many items, exporting the list asks for confirmation first.</summary>
    const int ConfirmListExportAbove = 2000;

    CancellationTokenSource? _exportCancellation;

    bool IsExporting => _exportCancellation != null;

    void CancelExport() => _exportCancellation?.Cancel();

    void ExportDocument()
    {
        if (DocumentOf(ActiveEditor) is not { } document)
        {
            SetStatus("No document to export.");
            return;
        }

        var kind = document.Target.Kind;
        var dialog = new SaveFileDialog
        {
            FileName = Exporter.SafeFileName(document.Title) + Exporter.Extension(kind),
            Filter = Lang.Tr("C/C++ files (*.h;*.cpp)|*.h;*.cpp|Text files (*.txt)|*.txt|All files (*.*)|*.*"),
            FilterIndex = kind is ItemKind.Type or ItemKind.Function ? 1 : 2,
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            File.WriteAllText(dialog.FileName, document.Text);
            Log(Lang.Format("Exported {0}", dialog.FileName));
            SetStatus("Exported {0}", dialog.FileName);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ShowError("Export failed", error.Message);
        }
    }

    async void ExportClass()
    {
        if (_pdb is not { } pdb || DocumentOf(ActiveEditor) is not { Target.Kind: ItemKind.Type } document)
        {
            SetStatus("Open a class, struct, union or enum tab first.");
            return;
        }
        if (PromptForFolder() is not { } folder)
        {
            return;
        }

        var timer = Stopwatch.StartNew();
        await RunExport((report, cancellation) =>
        {
            int files = Exporter.ExportItems(pdb, [document.Target], folder, report, cancellation);
            return Summary(files, folder, timer);
        });
    }

    async void ExportListedItems()
    {
        if (_pdb is not { } pdb || _category is not { } category || _items.Length == 0)
        {
            SetStatus("No document to export.");
            return;
        }

        int[] items = _items;
        if (items.Length > ConfirmListExportAbove)
        {
            string question = Lang.Format("Export the {0:N0} listed items, one file each?", items.Length);
            if (!Confirm(question, MessageBoxImage.Question))
            {
                return;
            }
        }
        if (PromptForFolder() is not { } folder)
        {
            return;
        }

        var timer = Stopwatch.StartNew();
        await RunExport((report, cancellation) =>
        {
            Target[] targets = Array.ConvertAll(items, item => category.TargetOf(pdb, item));
            int files = Exporter.ExportItems(pdb, targets, folder, report, cancellation);
            return Summary(files, folder, timer);
        });
    }

    async void ExportEverything()
    {
        if (_pdb is not { } pdb)
        {
            return;
        }

        string question = Lang.Format(
            "Export everything?\n\nEvery type is written as a header (grouped like the original source tree) and every function as a .cpp (one per module): {0:N0} types and {1:N0} functions.\n\nThis can take several minutes and several GB of disk space.",
            pdb.Types.Count,
            pdb.Functions.Count);
        if (!Confirm(question, MessageBoxImage.Warning) || PromptForFolder() is not { } folder)
        {
            return;
        }

        var timer = Stopwatch.StartNew();
        await RunExport((report, cancellation) =>
        {
            var result = Exporter.ExportEverything(pdb, folder, report, cancellation);
            string summary = Summary(result.Files, folder, timer);
            if (result.Failed > 0)
            {
                summary += "  -  " + Lang.Format("{0:N0} file(s) could not be written, first error: {1}", result.Failed, result.FirstError ?? "");
            }
            return summary;
        });
    }

    static string Summary(int files, string folder, Stopwatch timer) =>
        Lang.Format("{0:N0} file(s) written to {1} in {2:F1} s", files, folder, timer.Elapsed.TotalSeconds);

    bool Confirm(string question, MessageBoxImage icon) =>
        MessageBox.Show(this, question, Lang.Tr("Export"), MessageBoxButton.OKCancel, icon) == MessageBoxResult.OK;

    string? PromptForFolder()
    {
        var dialog = new OpenFolderDialog { Title = Lang.Tr("Choose the destination folder") };
        return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
    }

    /// <summary>
    /// Runs <paramref name="export"/> on a background thread, showing its progress in the status bar.
    /// The delegate returns the summary to display when it is done.
    /// </summary>
    async Task RunExport(Func<Action<string, double>, CancellationToken, string> export)
    {
        if (IsExporting)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        _exportCancellation = cancellation;
        MCancel.IsEnabled = true;
        Progress.Value = 0;
        Progress.Visibility = Visibility.Visible;

        IProgress<(string Activity, double Fraction)> progress = new Progress<(string Activity, double Fraction)>(step =>
        {
            // Reports still queued when the export ends must not overwrite the final status.
            if (_exportCancellation == cancellation)
            {
                Progress.Value = step.Fraction;
                StatusText.Text = step.Activity;
            }
        });

        // Worker threads report after every file: let at most ten updates per second through to the UI.
        long lastReport = 0;
        void Report(string activity, double fraction)
        {
            long now = Environment.TickCount64;
            if (now - Interlocked.Read(ref lastReport) >= 100)
            {
                Interlocked.Exchange(ref lastReport, now);
                progress.Report((activity, fraction));
            }
        }

        try
        {
            string summary = await Task.Run(() => export(Report, cancellation.Token));
            _exportCancellation = null;
            Log(summary);
            SetStatus("{0}", summary);
        }
        catch (OperationCanceledException)
        {
            _exportCancellation = null;
            Log(Lang.Tr("Export cancelled."));
            SetStatus("Export cancelled.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or AggregateException or InvalidDataException)
        {
            _exportCancellation = null;
            SetStatus("Export failed");
            ShowError("Export failed", error.GetBaseException().Message);
        }
        finally
        {
            _exportCancellation = null;
            MCancel.IsEnabled = false;
            Progress.Visibility = Visibility.Collapsed;
        }
    }
}
