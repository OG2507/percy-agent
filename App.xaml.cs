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
            // Log only — no MessageBox. A dialog has to measure text to draw
            // itself, so if the fault IS in text rendering the handler throws
            // inside the handler and takes the app down anyway. That happened.
            Record("UI thread", args.Exception);
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
