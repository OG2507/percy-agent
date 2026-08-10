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
using System.Windows.Threading;

namespace PercyAgent;

// The Baldrick tab: Percy is the CONTROLLER for jobs (Stephen's design,
// 2026-08-09). This polls Baldrick's /api/v1/percy/jobs (outbound only, the
// worker's secret) and shows job state: queued / running / failed runs and
// finished runs awaiting the YES, plus the decision-queue counts. Verbs:
// Approve, Reject (reason required), Retry, Cancel, Open (deep link to the
// workbench page). Baldrick is where the detail lives; this is where the
// jobs live.
public partial class MainWindow
{
    static readonly HttpClient BaldrickHttp = new() { Timeout = TimeSpan.FromSeconds(20) };
    DispatcherTimer? _baldrickTimer;
    List<BaldrickJob> _baldrickJobs = new();

    string BaldrickUrl => Store.Setting("baldrick_url", "https://baldrick.vslmedia.co.uk").TrimEnd('/');

    string BaldrickSecret()
    {
        var path = Store.Setting("baldrick_secret_file",
            @"C:\ComfyUI\ComfyUI\user\default\workflows\baldrick-tools\.worker_secret");
        try { return File.ReadAllText(path).Trim(); }
        catch { return ""; }
    }

    void StartBaldrickPolling()
    {
        _ = RefreshBaldrickAsync();
        _baldrickTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _baldrickTimer.Tick += async (_, _) => await RefreshBaldrickAsync();
        _baldrickTimer.Start();
    }

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
            var jobs = new List<BaldrickJob>();
            foreach (var it in doc.RootElement.GetProperty("items").EnumerateArray())
            {
                jobs.Add(new BaldrickJob
                {
                    Id = it.GetProperty("id").GetString() ?? "",
                    Kind = it.GetProperty("kind").GetString() ?? "",
                    Process = it.GetProperty("process").GetString() ?? "",
                    Client = it.GetProperty("client").GetString() ?? "",
                    QueuedAt = it.GetProperty("queuedAt").GetString() ?? "",
                    Error = it.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : "",
                    Link = it.GetProperty("link").GetString() ?? "/ops/processes",
                    Actions = string.Join(",", it.GetProperty("actions").EnumerateArray().Select(a => a.GetString())),
                });
            }
            var chips = new List<string>();

            // Production batches: the render pipeline per client, straight from the
            // system (Stephen 2026-08-11: Percy watches renders, not a chat window).
            if (doc.RootElement.TryGetProperty("production", out var prod))
            {
                if (prod.TryGetProperty("batches", out var batches))
                {
                    foreach (var b in batches.EnumerateObject())
                    {
                        int V(string k) => b.Value.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
                        var moving = b.Value.TryGetProperty("latestMove", out var lm) && lm.ValueKind == JsonValueKind.String
                            && DateTime.TryParse(lm.GetString(), out var mv)
                            ? (DateTime.UtcNow - mv.ToUniversalTime()).TotalMinutes < 10 ? "rendering" : $"stalled since {mv:HH:mm}"
                            : "idle";
                        chips.Add($"{b.Name} renders: {V("pending")} queued, {V("awaiting_approval")} to review ({moving})");
                    }
                }
                // The line-up itself: what's rendering and what's next, each row
                // named (product · colour) with the workflow it will run through
                // (format → method [recipe status]) so queue and machinery are
                // visibly matched — a count alone is not a queue.
                if (prod.TryGetProperty("lineup", out var lineup))
                {
                    // Join each job's METHOD to the local methods map so the
                    // Workflow column shows the actual file on this machine —
                    // the job and the thing that will run it, side by side.
                    var methodFiles = Store.Methods().ToDictionary(m => m.MethodKey, m => m.WorkflowFile);
                    foreach (var l in lineup.EnumerateArray())
                    {
                        var st = l.GetProperty("status").GetString() ?? "";
                        var method = l.TryGetProperty("method", out var me) && me.ValueKind == JsonValueKind.String ? me.GetString() ?? "" : "";
                        var recipeStatus = l.TryGetProperty("recipeStatus", out var rs) && rs.ValueKind == JsonValueKind.String ? rs.GetString() ?? "" : "";
                        var file = method.Length > 0 && methodFiles.TryGetValue(method, out var wf) && !string.IsNullOrEmpty(wf) ? wf : "";
                        var workflow = method.Length == 0
                            ? "NO METHOD — job can never render"
                            : $"{method} → {(file.Length > 0 ? file : "(no workflow file — toolbox steps only)")}" +
                              (recipeStatus.Length > 0 && recipeStatus != "proven" ? $" [{recipeStatus}]" : "");
                        jobs.Add(new BaldrickJob
                        {
                            Id = l.GetProperty("id").GetString() ?? "",
                            Kind = st == "generating" ? "rendering NOW" : "render queued",
                            Process = l.GetProperty("item").GetString() ?? "",
                            Workflow = workflow,
                            Client = l.GetProperty("client").GetString() ?? "",
                            QueuedAt = l.TryGetProperty("startedAt", out var sa) && sa.ValueKind == JsonValueKind.String ? sa.GetString() ?? "" : "",
                            Link = "/produce",
                            Actions = "",
                        });
                    }
                }
                if (prod.TryGetProperty("deadJobs", out var dead))
                {
                    foreach (var d in dead.EnumerateArray())
                    {
                        jobs.Add(new BaldrickJob
                        {
                            Id = d.GetProperty("id").GetString() ?? "",
                            Kind = "production-failed",
                            Process = "render (3 strikes)",
                            Client = d.GetProperty("client").GetString() ?? "",
                            QueuedAt = d.TryGetProperty("failedAt", out var fa) ? fa.GetString() ?? "" : "",
                            Error = d.TryGetProperty("error", out var de) && de.ValueKind == JsonValueKind.String ? de.GetString() ?? "" : "",
                            Link = "/produce",
                            Actions = "retry,cancel", // mapped to retry-production / abandon-production on send
                        });
                    }
                }
            }

            _baldrickJobs = jobs;
            BaldrickGrid.ItemsSource = jobs;

            foreach (var q in doc.RootElement.GetProperty("queues").EnumerateArray())
            {
                var n = q.GetProperty("count").GetInt32();
                if (n > 0) chips.Add($"{q.GetProperty("label").GetString()}: {n}");
            }
            var waiting = jobs.Count(j => j.Kind == "awaiting-decision");
            var failed = jobs.Count(j => j.Kind == "failed");
            BaldrickStatus.Text =
                $"{jobs.Count} jobs ({waiting} awaiting your yes, {failed} failed)" +
                (chips.Count > 0 ? "  ·  " + string.Join("  ·  ", chips) : "") +
                $"  ·  as of {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            BaldrickStatus.Text = "Couldn't reach Baldrick: " + ex.Message;
        }
    }

    async Task ActBaldrickAsync(string action)
    {
        if (BaldrickGrid.SelectedItem is not BaldrickJob job)
        {
            BaldrickStatus.Text = "Pick a job in the list first.";
            return;
        }
        if (!job.Actions.Split(',').Contains(action))
        {
            BaldrickStatus.Text = $"'{action}' doesn't apply to a {job.Kind} job.";
            return;
        }
        var note = BaldrickReason.Text.Trim();
        if (action == "reject" && note.Length == 0)
        {
            BaldrickStatus.Text = "A rejection owes a reason — type it in the reason box.";
            return;
        }
        // A production-failed row reuses the Retry/Cancel buttons but speaks the
        // production verbs to the API (cancel = abandon, reason box respected).
        var sendAction = job.Kind == "production-failed"
            ? (action == "retry" ? "retry-production" : "abandon-production")
            : action;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, BaldrickUrl + "/api/v1/percy/act");
            req.Headers.Add("x-production-secret", BaldrickSecret());
            req.Content = new StringContent(
                JsonSerializer.Serialize(new { runId = job.Id, action = sendAction, note }),
                Encoding.UTF8, "application/json");
            using var res = await BaldrickHttp.SendAsync(req);
            var body = await res.Content.ReadAsStringAsync();
            BaldrickStatus.Text = res.IsSuccessStatusCode
                ? $"{action} → {job.Process} ({job.Client})"
                : $"Baldrick said {(int)res.StatusCode}: {body}";
            BaldrickReason.Text = "";
            await RefreshBaldrickAsync();
        }
        catch (Exception ex)
        {
            BaldrickStatus.Text = "Action failed: " + ex.Message;
        }
    }

    void BaldrickRefresh_Click(object sender, RoutedEventArgs e) => _ = RefreshBaldrickAsync();
    void BaldrickApprove_Click(object sender, RoutedEventArgs e) => _ = ActBaldrickAsync("approve");
    void BaldrickReject_Click(object sender, RoutedEventArgs e) => _ = ActBaldrickAsync("reject");
    void BaldrickRetry_Click(object sender, RoutedEventArgs e) => _ = ActBaldrickAsync("retry");
    void BaldrickCancel_Click(object sender, RoutedEventArgs e) => _ = ActBaldrickAsync("cancel");

    void BaldrickOpen_Click(object sender, RoutedEventArgs e)
    {
        var link = BaldrickGrid.SelectedItem is BaldrickJob job ? job.Link : "/ops/processes";
        Process.Start(new ProcessStartInfo(BaldrickUrl + link) { UseShellExecute = true });
    }
}

public class BaldrickJob
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Process { get; set; } = "";
    public string Workflow { get; set; } = "";
    public string Client { get; set; } = "";
    public string QueuedAt { get; set; } = "";
    public string Error { get; set; } = "";
    public string Link { get; set; } = "";
    public string Actions { get; set; } = "";
}
