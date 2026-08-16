using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

namespace PercyAgent;

// PLAY and PAUSE for the one queue.
//
// Play  — starts percy_run.py. It asks Baldrick for its next job, does that
//         one task, reports it with evidence, and goes round again until
//         there is nothing left or it is paused.
// Pause — writes a PAUSE file. Percy finishes the job it is on, reports it,
//         and then stops. It is never killed mid-job, so nothing is ever
//         left half-done with no paper trail.
//
// What it shows is read from the status file the runner writes as it goes.
// If that file goes stale, this says "not running" rather than guessing —
// a bar that claims Percy is working when it died an hour ago is worse than
// no bar at all.
public partial class MainWindow
{
    const string BaldrickRoot = @"D:\ComfyUI-Data\baldrick";
    static string PauseFile => Path.Combine(BaldrickRoot, "PAUSE");
    static string StatusFile => Path.Combine(BaldrickRoot, "percy_status.json");
    static string RunnerScript => Path.Combine(BaldrickRoot, @"workflows\python\percy_run.py");

    // Anything older than this and we stop believing it.
    static readonly TimeSpan Stale = TimeSpan.FromMinutes(3);

    DispatcherTimer? _runnerTimer;

    void StartRunnerWatch()
    {
        RefreshRunner();
        _runnerTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _runnerTimer.Tick += (_, _) => RefreshRunner();
        _runnerTimer.Start();
    }

    void RefreshRunner()
    {
        var paused = File.Exists(PauseFile);
        string state = "stopped", detail = "", ago = "";

        try
        {
            if (File.Exists(StatusFile))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(StatusFile));
                var r = doc.RootElement;
                state = r.TryGetProperty("state", out var s) ? (s.GetString() ?? "stopped") : "stopped";
                detail = r.TryGetProperty("detail", out var d) ? (d.GetString() ?? "") : "";
                if (r.TryGetProperty("at", out var a) && DateTime.TryParse(a.GetString(), out var at))
                {
                    var age = DateTime.Now - at;
                    if (age > Stale && state != "stopped" && state != "paused")
                    {
                        // It said it was working, then went quiet. Say that plainly.
                        state = "stopped";
                        detail = $"went quiet {(int)age.TotalMinutes} min ago while: {detail}";
                    }
                    else ago = age.TotalSeconds < 90 ? "" : $"  ({(int)age.TotalMinutes} min ago)";
                }
            }
        }
        catch { /* an unreadable status file means we do not know — treat as stopped */ }

        if (paused && state != "working") { state = "paused"; if (detail.Length == 0) detail = "press Play to start again"; }

        var (label, colour) = state switch
        {
            "working"  => ("Percy: WORKING", "#2E7D32"),
            "waiting"  => ("Percy: WAITING", "#B26A00"),
            "idle"     => ("Percy: RUNNING", "#2E7D32"),
            "starting" => ("Percy: STARTING", "#2E7D32"),
            "paused"   => ("Percy: PAUSED", "#B26A00"),
            _          => ("Percy: NOT RUNNING", "#8C2F2F"),
        };

        RunnerState.Text = label;
        RunnerState.Foreground = Brush(colour);
        RunnerDoing.Text = detail + ago;

        var running = state is "working" or "waiting" or "idle" or "starting";
        RunnerPlay.IsEnabled = !running;
        RunnerPause.IsEnabled = running && !paused;
        RunnerStop.IsEnabled = running || EngineUp();

        var engineUp = EngineUp();
        EngineState.Text = engineUp ? "engine: up" : "engine: off";
        EngineState.Foreground = Brush(engineUp ? "#2E7D32" : "#8C2F2F");

        // THE SUPERVISOR LIVES HERE NOW (the bat loop is retired): while Play
        // is on and not paused, a queue that went quiet is started again once
        // the engine answers. percy_run's single-instance guard makes an
        // accidental double-spawn exit harmlessly. At most one spawn per two
        // minutes, so a crashing runner cannot be hammered.
        if (_keepQueueAlive && !paused && !running && engineUp
            && DateTime.Now - _lastQueueSpawn > TimeSpan.FromMinutes(2))
        {
            SpawnQueue();
            RunnerState.Text = "Percy: STARTING";
            RunnerState.Foreground = Brush("#2E7D32");
            RunnerDoing.Text = "the queue went quiet — starting it again";
        }
        else if (_keepQueueAlive && !paused && !running && !engineUp)
        {
            RunnerState.Text = "Percy: WAITING FOR THE ENGINE";
            RunnerState.Foreground = Brush("#B26A00");
            RunnerDoing.Text = "the engine is loading its models — the queue starts itself when it answers";
        }
    }

    void RunnerPlay_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // Clearing the pause is part of pressing Play — otherwise Percy
            // would start and immediately stop again.
            if (File.Exists(PauseFile)) File.Delete(PauseFile);

            if (!File.Exists(RunnerScript))
            {
                RunnerState.Text = "Percy: CANNOT START";
                RunnerState.Foreground = Brush("#8C2F2F");
                RunnerDoing.Text = "the runner is not at " + RunnerScript;
                return;
            }

            // ONE BUTTON, EVERYTHING: the engine first (hidden, if it is not
            // already up), then the queue. If the engine is still loading its
            // models, the keep-alive starts the queue the moment it answers.
            _keepQueueAlive = true;
            var engineWasUp = EngineUp();
            StartEngineIfDown();
            if (engineWasUp)
            {
                SpawnQueue();
                RunnerState.Text = "Percy: STARTING";
                RunnerDoing.Text = "asking Baldrick for work";
            }
            else
            {
                RunnerState.Text = "Percy: WAKING THE ENGINE";
                RunnerDoing.Text = "loading models takes a few minutes — the queue starts itself when the engine answers";
            }
            RunnerState.Foreground = Brush("#2E7D32");
            RunnerPlay.IsEnabled = false;
        }
        catch (Exception ex)
        {
            RunnerState.Text = "Percy: CANNOT START";
            RunnerState.Foreground = Brush("#8C2F2F");
            RunnerDoing.Text = ex.Message;
        }
    }

    void RunnerPause_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _keepQueueAlive = false;
            File.WriteAllText(PauseFile, "paused from Percy Agent " + DateTime.Now.ToString("s"));
            RunnerState.Text = "Percy: PAUSING";
            RunnerState.Foreground = Brush("#B26A00");
            RunnerDoing.Text = "finishing the job it is on, then stopping (the engine stays warm — Stop gives the machine back)";
            RunnerPause.IsEnabled = false;
        }
        catch (Exception ex)
        {
            RunnerDoing.Text = "could not pause: " + ex.Message;
        }
    }

    void RunnerStop_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            StopEverything();
            RunnerState.Text = "Percy: STOPPED";
            RunnerState.Foreground = Brush("#8C2F2F");
            RunnerDoing.Text = "everything stopped, graphics memory freed — the machine is yours";
            RunnerStop.IsEnabled = false;
            RunnerPlay.IsEnabled = true;
        }
        catch (Exception ex)
        {
            RunnerDoing.Text = "could not stop: " + ex.Message;
        }
    }
}
