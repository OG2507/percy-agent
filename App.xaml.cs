using System.Windows;

namespace PercyAgent;

public partial class App : Application
{
    public static Store Store { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        Store = new Store();
        base.OnStartup(e);
    }
}
