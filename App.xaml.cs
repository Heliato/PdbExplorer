using System.Windows;
using PdbExplorer.Cli;
using PdbExplorer.UI;

namespace PdbExplorer;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (ConsoleCommands.TryRun(e.Args) is { } exitCode)
        {
            Shutdown(exitCode);
            return;
        }

        Lang.Select(Settings.Current.Language);
        DispatcherUnhandledException += (_, error) =>
        {
            // A record this reader cannot decode should cost one message, not the whole session.
            MessageBox.Show(error.Exception.Message, "PDB Explorer", MessageBoxButton.OK, MessageBoxImage.Error);
            error.Handled = true;
        };

        string? pdbPath = e.Args.FirstOrDefault(argument => !argument.StartsWith("--"));
        new MainWindow(pdbPath).Show();
    }
}
