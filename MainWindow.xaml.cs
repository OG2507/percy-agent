using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PercyAgent;

public partial class MainWindow : Window
{
    Store Store => App.Store;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => { Refresh(); LoadMethods(); await RefreshWorkAsync(); };
    }

    /// <summary>A view row — the record plus the few things XAML needs to draw it.</summary>
    public sealed record Row(QueueItem Item)
    {
        public int Id => Item.Id;
        public string Sender => Item.Sender;
        public string Source => Item.Source;
        public string Subject => Item.Subject;
        public string Reason => string.IsNullOrWhiteSpace(Item.Reason) ? Item.Preview : Item.Reason;
        public string Age => Item.Age;
        public string CategoryLabel => Item.CategoryLabel;
        public Visibility LinkVisibility => Item.HasLink ? Visibility.Visible : Visibility.Collapsed;
        public string? Link => Item.Link;

        public Brush BadgeBrush => Item.Category switch
        {
            "failure"  => new SolidColorBrush(Color.FromRgb(0xE0, 0x5A, 0x5A)),
            "official" => new SolidColorBrush(Color.FromRgb(0x5A, 0xA9, 0xE0)),
            "bill"     => new SolidColorBrush(Color.FromRgb(0xE0, 0xB3, 0x3C)),
            "approval" => new SolidColorBrush(Color.FromRgb(0xE0, 0x81, 0x3C)),
            "reply"    => new SolidColorBrush(Color.FromRgb(0x5F, 0xBF, 0x8F)),
            _          => new SolidColorBrush(Color.FromRgb(0x8B, 0x93, 0xA1)),
        };
    }

    List<Row> rows = [];

    void Refresh()
    {
        rows = Store.Queue().Select(x => new Row(x)).ToList();
        QueueList.ItemsSource = rows;

        var n = rows.Count;
        Headline.Text = n == 0 ? "Nothing needs you." : $"{n} thing{(n == 1 ? "" : "s")} need{(n == 1 ? "s" : "")} you.";
        Subhead.Text = n == 0
            ? "Everything that arrived was handled without you."
            : "Deal with these and you're done for the day.";
        EmptyState.Visibility = n == 0 ? Visibility.Visible : Visibility.Collapsed;

        var handled = Store.HandledCount();
        Footer.Text = $"{handled} handled without you  ·  {Store.RuleCount()} rules active";
        DbHint.Text = Store.DatabasePath;
    }

    // ── Work tab ────────────────────────────────────────────────────────────
    // Nothing here keeps its own copy of the queue. Baldrick's queue is the only
    // queue, so approving on the website and approving here are the same act.

    readonly Baldrick baldrick = new();
    CancellationTokenSource? runCts;

    async void RefreshWork_Click(object sender, RoutedEventArgs e) => await RefreshWorkAsync();

    async Task RefreshWorkAsync()
    {
        // Nothing on this tab is worth losing the window over — a Baldrick that is
        // down, or a missing secret file, should read as a red line, not a crash.
        try
        {
            var s = await baldrick.GetStatusAsync();
            StatAwaiting.Text = s.AwaitingApproval.ToString();
            StatComfy.Text = s.ComfyUp ? "up" : "down";
            StatComfy.Foreground = new SolidColorBrush(s.ComfyUp
                ? Color.FromRgb(0x5F, 0xBF, 0x8F) : Color.FromRgb(0xE0, 0x5A, 0x5A));
            WorkError.Text = s.Error ?? "";
            WorkError.Visibility = s.Error is null ? Visibility.Collapsed : Visibility.Visible;
        }
        catch (Exception ex)
        {
            StatAwaiting.Text = "—";
            StatComfy.Text = "?";
            WorkError.Text = $"Could not read Baldrick: {ex.Message}";
            WorkError.Visibility = Visibility.Visible;
        }
    }

    void AppendLog(string line) => Dispatcher.Invoke(() =>
    {
        if (WorkLog.Text.StartsWith("Percy hasn't run")) WorkLog.Text = "";
        WorkLog.Text += (WorkLog.Text.Length > 0 ? "\n" : "") + line;
        LogScroll.ScrollToEnd();
    });

    async void Run_Click(object sender, RoutedEventArgs e)
    {
        if (runCts is not null) { runCts.Cancel(); return; }     // a second click stops it
        runCts = new CancellationTokenSource();
        RunBtn.Content = "Stop";
        WorkLog.Text = "";
        try { await Baldrick.RunWorkerAsync(AppendLog, runCts.Token); }
        finally
        {
            runCts.Dispose(); runCts = null;
            RunBtn.Content = "Run Percy";
            await RefreshWorkAsync();        // whatever it made is now waiting on you
        }
    }

    void OpenApprovals_Click(object sender, RoutedEventArgs e) => Baldrick.OpenApprovalsInBrowser();

    // ── Methods tab ─────────────────────────────────────────────────────────
    // The grid edits the same table Percy Worker dispatches from. Save writes
    // straight back; there is no second copy anywhere to drift.

    System.Collections.ObjectModel.ObservableCollection<MethodRow> methodRows = [];

    void LoadMethods()
    {
        methodRows = new(Store.Methods());
        MethodsGrid.ItemsSource = methodRows;
        MethodsHint.Text = $"{methodRows.Count} methods · this table is what the worker runs · db: {Store.DatabasePath}";
    }

    void ReloadMethods_Click(object sender, RoutedEventArgs e) => LoadMethods();

    void SaveMethods_Click(object sender, RoutedEventArgs e)
    {
        MethodsGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Row, true);
        int saved = 0;
        foreach (var m in methodRows)
        {
            if (string.IsNullOrWhiteSpace(m.MethodKey)) continue;   // a blank new-row line
            Store.SaveMethod(m);
            saved++;
        }
        LoadMethods();
        MethodsHint.Text = $"saved {saved} methods · db: {Store.DatabasePath}";
    }

    int TagId(object sender) => (int)((Button)sender).Tag;

    void Done_Click(object sender, RoutedEventArgs e)   { Store.Decide(TagId(sender), "done"); Refresh(); }
    void Snooze_Click(object sender, RoutedEventArgs e) { Store.Decide(TagId(sender), "snoozed"); Refresh(); }
    void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

    void Open_Click(object sender, RoutedEventArgs e)
    {
        var row = rows.FirstOrDefault(x => x.Id == TagId(sender));
        if (row?.Link is not { Length: > 0 } link) return;
        try { Process.Start(new ProcessStartInfo(link) { UseShellExecute = true }); }
        catch { /* a bad link should never take the window down */ }
    }
}
