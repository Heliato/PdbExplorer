using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using AvalonDock.Themes;
using Microsoft.Win32;
using PdbExplorer.Pdb;
using PdbExplorer.Rendering;

namespace PdbExplorer.UI;

/// <summary>
/// The main window. This file holds what concerns the window as a whole (theme, language, status bar,
/// loading a PDB); the menu, the explorer, the documents and the export commands are in MainWindow.*.cs.
/// </summary>
public partial class MainWindow : Window
{
    PdbFile? _pdb;

    // The status and count texts are kept as (key, arguments) so they can be shown again in another language.
    string _status = "Ready - open a PDB (Ctrl+O) or drop it onto the window";
    object[] _statusArguments = [];

    public MainWindow(string? pdbPath)
    {
        InitializeComponent();
        ApplyTheme();
        AttachMenuActions();
        ApplyLanguage();

        _searchDelay.Tick += (_, _) =>
        {
            _searchDelay.Stop();
            RunSearch();
        };
        Dock.ActiveContentChanged += (_, _) => ShowPropertiesOf(Dock.ActiveContent as ICSharpCode.AvalonEdit.TextEditor);

        if (pdbPath != null)
        {
            Loaded += (_, _) => OpenPdb(pdbPath);
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyTitleBarTheme();
    }

    void ApplyTheme()
    {
        AppTheme.Apply();
        Dock.Theme = AppTheme.IsDark ? new Vs2013DarkTheme() : new Vs2013LightTheme();
        ApplyTitleBarTheme();

        foreach (var editor in Editors)
        {
            editor.TextArea.TextView.Redraw();
        }

        // Glyph colors come from the palette: the lists have to read them again.
        Categories.Items.Refresh();
        if (_category != null)
        {
            Results.ItemsSource = new VirtualList(_category, _items);
        }
    }

    void ApplyTitleBarTheme()
    {
        const int useImmersiveDarkMode = 20;   // DWMWA_USE_IMMERSIVE_DARK_MODE
        IntPtr window = new WindowInteropHelper(this).Handle;
        if (window != IntPtr.Zero)
        {
            int enabled = AppTheme.IsDark ? 1 : 0;
            DwmSetWindowAttribute(window, useImmersiveDarkMode, ref enabled, sizeof(int));
        }
    }

    void ApplyLanguage()
    {
        ApplyMenuTitles();
        ExplorerPane.Title = Lang.Tr("Symbol Explorer");
        OutputPane.Title = Lang.Tr("Output");
        PropsPane.Title = Lang.Tr("Properties");
        SearchHint.Text = Lang.Tr("Search this category (Ctrl+T)");
        StatusText.Text = Lang.Format(_status, _statusArguments);
        ShowResultCount();
        Categories.Items.Refresh();

        if (OptionsTab is { Content: OptionsView options } tab)
        {
            tab.Title = Lang.Tr("Options");
            options.Localize();
        }
    }

    void SetStatus(string text, params object[] arguments)
    {
        _status = text;
        _statusArguments = arguments;
        StatusText.Text = Lang.Format(text, arguments);
    }

    void Log(string line)
    {
        OutputLog.AppendText(line + "\n");
        OutputLog.ScrollToEnd();
    }

    void ShowError(string title, string message)
    {
        Log(Lang.Tr("ERROR: ") + message);
        MessageBox.Show(this, message, Lang.Tr(title), MessageBoxButton.OK, MessageBoxImage.Error);
    }

    void PromptForPdb()
    {
        var dialog = new OpenFileDialog { Filter = Lang.Tr("PDB files (*.pdb)|*.pdb|All files (*.*)|*.*") };
        if (dialog.ShowDialog(this) == true)
        {
            OpenPdb(dialog.FileName);
        }
    }

    void OnFileDropped(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
        {
            OpenPdb(files[0]);
        }
    }

    async void OpenPdb(string path)
    {
        CloseDocuments();
        _pdb = null;
        _category = null;
        _items = [];
        _history.Clear();
        _historyPosition = -1;
        _resultCount = "";
        Categories.ItemsSource = null;
        Results.ItemsSource = null;
        PropertyList.ItemsSource = null;
        CountText.Text = "";

        Progress.Value = 0;
        Progress.Visibility = Visibility.Visible;
        SetStatus("Loading...");
        Log(Lang.Format("Opening {0}", path));

        try
        {
            // Progress<T> brings the callbacks of the loading thread back to the UI thread.
            IProgress<string> log = new Progress<string>(Log);
            IProgress<(string Activity, double Fraction)> activity = new Progress<(string Activity, double Fraction)>(step =>
            {
                Progress.Value = step.Fraction;
                StatusText.Text = step.Activity;
            });
            var progress = new LoadProgress(log.Report, (text, fraction) => activity.Report((text, fraction)));

            var pdb = await Task.Run(() => PdbFile.Open(path, progress));
            _pdb = pdb;
            Title = $"{Path.GetFileName(path)} - PDB Explorer";
            Categories.ItemsSource = CreateCategories(pdb);
            Categories.SelectedIndex = 0;
            SetStatus("Ready - {0} loaded in {1:F1} s", Path.GetFileName(path), pdb.LoadTime.TotalSeconds);
            Open(new Target(ItemKind.Info));

            if (Settings.Current.InlineParamNames)
            {
                IndexInlinedParameters(pdb);
            }
        }
        catch (Exception error)
        {
            // Opening runs parallel loops, which wrap what they throw.
            string message = error is AggregateException aggregate ? aggregate.Flatten().InnerExceptions[0].Message : error.Message;
            SetStatus("Loading failed");
            ShowError("Cannot open this PDB", message);
        }
        finally
        {
            Progress.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// Runs in the background after loading. Class documents opened in the meantime are rendered again
    /// once the parameter names of always-inlined methods are available.
    /// </summary>
    async void IndexInlinedParameters(PdbFile pdb)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            await Task.Run(() => pdb.BuildInlinedParameterIndex(CancellationToken.None));
        }
        catch (Exception error) when (error is AggregateException or InvalidDataException)
        {
            Log(Lang.Tr("ERROR: ") + error.GetBaseException().Message);
            return;
        }

        if (pdb != _pdb)
        {
            return;
        }
        Log(Lang.Format(
            "Always-inlined functions indexed in {0:F1} s: parameter names recovered for {1:N0} of them",
            timer.Elapsed.TotalSeconds,
            pdb.InlinedFunctionCount));
        RenderAgain(document => document.Target.Kind == ItemKind.Type);
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
