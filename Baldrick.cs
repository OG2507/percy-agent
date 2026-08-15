using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace PercyAgent;

/// <summary>
/// Talks to Baldrick and to the local machine, so the Work tab can show what is
/// waiting and start a production run.
///
/// Deliberately holds NO state of its own. Baldrick's queue is the only queue —
/// approve on the website or in here and both show the same thing, because
/// neither keeps a copy. Two lists that can disagree is the failure this avoids.
/// </summary>
public sealed class Baldrick
{
    public const string Api = "https://baldrick.vslmedia.co.uk";
    // Where the worker secret lives. The toolbox moved into workflows\python
    // and this path did not follow it, so every call went out with an empty
    // secret and came back 401 — for a whole day, saying only "Baldrick
    // returned 401" with no hint why. First one that exists wins, newest
    // first, and if none exist that is said out loud rather than guessed at.
    static readonly string[] SecretPaths =
    {
        @"D:\ComfyUI-Data\baldrick\workflows\python\.worker_secret",
        @"D:\ComfyUI-Data\baldrick\.worker_secret",
        @"C:\ComfyUI\ComfyUI\user\default\workflows\baldrick-tools\.worker_secret",
    };
    const string WorkerDir  = @"D:\ComfyUI-Data\baldrick";
    const string ComfyUrl   = "http://127.0.0.1:8188/system_stats";

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    string? secret;
    string Secret
    {
        get
        {
            if (secret != null) return secret;
            foreach (var p in SecretPaths)
                if (File.Exists(p)) return secret = File.ReadAllText(p).Trim();
            return secret = "";
        }
    }

    // The Run tab authenticates with the same secret this class already loads —
    // one loading mechanism, not a second copy with its own path that can go
    // stale (the day-long 401 above is what a second copy costs).
    public string WorkerSecret => Secret;

    public sealed record Status(
        int AwaitingApproval, int PendingLocal, int PendingCloud,
        bool ComfyUp, string? Error);

    /// <summary>Everything the Work tab needs, in one call. Never throws — a
    /// dead network shows as an error line, not a crashed window.</summary>
    public async Task<Status> GetStatusAsync()
    {
        bool comfy = await IsComfyUpAsync();
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{Api}/api/v1/production/jobs/awaiting-approval");
            req.Headers.Add("x-production-secret", Secret);
            var res = await Http.SendAsync(req);
            if (!res.IsSuccessStatusCode)
            {
                var why = (int)res.StatusCode == 401
                    ? Secret.Length == 0
                        ? "Baldrick refused us (401) — no worker secret found. Looked in: " + string.Join(" ; ", SecretPaths)
                        : "Baldrick refused us (401) — the worker secret we sent is not the one it expects"
                    : $"Baldrick returned {(int)res.StatusCode}";
                return new Status(0, 0, 0, comfy, why);
            }

            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            int awaiting = doc.RootElement.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Array
                ? d.GetArrayLength() : 0;
            return new Status(awaiting, 0, 0, comfy, null);
        }
        catch (Exception ex)
        {
            return new Status(0, 0, 0, comfy, ex.Message);
        }
    }

    public static async Task<bool> IsComfyUpAsync()
    {
        try
        {
            // FOUR SECONDS WAS A LIE DETECTOR FOR A SLOW ANSWER, NOT A DEAD ONE.
            // ComfyUI serves /system_stats on the same thread that is sampling
            // the card, so while the 5090 renders it can take many seconds to
            // reply. The app then announced "ComfyUI isn't working" about a
            // ComfyUI that was working hard. Stephen, 2026-08-14: "it's coming
            // up saying Comfy UI isn't working. I thought we'd already decided
            // this." Busy is not down.
            using var c = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            return (await c.GetAsync(ComfyUrl)).IsSuccessStatusCode;
        }
        catch { return false; }
    }

    /// <summary>
    /// Run Percy Worker, streaming its output back line by line.
    ///
    /// The GPU is on this machine, so the worker runs here — the app launches and
    /// watches it rather than replacing it. Same script the batch file runs, so
    /// there is one implementation, not two that drift.
    /// </summary>
    public static async Task RunWorkerAsync(Action<string> onLine, CancellationToken ct = default)
    {
        if (!await IsComfyUpAsync())
        {
            onLine("ComfyUI is not running — start it first (start-comfy.bat), then try again.");
            return;
        }

        var psi = new ProcessStartInfo
        {
            FileName = @"C:\ComfyUI\ComfyUI\.venv\Scripts\python.exe",
            Arguments = "percy_worker.py",
            WorkingDirectory = WorkerDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";

        using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) onLine(e.Data); };
        p.ErrorDataReceived  += (_, e) => { if (e.Data is not null) onLine(e.Data); };

        try
        {
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            await p.WaitForExitAsync(ct);
            onLine(p.ExitCode == 0 ? "— finished —" : $"— finished with exit code {p.ExitCode} —");
        }
        catch (OperationCanceledException)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
            onLine("— stopped —");
        }
        catch (Exception ex)
        {
            onLine($"could not start the worker: {ex.Message}");
        }
    }

    public static void OpenApprovalsInBrowser()
    {
        try { Process.Start(new ProcessStartInfo($"{Api}/produce/approvals") { UseShellExecute = true }); }
        catch { /* a bad link should never take the window down */ }
    }
}
