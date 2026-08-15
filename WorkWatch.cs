using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

namespace PercyAgent;

// The QUEUE WATCHER on the Work tab: who is the queue waiting on, and is
// anything on fire. Three truths, cross-checked:
//
//   1. Baldrick's /api/v1/percy/queue-health — the queue by whose move it is.
//   2. percy_status.json  — what the runner on THIS machine says it is doing.
//   3. comfy_status.json  — what the Comfy supervisor says about the engine.
//
// The alarm banner exists for when these DISAGREE: Baldrick says twelve jobs
// wait on Percy while the status file says Percy stopped an hour ago. Each of
// those alone looks fine; together they mean nothing is being made and nobody
// is being told. This tab is where somebody gets told.
public partial class MainWindow
{
    static string ComfyStatusFile => Path.Combine(BaldrickRoot, "comfy_status.json");

    // Both status files are written on every change and on exit, so a file
    // older than this is a memory, not a report — its writer is gone.
    static readonly TimeSpan WatchStale = TimeSpan.FromMinutes(5);

    DispatcherTimer? _workTimer;

    void StartWorkWatch()
    {
        // The first read happens with the window's Loaded refresh; this keeps
        // it honest every minute after that. Same cadence as the Baldrick tab.
        _workTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _workTimer.Tick += async (_, _) => await RefreshWorkAsync();
        _workTimer.Start();
    }

    sealed record FileStatus(string State, string Detail, bool Stale);

    static FileStatus ReadStatusFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return new("missing", "", true);
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var r = doc.RootElement;
            var state = r.TryGetProperty("state", out var s) ? s.GetString() ?? "" : "";
            var detail = r.TryGetProperty("detail", out var d) ? d.GetString() ?? "" : "";
            var stale = !(r.TryGetProperty("at", out var a) && DateTime.TryParse(a.GetString(), out var at))
                        || DateTime.Now - at > WatchStale;
            return new(state, detail, stale);
        }
        catch { return new("unreadable", "", true); }   // can't read it = can't trust it
    }

    async Task RefreshQueueHealthAsync()
    {
        try
        {
            var h = await baldrick.GetQueueHealthAsync();
            var percy = ReadStatusFile(StatusFile);
            var comfy = ReadStatusFile(ComfyStatusFile);

            // ── The card: one line per owner, plain sentences ───────────────
            if (h.Error is not null)
            {
                // The real reason, in the card — never blank, never swallowed.
                QueueByOwner.Text = "Couldn't read the queue: " + h.Error;
            }
            else
            {
                var lines = h.ByOwner.Select(OwnerLine).ToList();
                if (lines.Count == 0) lines.Add("The queue is empty — nothing is waiting on anyone.");
                if (h.MinutesSincePercyTouched is int m)
                    lines.Add($"Percy last touched a job {m} min ago.");
                QueueByOwner.Text = string.Join("\n", lines);
            }

            // ── The alarms: truths that conflict ────────────────────────────
            // Red is for things a person should act on NOW; amber is for jobs
            // that are broken data rather than a broken machine.
            var red = new List<string>();
            var amber = new List<string>();

            var pRow = h.ByOwner.FirstOrDefault(o => o.Owner == "percy");
            if (pRow is not null && pRow.Jobs > pRow.ThreeStrikes
                && (percy.Stale || percy.State == "paused"))
            {
                // Struck-out jobs wait on a human decision, not on Percy — so
                // only alarm when there are jobs Percy could actually take.
                var how = !percy.Stale && percy.State == "paused" ? "paused" : "not running";
                red.Add($"{pRow.Jobs} jobs wait on Percy but Percy is {how} — press Run Percy / Play.");
            }

            if (comfy.State == "wrong-engine")
                red.Add(comfy.Detail.Length > 0 ? comfy.Detail
                    : "ComfyUI is running the wrong engine.");   // the supervisor's words, verbatim, when it gave any

            if (comfy.State == "down" || comfy.Stale)
                red.Add("ComfyUI is down and its supervisor isn't reporting — renders cannot happen.");

            if (h.StuckGenerating.Count > 0)
                red.Add($"{h.StuckGenerating.Count} jobs stuck mid-render over 30 minutes: "
                    + string.Join(", ", h.StuckGenerating.Select(j => j.JobCode)) + ".");

            if (h.CannotMove.Count > 0)
                amber.Add($"{h.CannotMove.Count} jobs can never move: "
                    + string.Join("; ", h.CannotMove.Take(3).Select(j => $"{j.JobCode} — {j.Why}"))
                    + (h.CannotMove.Count > 3 ? "; …" : "") + ".");

            if (red.Count == 0 && amber.Count == 0)
            {
                QueueAlarm.Visibility = Visibility.Collapsed;
            }
            else
            {
                var urgent = red.Count > 0;
                QueueAlarm.Background = Brush(urgent ? "#2A1518" : "#2A2315");
                QueueAlarm.BorderBrush = Brush(urgent ? "#E05A5A" : "#B26A00");
                QueueAlarmText.Foreground = Brush(urgent ? "#E05A5A" : "#E0B45A");
                QueueAlarmText.Text = string.Join("\n", red.Concat(amber));
                QueueAlarm.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex)
        {
            // Nothing on this tab is worth losing the window over — and even a
            // bug in the watcher itself must say its name in the card.
            QueueByOwner.Text = "The queue watcher itself failed: " + ex.Message;
        }
    }

    static string OwnerLine(Baldrick.OwnerLoad o)
    {
        var oldest = o.StalestHours >= 48 ? $"{o.StalestHours / 24:0} days"
                   : o.StalestHours >= 1 ? $"{o.StalestHours:0} h"
                   : "under an hour";
        var strikes = o.ThreeStrikes > 0 ? $", {o.ThreeStrikes} struck out" : "";

        // The server's parenthesised owner means the job has nowhere to go at
        // all — say that plainly rather than "waiting on (finished all…)".
        if (o.Owner.StartsWith("("))
            return $"{o.Jobs} with no next move — finished all steps or no method (oldest {oldest} old{strikes})";

        var who = o.Owner switch
        {
            "percy"    => "Percy",
            "baldrick" => "Baldrick",
            "human"    => "you",
            _          => o.Owner,
        };
        return $"{o.Jobs} waiting on {who} (oldest {oldest} old{strikes})";
    }
}
