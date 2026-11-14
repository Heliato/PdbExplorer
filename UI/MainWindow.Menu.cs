using System.Windows.Controls;
using System.Windows.Input;
using PdbExplorer.Rendering;

namespace PdbExplorer.UI;

// Menu and keyboard shortcuts. The shortcuts follow Visual Studio's defaults.
public partial class MainWindow
{
    /// <summary>Every menu item with its (English) title and what it does; submenus have no action.</summary>
    (MenuItem Item, string Title, Action? Run)[] MenuEntries =>
    [
        (MFile, "_File", null),
        (MOpen, "_Open PDB...", PromptForPdb),
        (MInfo, "PDB _information", () => Open(new Target(ItemKind.Info))),
        (MExport, "_Export document...", ExportDocument),
        (MExportClass, "Export _class (.h + .cpp)...", ExportClass),
        (MExportList, "Export _listed items...", ExportListedItems),
        (MExportAll, "Export e_verything...", ExportEverything),
        (MCancel, "_Cancel export", CancelExport),
        (MExit, "E_xit", Close),

        (MNav, "_Navigate", null),
        (MFind, "_Go to symbol", FocusSearch),
        (MFindDoc, "_Find in document", FindInDocument),
        (MGoDef, "Go to _definition", GoToDefinition),
        (MBack, "_Back", () => Navigate(-1)),
        (MForward, "For_ward", () => Navigate(+1)),

        (MWindow, "_Window", null),
        (MClose, "_Close tab", CloseActiveTab),
        (MCloseAll, "Close _all tabs", CloseAllTabs),
        (MNext, "_Next tab", () => CycleTab(+1)),
        (MPrev, "_Previous tab", () => CycleTab(-1)),

        (MTools, "_Tools", null),
        (MOptions, "_Options...", ShowOptions),
    ];

    (ModifierKeys Modifiers, Key Key, Action Run)[] Shortcuts
    {
        get
        {
            const ModifierKeys ctrl = ModifierKeys.Control;
            const ModifierKeys ctrlShift = ModifierKeys.Control | ModifierKeys.Shift;
            const ModifierKeys alt = ModifierKeys.Alt;
            return
            [
                (ctrl, Key.O, PromptForPdb),
                (ctrl, Key.T, FocusSearch),
                (ctrl, Key.E, FocusSearch),
                (ctrl, Key.OemComma, FocusSearch),
                (ctrl, Key.F4, CloseActiveTab),
                (ctrl, Key.W, CloseActiveTab),
                (ctrl, Key.Tab, () => CycleTab(+1)),
                (ctrl, Key.PageDown, () => CycleTab(+1)),
                (ctrlShift, Key.Tab, () => CycleTab(-1)),
                (ctrl, Key.PageUp, () => CycleTab(-1)),
                (ctrl, Key.OemMinus, () => Navigate(-1)),
                (ctrl, Key.Subtract, () => Navigate(-1)),
                (alt, Key.Left, () => Navigate(-1)),
                (ctrlShift, Key.OemMinus, () => Navigate(+1)),
                (ctrlShift, Key.Subtract, () => Navigate(+1)),
                (alt, Key.Right, () => Navigate(+1)),
                (ctrl, Key.S, ExportDocument),
                (ctrlShift, Key.S, ExportEverything),
                (ctrlShift, Key.E, ExportClass),
            ];
        }
    }

    void AttachMenuActions()
    {
        foreach (var (item, _, run) in MenuEntries)
        {
            if (run != null)
            {
                item.Click += (_, _) => run();
            }
        }
    }

    void ApplyMenuTitles()
    {
        foreach (var (item, title, _) in MenuEntries)
        {
            item.Header = Lang.Tr(title);
        }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);

        // With Alt held, WPF reports the key as a "system" key.
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = Keyboard.Modifiers;

        if (key == Key.Escape && modifiers == ModifierKeys.None && IsExporting)
        {
            CancelExport();
            e.Handled = true;
            return;
        }

        foreach (var shortcut in Shortcuts)
        {
            if (shortcut.Key == key && shortcut.Modifiers == modifiers)
            {
                shortcut.Run();
                e.Handled = true;
                return;
            }
        }
    }
}
