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
                    string state; SolidColorBrush brush; string hint;
                    if (generating > 0) { state = "RENDERING"; brush = Brush("#E0B45A"); hint = "The 5090 is working through the queue."; }
                    else if (b.Value.TryGetProperty("latestMove", out var lm) && lm.ValueKind == JsonValueKind.String
                             && DateTime.TryParse(lm.GetString(), out var mv)
                             && (DateTime.UtcNow - mv.ToUniversalTime()).TotalMinutes < 10) { state = "MOVING"; brush = Brush("#4FC38A"); hint = "Something moved in the last 10 minutes."; }
                    else if (pending > 0) { state = "HELD"; brush = Brush("#9BA3B0"); hint = "Queued but not rendering — the worker isn't running (Stephen's order, or GPU busy). Jobs wait safely; they render when the worker next runs."; }
                    else { state = "QUIET"; brush = Brush("#4FC38A"); hint = "Nothing waiting to render."; }
                    cards.Add(new BaldrickCard
                    {
                        Badge = state, BadgeBrush = brush,
                        Title = $"{b.Name} renders",
                        Sub = $"{pending} queued · {generating} rendering · {review} waiting for your review" + (failed > 0 ? $" · {failed} failed" : "") + "\n" + hint
                              + (review > 0 ? "  →  Open goes to the approval queue." : ""),
                        CanOpen = Visibility.Visible,
                        Link = review > 0 ? "/produce/approvals" : "/produce",
                    });
                }
            }

            // ── BALDRICK'S OWN WORK IS NOT SHOWN HERE. ──────────────────────
            // Stephen, 2026-08-14: "things that should be in Percy are things
            // that Percy does. Nothing else... You would have a cue of what
            // Baldrick is needing to do as a next stage. That shouldn't come up
            // in the same place as Percy. That's really confusing."
            //
            // Right, and a divider was not enough — I tried that first and it
            // was still one list. Process runs are the SERVER thinking:
            // planning, analysing, writing. Nothing about them touches the
            // 5090, nothing about them is Percy's, and mixing them into his
            // queue is what made the queue unreadable. They live on the web at
            // /ops/processes until they have a place of their own.
            //
            // The feed still returns them; this app deliberately ignores them.
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
            // It used to say this in small grey text at the top of the page, so
            // pressing Abandon looked like it did nothing at all. Say it loudly,
            // and put the cursor where the answer goes.
            BaldrickStatus.Text = "▲  Type a reason in the box first — a rejection owes a reason.";
            BaldrickStatus.Foreground = Brush("#8C2F2F");
            BaldrickStatus.FontWeight = System.Windows.FontWeights.Bold;
            BaldrickReason.Focus();
            BaldrickReason.BorderBrush = Brush("#8C2F2F");
            return;
        }
        BaldrickStatus.FontWeight = System.Windows.FontWeights.Normal;
        BaldrickReason.BorderBrush = Brush("#2A3140");
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
    public string Detail { get; set; } = "";
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
