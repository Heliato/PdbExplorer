using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AvalonDock.Layout;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Search;
using PdbExplorer.Rendering;

namespace PdbExplorer.UI;

// Document tabs: opening, navigating between them, following links, and the Options tab.
public partial class MainWindow
{
    const string OptionsTabId = "options";
    const int MaxTabTitleLength = 48;

    readonly List<Target> _history = [];
    int _historyPosition = -1;

    IEnumerable<LayoutDocument> Tabs => Dock.Layout.Descendents().OfType<LayoutDocument>();

    IEnumerable<TextEditor> Editors => Tabs.Select(tab => tab.Content).OfType<TextEditor>();

    /// <summary>The tab with the focus, or the last one that had it when the focus is in a side panel.</summary>
    LayoutDocument? ActiveTab => Dock.Layout.ActiveContent as LayoutDocument ?? Dock.Layout.LastFocusedDocument as LayoutDocument;

    TextEditor? ActiveEditor => ActiveTab?.Content as TextEditor;

    LayoutDocument? OptionsTab => Tabs.FirstOrDefault(tab => tab.ContentId == OptionsTabId);

    /// <summary>The document shown by an editor is kept in its Tag.</summary>
    static Document? DocumentOf(TextEditor? editor) => editor?.Tag as Document;

    static string TabId(Target target) => $"{target.Kind}:{target.Id}:{target.Offset}";

    static string TabTitle(string title) =>
        title.Length > MaxTabTitleLength ? title[..(MaxTabTitleLength - 3)] + "..." : title;

    async void Open(Target target, bool addToHistory = true)
    {
        if (_pdb is not { } pdb)
        {
            return;
        }

        // A forward declaration and its definition are the same document.
        if (target.Kind == ItemKind.Type)
        {
            target = target with { Id = pdb.ResolveForwardReference(target.Id) };
        }
        if (addToHistory)
        {
            Remember(target);
        }

        string id = TabId(target);
        if (Tabs.FirstOrDefault(tab => tab.ContentId == id) is { } existing)
        {
            existing.IsActive = true;
            return;
        }

        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            var document = await Task.Run(() => DocumentRenderer.Render(pdb, target));

            // While rendering, another PDB may have been opened, or the same item opened a second time.
            if (pdb != _pdb || Tabs.Any(tab => tab.ContentId == id))
            {
                return;
            }

            var editor = CreateEditor();
            Show(editor, document);
            AddTab(new LayoutDocument
            {
                Title = TabTitle(document.Title),
                ToolTip = document.Title,
                ContentId = id,
                Content = editor,
            });
            PropertyList.ItemsSource = document.Properties;
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    void AddTab(LayoutDocument tab)
    {
        var pane = ActiveTab?.Parent as LayoutDocumentPane ?? Dock.Layout.Descendents().OfType<LayoutDocumentPane>().First();
        pane.Children.Add(tab);
        tab.IsActive = true;
    }

    TextEditor CreateEditor()
    {
        var settings = Settings.Current;
        var editor = new TextEditor
        {
            IsReadOnly = true,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = settings.FontSize,
            ShowLineNumbers = settings.LineNumbers,
            Padding = new Thickness(4, 0, 0, 0),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        editor.Options.EnableHyperlinks = false;
        editor.Options.EnableEmailHyperlinks = false;

        // Resource references follow the theme when it changes.
        editor.SetResourceReference(BackgroundProperty, "Editor");
        editor.SetResourceReference(ForegroundProperty, "EditorFg");
        editor.SetResourceReference(TextEditor.LineNumbersForegroundProperty, "LineNumber");

        var findBar = SearchPanel.Install(editor);
        findBar.Localization = new FindBarTexts();
        findBar.SetResourceReference(SearchPanel.MarkerBrushProperty, "FindMatch");

        editor.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (Keyboard.Modifiers == ModifierKeys.Control && editor.GetPositionFromPoint(e.GetPosition(editor)) is { } position)
            {
                e.Handled = FollowLink(editor, editor.Document.GetOffset(position.Location));
            }
        };
        editor.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F12)
            {
                e.Handled = FollowLink(editor, editor.CaretOffset);
            }
        };
        return editor;
    }

    static void Show(TextEditor editor, Document document)
    {
        editor.Tag = document;
        editor.Text = document.Text;

        var transformers = editor.TextArea.TextView.LineTransformers;
        for (int i = transformers.Count - 1; i >= 0; i--)
        {
            if (transformers[i] is SegmentColorizer)
            {
                transformers.RemoveAt(i);
            }
        }
        transformers.Add(new SegmentColorizer(document.Segments));
    }

    /// <summary>
    /// Renders open documents again, keeping their scroll position: after a language change, or once
    /// information that was missing when they were opened becomes available.
    /// </summary>
    async void RenderAgain(Func<Document, bool> filter)
    {
        if (_pdb is not { } pdb)
        {
            return;
        }

        foreach (var tab in Tabs.ToList())
        {
            if (tab.Content is not TextEditor editor || DocumentOf(editor) is not { } current || !filter(current))
            {
                continue;
            }

            var document = await Task.Run(() => DocumentRenderer.Render(pdb, current.Target));
            if (pdb != _pdb || !ReferenceEquals(editor.Tag, current))
            {
                continue;
            }

            double scrollOffset = editor.VerticalOffset;
            Show(editor, document);
            editor.ScrollToVerticalOffset(scrollOffset);
            tab.Title = TabTitle(document.Title);
            tab.ToolTip = document.Title;
        }
        ShowPropertiesOf(ActiveEditor);
    }

    void ShowPropertiesOf(TextEditor? editor)
    {
        if (DocumentOf(editor) is { } document)
        {
            PropertyList.ItemsSource = document.Properties;
        }
    }

    /// <summary>Follows the link under <paramref name="offset"/>, if there is one.</summary>
    bool FollowLink(TextEditor editor, int offset)
    {
        if (DocumentOf(editor) is not { } document)
        {
            return false;
        }

        int index = document.Segments.FindLastIndex(segment => segment.Start <= offset);
        if (index < 0 || offset > document.Segments[index].End)
        {
            return false;
        }

        var segment = document.Segments[index];
        if (segment.Link is { } target)
        {
            Open(target);
            return true;
        }
        if (segment.Search != null)
        {
            SearchFunctions(segment.Search);
            return true;
        }
        return false;
    }

    void GoToDefinition()
    {
        if (ActiveEditor is { } editor)
        {
            FollowLink(editor, editor.CaretOffset);
        }
    }

    void FindInDocument()
    {
        if (ActiveEditor is { } editor)
        {
            editor.Focus();
            ApplicationCommands.Find.Execute(null, editor.TextArea);
        }
    }

    void Remember(Target target)
    {
        if (_historyPosition >= 0 && _history[_historyPosition] == target)
        {
            return;
        }

        // Opening something after going back drops the "forward" part of the history.
        _history.RemoveRange(_historyPosition + 1, _history.Count - _historyPosition - 1);
        _history.Add(target);
        _historyPosition = _history.Count - 1;
    }

    /// <summary>Moves backward (-1) or forward (+1) through the documents opened so far.</summary>
    void Navigate(int direction)
    {
        int position = _historyPosition + direction;
        if (position < 0 || position >= _history.Count)
        {
            return;
        }
        _historyPosition = position;
        Open(_history[position], addToHistory: false);
    }

    void CloseActiveTab() => ActiveTab?.Close();

    void CloseAllTabs()
    {
        foreach (var tab in Tabs.ToList())
        {
            tab.Close();
        }
    }

    /// <summary>Closes the documents of the current PDB (the Options tab stays).</summary>
    void CloseDocuments()
    {
        foreach (var tab in Tabs.Where(tab => tab.Content is TextEditor).ToList())
        {
            tab.Close();
        }
    }

    void CycleTab(int direction)
    {
        if (ActiveTab is not { Parent: LayoutDocumentPane pane } tab)
        {
            return;
        }

        int count = pane.Children.Count;
        int index = pane.Children.IndexOf(tab);
        pane.Children[((index + direction) % count + count) % count].IsActive = true;
    }

    void ShowOptions()
    {
        if (OptionsTab is { } existing)
        {
            existing.IsActive = true;
            return;
        }

        var options = new OptionsView();
        options.Changed += OnOptionChanged;
        AddTab(new LayoutDocument { Title = Lang.Tr("Options"), ContentId = OptionsTabId, Content = options });
    }

    void OnOptionChanged(OptionsView.Option option)
    {
        var settings = Settings.Current;
        switch (option)
        {
            case OptionsView.Option.Language:
                Lang.Select(settings.Language);
                ApplyTheme();   // a new AvalonDock theme object reloads its menu texts in the new language
                ApplyLanguage();
                RenderAgain(_ => true);
                break;

            case OptionsView.Option.Theme:
                ApplyTheme();
                break;

            case OptionsView.Option.Editor:
                foreach (var editor in Editors)
                {
                    editor.FontSize = settings.FontSize;
                    editor.ShowLineNumbers = settings.LineNumbers;
                }
                break;

            case OptionsView.Option.InlinedParameterNames:
                if (settings.InlineParamNames && _pdb is { HasInlinedParameterIndex: false } pdb)
                {
                    IndexInlinedParameters(pdb);
                }
                break;
        }
    }
}
