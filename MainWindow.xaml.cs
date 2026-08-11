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
        Loaded += async (_, _) => { Refresh(); LoadMethods(); StartBaldrickPolling(); await RefreshWorkAsync(); };
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

    // ── Methods tab: CARDS, read-only (Stephen 2026-08-11) ──────────────────
    // The truth is Baldrick's Central Catalogue (method on the output type),
    // which the worker prefers. This shows the LOCAL FALLBACK map as readable
    // cards — one method per card, no spreadsheet.

    void LoadMethods()
    {
        var cards = new List<BaldrickCard>();
        foreach (var m in Store.Methods())
        {
            var local = m.Engine.StartsWith("local");
            var detail = (string.IsNullOrEmpty(m.WorkflowFile) ? "no workflow file (toolbox steps only)" : m.WorkflowFile)
                         + (string.IsNullOrEmpty(m.Steps) ? "" : $"   ·   finishing: {m.Steps}");
            cards.Add(new BaldrickCard
            {
                Id = m.MethodKey,
                Badge = m.Enabled ? (local ? "LOCAL (5090)" : "CLOUD") : "OFF",
                BadgeBrush = m.Enabled
                    ? new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(local ? "#4FC38A" : "#6AA8E0"))
                    : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Gray),
                Title = $"{m.MethodKey} — {m.Label}",
                Sub = m.Notes,
                Detail = detail,
            });
        }
        MethodCards.ItemsSource = cards;
        MethodsHint.Text = $"{cards.Count} methods (local fallback) · the truth lives in the Central Catalogue · db: {Store.DatabasePath}";
    }

    void ReloadMethods_Click(object sender, RoutedEventArgs e) => LoadMethods();

    void OpenCatalogue_Click(object sender, RoutedEventArgs e) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            Store.Setting("baldrick_url", "https://baldrick.vslmedia.co.uk").TrimEnd('/') + "/catalogue")
        { UseShellExecute = true });

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
