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
        Loaded += (_, _) => Refresh();
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
