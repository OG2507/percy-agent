using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace PercyAgent;

// The Baldrick tab as CARDS (Stephen's design, 2026-08-11): one thing per
// card, a badge, a sentence, buttons — the Decisions-tab pattern, never a
// dense grid. Order: batch stories (what's happening) → needs-you (decide)
// → rendering now → next up. Each render card shows its checklist progress
// from the job row's step marks. Polls /api/v1/percy/jobs every 60s.
public partial class MainWindow
{
    static readonly HttpClient BaldrickHttp = new() { Timeout = TimeSpan.FromSeconds(20) };
    DispatcherTimer? _baldrickTimer;

    string BaldrickUrl => Store.Setting("baldrick_url", "https://baldrick.vslmedia.co.uk").TrimEnd('/');

    static readonly string[] StepOrder = { "claimed", "rendered", "uploaded", "approved", "filed", "cropped" };

    string BaldrickSecret()
    {
        var path = Store.Setting("baldrick_secret_file",
            @"D:\ComfyUI-Data\baldrick\workflows\python\.worker_secret");
        try { return File.ReadAllText(path).Trim(); }
        catch
        {
            try { return File.ReadAllText(@"C:\ComfyUI\ComfyUI\user\default\workflows\baldrick-tools\.worker_secret").Trim(); }
            catch { return ""; }
        }
    }

    void StartBaldrickPolling()
    {
        _ = RefreshBaldrickAsync();
        _baldrickTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _baldrickTimer.Tick += async (_, _) => await RefreshBaldrickAsync();
        _baldrickTimer.Start();
    }

    static SolidColorBrush Brush(string hex) =>
        new((Color)ColorConverter.ConvertFromString(hex));

    async Task RefreshBaldrickAsync()
    {
        var secret = BaldrickSecret();
        if (secret.Length == 0)
        {
            BaldrickStatus.Text = "No secret — check settings key baldrick_secret_file";
            return;
        }
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, BaldrickUrl + "/api/v1/percy/jobs");
            req.Headers.Add("x-production-secret", secret);
            using var res = await BaldrickHttp.SendAsync(req);
            if (!res.IsSuccessStatusCode)
            {
                BaldrickStatus.Text = $"Baldrick said {(int)res.StatusCode} — check the secret/url";
                return;
            }
            var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            var cards = new List<BaldrickCard>();

            // ── Batch stories: what's happening, per client, in a sentence ──
            if (doc.RootElement.TryGetProperty("production", out var prod) &&
                prod.TryGetProperty("batches", out var batches))
            {
                foreach (var b in batches.EnumerateObject())
                {
                    int V(string k) => b.Value.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
                    var pending = V("pending"); var review = V("awaiting_approval"); var generating = V("generating"); var failed = V("failed");
                    string state; SolidColorBrush brush;
                    if (generating > 0) { state = "RENDERING"; brush = Brush("#E0B45A"); }
                    else if (b.Value.TryGetProperty("latestMove", out var lm) && lm.ValueKind == JsonValueKind.String
                             && DateTime.TryParse(lm.GetString(), out var mv)
                             && (DateTime.UtcNow - mv.ToUniversalTime()).TotalMinutes < 10) { state = "MOVING"; brush = Brush("#4FC38A"); }
                    else if (pending > 0) { state = "HELD"; brush = Brush("#9BA3B0"); }
                    else { state = "QUIET"; brush = Brush("#4FC38A"); }
                    cards.Add(new BaldrickCard
                    {
                        Badge = state, BadgeBrush = brush,
                        Title = $"{b.Name} renders",
                        Sub = $"{pending} queued · {generating} rendering · {review} waiting for your review" + (failed > 0 ? $" · {failed} failed" : ""),
                    });
                }
            }

            // ── Needs you: finished runs awaiting the yes; failed runs ──────
            var label = "";
            foreach (var it in doc.RootElement.GetProperty("items").EnumerateArray())
            {
                var kind = it.GetProperty("kind").GetString() ?? "";
                var process = it.GetProperty("process").GetString() ?? "";
                var client = it.GetProperty("client").GetString() ?? "";
                var err = it.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : "";
                label = kind switch
                {
                    "awaiting-decision" => "NEEDS YOUR YES",
                    "failed" => "RUN FAILED",
                    "running" => "THINKING",
                    _ => "QUEUED RUN",
                };
                cards.Add(new BaldrickCard
                {
                    Id = it.GetProperty("id").GetString() ?? "",
                    Badge = label,
                    BadgeBrush = kind == "awaiting-decision" ? Brush("#6AA8E0") : kind == "failed" ? Brush("#E05A5A") : Brush("#9BA3B0"),
                    Title = $"{process} — {client}",
                    Sub = err,
                    CanApprove = kind == "awaiting-decision" ? Visibility.Visible : Visibility.Collapsed,
                    CanReject = kind == "awaiting-decision" ? Visibility.Visible : Visibility.Collapsed,
                    CanRetry = kind == "failed" ? Visibility.Visible : Visibility.Collapsed,
                    CanCancel = kind is "failed" or "queued" ? Visibility.Visible : Visibility.Collapsed,
                    CanOpen = Visibility.Visible,
                    Link = it.GetProperty("link").GetString() ?? "/ops/processes",
                    ActApprove = "approve", ActReject = "reject", ActRetry = "retry", ActCancel = "cancel",
                });
            }

            if (doc.RootElement.TryGetProperty("production", out var prod2))
            {
                // 3-strike failures: the only production items a human sees
                if (prod2.TryGetProperty("deadJobs", out var dead))
                {
                    foreach (var d in dead.EnumerateArray())
                    {
                        cards.Add(new BaldrickCard
                        {
                            Id = d.GetProperty("id").GetString() ?? "",
                            Badge = "3 STRIKES",
                            BadgeBrush = Brush("#E05A5A"),
                            Title = $"{(d.TryGetProperty("jobCode", out var jc) ? jc.GetString() : null) ?? "render"} — {d.GetProperty("client").GetString()}",
                            Sub = d.TryGetProperty("error", out var de) && de.ValueKind == JsonValueKind.String ? de.GetString() ?? "" : "",
                            CanRetry = Visibility.Visible, CanCancel = Visibility.Visible, CanOpen = Visibility.Visible,
                            Link = "/produce",
                            ActRetry = "retry-production", ActCancel = "abandon-production",
                        });
                    }
                }

                // Rendering now + next up, with checklist progress
                if (prod2.TryGetProperty("lineup", out var lineup))
                {
                    var shown = 0; var total = 0;
                    foreach (var l in lineup.EnumerateArray())
                    {
                        total++;
                        if (shown >= 8) continue;
                        shown++;
                        var st = l.GetProperty("status").GetString() ?? "";
                        var ticked = new List<string>(); string next = "render";
                        if (l.TryGetProperty("stepMarks", out var marks) && marks.ValueKind == JsonValueKind.Object)
                        {
                            foreach (var s in StepOrder)
                                if (marks.TryGetProperty(s, out _)) ticked.Add(s);
                            next = StepOrder.FirstOrDefault(s => !ticked.Contains(s)) ?? "done";
                        }
                        cards.Add(new BaldrickCard
                        {
                            Id = l.GetProperty("id").GetString() ?? "",
                            Badge = st == "generating" ? "RENDERING NOW" : "NEXT UP",
                            BadgeBrush = st == "generating" ? Brush("#E0B45A") : Brush("#3B4452"),
                            Title = l.GetProperty("item").GetString() ?? "",
                            Sub = $"{l.GetProperty("workflow").GetString()}   ·   steps done: {ticked.Count}/{StepOrder.Length}, next: {next}",
                        });
                    }
                    if (total > shown)
                        cards.Add(new BaldrickCard { Badge = "QUEUE", BadgeBrush = Brush("#3B4452"), Title = $"…and {total - shown} more waiting", Sub = "The full line-up lives in Baldrick → Produce." });
                }
            }

            BaldrickCards.ItemsSource = cards;
            var chips = new List<string>();
            foreach (var q in doc.RootElement.GetProperty("queues").EnumerateArray())
            {
                var n = q.GetProperty("count").GetInt32();
                if (n > 0) chips.Add($"{q.GetProperty("label").GetString()}: {n}");
            }
            var needsYou = cards.Count(c => c.Badge is "NEEDS YOUR YES" or "3 STRIKES");
            BaldrickStatus.Text = (needsYou > 0 ? $"{needsYou} thing{(needsYou == 1 ? "" : "s")} need you" : "Nothing needs you")
                + (chips.Count > 0 ? "  ·  " + string.Join("  ·  ", chips) : "")
                + $"  ·  as of {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            BaldrickStatus.Text = "Couldn't reach Baldrick: " + ex.Message;
        }
    }

    async Task ActBaldrickCardAsync(string id, string action)
    {
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(action)) return;
        var note = BaldrickReason.Text.Trim();
        if (action is "reject" or "abandon-production" && note.Length == 0)
        {
            BaldrickStatus.Text = "A rejection owes a reason — type it in the reason box first.";
            return;
        }
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, BaldrickUrl + "/api/v1/percy/act");
            req.Headers.Add("x-production-secret", BaldrickSecret());
            req.Content = new StringContent(
                JsonSerializer.Serialize(new { runId = id, action, note }),
                Encoding.UTF8, "application/json");
            using var res = await BaldrickHttp.SendAsync(req);
            var body = await res.Content.ReadAsStringAsync();
            BaldrickStatus.Text = res.IsSuccessStatusCode ? $"{action} ✓" : $"Baldrick said {(int)res.StatusCode}: {body}";
            BaldrickReason.Text = "";
            await RefreshBaldrickAsync();
        }
        catch (Exception ex)
        {
            BaldrickStatus.Text = "Action failed: " + ex.Message;
        }
    }

    BaldrickCard? CardOf(object sender) => (sender as FrameworkElement)?.Tag as BaldrickCard;

    void BaldrickCardApprove_Click(object sender, RoutedEventArgs e) { var c = CardOf(sender); if (c != null) _ = ActBaldrickCardAsync(c.Id, c.ActApprove); }
    void BaldrickCardReject_Click(object sender, RoutedEventArgs e) { var c = CardOf(sender); if (c != null) _ = ActBaldrickCardAsync(c.Id, c.ActReject); }
    void BaldrickCardRetry_Click(object sender, RoutedEventArgs e) { var c = CardOf(sender); if (c != null) _ = ActBaldrickCardAsync(c.Id, c.ActRetry); }
    void BaldrickCardCancel_Click(object sender, RoutedEventArgs e) { var c = CardOf(sender); if (c != null) _ = ActBaldrickCardAsync(c.Id, c.ActCancel); }
    void BaldrickCardOpen_Click(object sender, RoutedEventArgs e)
    {
        var c = CardOf(sender);
        Process.Start(new ProcessStartInfo(BaldrickUrl + (c?.Link ?? "/ops/processes")) { UseShellExecute = true });
    }
    void BaldrickRefresh_Click(object sender, RoutedEventArgs e) => _ = RefreshBaldrickAsync();
}

public class BaldrickCard
{
    public string Id { get; set; } = "";
    public string Badge { get; set; } = "";
    public SolidColorBrush BadgeBrush { get; set; } = new(Colors.Gray);
    public string Title { get; set; } = "";
    public string Sub { get; set; } = "";
    public string Link { get; set; } = "";
    public string ActApprove { get; set; } = "approve";
    public string ActReject { get; set; } = "reject";
    public string ActRetry { get; set; } = "retry";
    public string ActCancel { get; set; } = "cancel";
    public Visibility CanApprove { get; set; } = Visibility.Collapsed;
    public Visibility CanReject { get; set; } = Visibility.Collapsed;
    public Visibility CanRetry { get; set; } = Visibility.Collapsed;
    public Visibility CanCancel { get; set; } = Visibility.Collapsed;
    public Visibility CanOpen { get; set; } = Visibility.Collapsed;
}
