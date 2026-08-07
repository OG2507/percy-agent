using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace PercyAgent;

public partial class App : Application
{
    public static Store Store { get; private set; } = null!;

    /// <summary>Next to the exe, so it is findable without knowing where AppData is.</summary>
    static readonly string LogPath = Path.Combine(AppContext.BaseDirectory, "percy-agent-error.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        // A window that vanishes tells you nothing. Anything that would kill the
        // app gets written down first, and anything survivable does not kill it.
        DispatcherUnhandledException += (_, args) =>
        {
            Record("UI thread", args.Exception);
            MessageBox.Show($"Percy hit a problem and kept going.\n\n{args.Exception.Message}\n\nWritten to:\n{LogPath}",
                            "Percy Agent", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;                    // survivable — do not take the window down
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Record("background thread — fatal", args.ExceptionObject as Exception);

        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Record("unobserved task", args.Exception);
            args.SetObserved();
        };

        try
        {
            Store = new Store();
        }
        catch (Exception ex)
        {
            Record("startup — could not open the database", ex);
            MessageBox.Show($"Percy could not start.\n\n{ex.Message}\n\nWritten to:\n{LogPath}",
                            "Percy Agent", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        base.OnStartup(e);
    }

    static void Record(string where, Exception? ex)
    {
        try
        {
            File.AppendAllText(LogPath,
                $"""

                ── {DateTime.Now:yyyy-MM-dd HH:mm:ss} — {where} ──
                {ex?.GetType().FullName}: {ex?.Message}
                {ex?.StackTrace}
                {(ex?.InnerException is null ? "" : $"INNER {ex.InnerException.GetType().FullName}: {ex.InnerException.Message}\n{ex.InnerException.StackTrace}")}

                """);
        }
        catch { /* logging must never be the thing that kills it */ }
    }
}
