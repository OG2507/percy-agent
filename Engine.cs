using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;

namespace PercyAgent;

// THE ONE APP OWNS THE MACHINE (Stephen, 16 Aug 2026: "I always said it
// should be one piece of software for everything on the machine").
//
// Play starts the ENGINE (ComfyUI, hidden) and the QUEUE from here; the
// keep-alive below is the supervisor now — the bat loop is retired. Stop
// Everything gives the machine back: processes gone, graphics memory freed.
// No bats on the desk, no supervisors at login, no console windows. This
// app is the only switch, and what it says is read from live status, so
// closing and reopening the app never loses the truth.
public partial class MainWindow
{
    static string StartComfyBat => Path.Combine(BaldrickRoot, "start-comfy.bat");

    // Play sets it, Pause and Stop clear it. While it is true, a queue that
    // goes quiet is started again (percy_run's own single-instance guard
    // makes an accidental double-spawn exit harmlessly).
    bool _keepQueueAlive;
    DateTime _lastQueueSpawn = DateTime.MinValue;

    static bool EngineUp()
    {
        try
        {
            using var c = new TcpClient();
            return c.ConnectAsync("127.0.0.1", 8188).Wait(400) && c.Connected;
        }
        catch { return false; }
    }

    void StartEngineIfDown()
    {
        if (EngineUp()) return;
        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c \"" + StartComfyBat + "\"",
            WorkingDirectory = BaldrickRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
        });
    }

    void SpawnQueue()
    {
        var python = Store.Setting("python_exe", "pythonw.exe");
        Process.Start(new ProcessStartInfo
        {
            FileName = python,
            Arguments = "\"" + RunnerScript + "\"",
            WorkingDirectory = Path.GetDirectoryName(RunnerScript)!,
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        _lastQueueSpawn = DateTime.Now;
    }

    static void RunHidden(string file, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = file,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            p?.WaitForExit(15000);
        }
        catch { /* stopping must never throw the window down */ }
    }

    // The machine back in Stephen's hands: queue killed, engine killed,
    // graphics memory freed. Loud in the bar, silent on the desktop.
    void StopEverything()
    {
        _keepQueueAlive = false;
        try { if (File.Exists(PauseFile)) File.Delete(PauseFile); } catch { }
        RunHidden("powershell.exe",
            "-NoProfile -Command \"" +
            "Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -match 'percy_run\\.py' } | " +
            "ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }; " +
            "$c = Get-NetTCPConnection -LocalPort 8188 -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1; " +
            "if ($c) { Stop-Process -Id $c.OwningProcess -Force -ErrorAction SilentlyContinue }\"");
    }
}
