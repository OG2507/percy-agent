using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace PercyAgent;

// The Run tab: the app as an extension of the website (Stephen, 2026-08-15:
// "I still can't go in and run things like the 12 month plan in it").
//
// GET  /api/v1/percy/processes says what Baldrick can run and for whom.
// POST /api/v1/percy/run queues one; the runner on the server picks it up.
// The server owns the list — nothing here hardcodes a process key, so adding
// a process on the server adds it here on Reload, not on a rebuild.
public partial class MainWindow
{
    public sealed class RunProcessRow
    {
        public string Key { get; set; } = "";
        public string Label { get; set; } = "";
        public string Description { get; set; } = "";
    }

    public sealed class RunGroupRow
    {
        public string Name { get; set; } = "";
        public List<RunProcessRow> Processes { get; set; } = [];
    }

    public sealed class RunClientRow
    {
        public string Code { get; set; } = "";
        public string Name { get; set; } = "";
        public override string ToString() => $"{Name}  ({Code})";
    }

    async Task LoadProcessesAsync()
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, BaldrickUrl + "/api/v1/percy/processes");
            req.Headers.Add("x-production-secret", baldrick.WorkerSecret);
            using var res = await BaldrickHttp.SendAsync(req);
            var body = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode)
            {
                RunFail($"Baldrick said {(int)res.StatusCode}: {ErrorOf(body)}");
                return;
            }

            using var doc = JsonDocument.Parse(body);
            var data = doc.RootElement.GetProperty("data");

            // Groups in the order the server sends them — it knows the shape of its own list.
            var groups = new List<RunGroupRow>();
            foreach (var p in data.GetProperty("processes").EnumerateArray())
            {
                var g = p.TryGetProperty("process_group", out var pg) ? pg.GetString() ?? "other" : "other";
                var group = groups.FirstOrDefault(x => x.Name == g);
                if (group is null) groups.Add(group = new RunGroupRow { Name = g });
                group.Processes.Add(new RunProcessRow
                {
                    Key = p.GetProperty("key").GetString() ?? "",
                    Label = p.TryGetProperty("label", out var l) ? l.GetString() ?? "" : "",
                    Description = p.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "",
                });
            }
            RunGroups.ItemsSource = groups;

            // Keep the chosen client across a reload — losing the selection makes
            // Reload feel like it undid something.
            var chosen = (RunClientBox.SelectedItem as RunClientRow)?.Code;
            var clients = new List<RunClientRow>();
            foreach (var cl in data.GetProperty("clients").EnumerateArray())
                clients.Add(new RunClientRow
                {
                    Code = cl.GetProperty("code").GetString() ?? "",
                    Name = cl.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                });
            RunClientBox.ItemsSource = clients;
            RunClientBox.SelectedItem = clients.FirstOrDefault(x => x.Code == chosen) ?? clients.FirstOrDefault();

            RunStatus.Text = $"{groups.Sum(g => g.Processes.Count)} processes · {clients.Count} clients · pick a client, press Run";
            RunStatus.Foreground = Brush("#8B93A1");
        }
        catch (Exception ex)
        {
            RunFail("Couldn't load the process list: " + ex.Message);
        }
    }

    void ReloadProcesses_Click(object sender, RoutedEventArgs e) => _ = LoadProcessesAsync();

    async void RunProcess_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not RunProcessRow p) return;
        if (RunClientBox.SelectedItem is not RunClientRow client)
        {
            RunFail("▲  Pick a client first — every run belongs to one client.");
            RunClientBox.Focus();
            return;
        }

        var btn = sender as Button;
        if (btn != null) btn.IsEnabled = false;   // one press, one run — no double-queue on a slow network
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, BaldrickUrl + "/api/v1/percy/run");
            req.Headers.Add("x-production-secret", baldrick.WorkerSecret);
            req.Content = new StringContent(
                JsonSerializer.Serialize(new { process_key = p.Key, client_code = client.Code }),
                Encoding.UTF8, "application/json");
            using var res = await BaldrickHttp.SendAsync(req);
            var body = await res.Content.ReadAsStringAsync();
            if (res.IsSuccessStatusCode)
            {
                // The server's own note says what happens next (the runner picks
                // it up within ~45s; the result arrives at System → Run approvals).
                string note = "";
                try
                {
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("data", out var d) &&
                        d.TryGetProperty("note", out var n) && n.ValueKind == JsonValueKind.String)
                        note = n.GetString() ?? "";
                }
                catch { /* a success without a readable note is still a success */ }
                RunStatus.Text = $"{p.Label} — queued for {client.Code} ✓" + (note.Length > 0 ? $"  ·  {note}" : "");
                RunStatus.Foreground = Brush("#5FBF8F");
            }
            else
            {
                // The server's words, verbatim. A bare "failed" that hides the
                // reason is the failure this project exists to kill.
                RunFail($"Baldrick said {(int)res.StatusCode}: {ErrorOf(body)}");
            }
        }
        catch (Exception ex)
        {
            RunFail($"{p.Label} — couldn't reach Baldrick: {ex.Message}");
        }
        finally
        {
            if (btn != null) btn.IsEnabled = true;
        }
    }

    void RunFail(string message)
    {
        RunStatus.Text = message;
        RunStatus.Foreground = Brush("#E05A5A");
    }

    /// <summary>The server's error text if the body is {"error":"…"}, otherwise the raw body.</summary>
    static string ErrorOf(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String)
                return e.GetString() ?? body;
        }
        catch { /* not JSON — the raw body is the message */ }
        return body;
    }
}
