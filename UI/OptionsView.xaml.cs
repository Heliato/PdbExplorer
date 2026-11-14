using System.Windows;
using System.Windows.Controls;

namespace PdbExplorer.UI;

/// <summary>The Options tab. A change is saved at once and announced through <see cref="Changed"/>.</summary>
public partial class OptionsView : UserControl
{
    public enum Option
    {
        Language,
        Theme,
        Editor,
        InlinedParameterNames,
    }

    static readonly double[] FontSizes = [10, 11, 12, 13, 14, 15, 16, 18, 20];

    static readonly (string Keys, string Action)[] Shortcuts =
    [
        ("Ctrl+O", "Open a PDB"),
        ("Ctrl+T / Ctrl+E", "Go to symbol (search the explorer)"),
        ("Enter", "Open the selected item"),
        ("F12 / Ctrl+Click", "Go to definition"),
        ("Ctrl+- / Alt+Left", "Navigate back"),
        ("Ctrl+Shift+- / Alt+Right", "Navigate forward"),
        ("Ctrl+F", "Find in the document"),
        ("F3 / Shift+F3", "Next / previous match"),
        ("Ctrl+F4 / Ctrl+W", "Close the tab"),
        ("Ctrl+Tab / Ctrl+Shift+Tab", "Next / previous tab"),
        ("Ctrl+S", "Export the document"),
        ("Ctrl+Shift+E", "Export the class (.h + .cpp)"),
        ("Ctrl+Shift+S", "Export everything"),
        ("Esc", "Cancel a running export"),
    ];

    /// <summary>True while the controls are being filled from the settings, so that this does not count as a change.</summary>
    readonly bool _loading = true;

    public OptionsView()
    {
        InitializeComponent();

        var settings = Settings.Current;
        foreach (double size in FontSizes)
        {
            FontChoice.Items.Add(new ListBoxItem { Content = size.ToString(), Tag = size });
        }
        LanguageChoice.SelectedIndex = settings.Language == "fr" ? 1 : 0;
        ThemeChoice.SelectedIndex = settings.Theme == "Light" ? 1 : 0;
        FontChoice.SelectedIndex = Math.Max(0, Array.IndexOf(FontSizes, settings.FontSize));
        LineNumbers.IsChecked = settings.LineNumbers;
        InlineNames.IsChecked = settings.InlineParamNames;

        Localize();
        _loading = false;
    }

    public event Action<Option>? Changed;

    public void Localize()
    {
        Heading.Text = Lang.Tr("Options");
        LLanguage.Text = Lang.Tr("Language");
        LTheme.Text = Lang.Tr("Theme");
        LFont.Text = Lang.Tr("Editor font size");
        IDark.Content = Lang.Tr("Dark");
        ILight.Content = Lang.Tr("Light");
        LineNumbers.Content = Lang.Tr("Show line numbers");
        InlineNames.Content = Lang.Tr("Find parameter names of always-inlined functions");
        InlineHint.Text = Lang.Tr("A function that is always inlined has no symbol of its own. Its parameter names are recovered, when the PDB still has them, by indexing every module in the background after loading.");
        LKeys.Text = Lang.Tr("Keyboard shortcuts");
        Keys.Text = string.Join("\n", Shortcuts.Select(shortcut => shortcut.Keys.PadRight(28) + Lang.Tr(shortcut.Action)));
    }

    void OnChanged(object sender, RoutedEventArgs e)
    {
        // Ctrl+click can clear the selection of a list: there is nothing to apply until a choice is made again.
        if (_loading
            || LanguageChoice.SelectedItem is not ListBoxItem language
            || ThemeChoice.SelectedItem is not ListBoxItem theme
            || FontChoice.SelectedItem is not ListBoxItem fontSize)
        {
            return;
        }

        var settings = Settings.Current;
        settings.Language = (string)language.Tag;
        settings.Theme = (string)theme.Tag;
        settings.FontSize = (double)fontSize.Tag;
        settings.LineNumbers = LineNumbers.IsChecked == true;
        settings.InlineParamNames = InlineNames.IsChecked == true;
        settings.Save();

        Option changed =
            sender == LanguageChoice ? Option.Language
            : sender == ThemeChoice ? Option.Theme
            : sender == InlineNames ? Option.InlinedParameterNames
            : Option.Editor;
        Changed?.Invoke(changed);
    }
}
