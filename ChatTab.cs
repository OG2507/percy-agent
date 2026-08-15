using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace PercyAgent;

// The Chat tab: talk to Claude through the LOCAL Claude Code CLI, so it runs
// on Stephen's subscription — same brain as Cowork, no API fees.
//
// Each send is one `claude -p "<message>"` process. The first message of a
// session starts a fresh conversation; every one after adds --continue so the
// conversation carries. It runs from its own clean home folder so no repo's
// CLAUDE.md hijacks the chat — the user-level config still provides his
// connectors. Output streams into the transcript as it arrives, stderr
// included: a failure must surface as a failure, never a blank pane.
public partial class MainWindow
{
    const string ChatHome = @"D:\ComfyUI-Data\baldrick\chat-home";

    string? claudePath;   // resolved once at startup; null = not found, and the tab says why
    bool chatStarted;     // set after the first send SUCCEEDS, so --continue always has a conversation to continue
    Process? chatProc;    // the reply in flight — a second press of the button stops it

    void InitChat()
    {
        // Nothing is clickable until we know the CLI is really there.
        ChatInput.IsEnabled = false;
        ChatSend.IsEnabled = false;
        _ = ResolveClaudeAsync();
    }

    /// <summary>Find the claude CLI and prove it answers. If it cannot, the tab
    /// says exactly what was looked for and what went wrong — never sits blank.</summary>
    async Task ResolveClaudeAsync()
    {
        string? found = null;
        string reason = "";
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "where.exe",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("claude");
            using var w = Process.Start(psi)!;
            var lines = (await w.StandardOutput.ReadToEndAsync())
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            await w.WaitForExitAsync();
            // Prefer a real .exe — a .cmd shim needs cmd.exe wrapped around it.
            found = lines.FirstOrDefault(l => l.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    ?? lines.FirstOrDefault();
            if (found is null) reason = "'where claude' found nothing on this machine's PATH";
        }
        catch (Exception ex)
        {
            reason = "couldn't even look for it — 'where.exe claude' failed: " + ex.Message;
        }

        if (found is null)
        {
            ChatLog.Text = "The Claude CLI is not available — " + reason + ".\n" +
                           "Install Claude Code (claude.com/claude-code), or fix PATH, then reopen the app.";
            ChatCliHint.Text = "claude CLI: not found";
            return;
        }

        try
        {
            using var p = Process.Start(ClaudeStart(found, "--version"))!;
            var version = (await p.StandardOutput.ReadToEndAsync()).Trim();
            var err = (await p.StandardError.ReadToEndAsync()).Trim();
            await p.WaitForExitAsync();
            if (p.ExitCode != 0)
            {
                ChatLog.Text = $"Found {found} but 'claude --version' failed (exit {p.ExitCode}):\n" +
                               (err.Length > 0 ? err : version.Length > 0 ? version : "(no output)");
                ChatCliHint.Text = "claude CLI: found but not answering";
                return;
            }
            claudePath = found;
            ChatCliHint.Text = $"claude {version}  ·  {found}  ·  home: {ChatHome}";
            ChatLog.Text = "Ready. The first message starts a fresh conversation; every one after continues it.";
            ChatInput.IsEnabled = true;
            ChatSend.IsEnabled = true;
        }
        catch (Exception ex)
        {
            ChatLog.Text = $"Found {found} but couldn't run it: {ex.Message}";
            ChatCliHint.Text = "claude CLI: found but not runnable";
        }
    }

    /// <summary>One place builds the process, so the version check and every send
    /// run the CLI the same way — from the clean chat home.</summary>
    static ProcessStartInfo ClaudeStart(string exe, params string[] args)
    {
        Directory.CreateDirectory(ChatHome);
        var psi = new ProcessStartInfo
        {
            WorkingDirectory = ChatHome,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        // ANSI colour codes would land in the transcript as garbage.
        psi.EnvironmentVariables["NO_COLOR"] = "1";
        if (exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
            exe.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
        {
            // CreateProcess cannot start a batch shim directly — cmd.exe carries it.
            psi.FileName = "cmd.exe";
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(exe);
        }
        else
        {
            psi.FileName = exe;
        }
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    void ChatInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && ChatSend.IsEnabled && Equals(ChatSend.Content, "Send"))
        {
            e.Handled = true;
            ChatSend_Click(sender, e);
        }
    }

    async void ChatSend_Click(object sender, RoutedEventArgs e)
    {
        if (chatProc is not null)
        {
            // A second press stops the reply — same pattern as the Work tab's Run button.
            try { if (!chatProc.HasExited) chatProc.Kill(entireProcessTree: true); } catch { }
            return;
        }

        var msg = ChatInput.Text.Trim();
        if (msg.Length == 0 || claudePath is null) return;

        ChatInput.Text = "";
        ChatInput.IsEnabled = false;      // one reply at a time — the CLI serialises the conversation anyway
        ChatSend.Content = "Stop";
        AppendChat($"You:\n{msg}\n");

        // The header is written by whichever line arrives first, so a reply that
        // opens with a stderr warning still reads as Claude's turn.
        bool wroteHeader = false;
        void Line(string prefix, string text) => Dispatcher.Invoke(() =>
        {
            if (!wroteHeader) { AppendChat("Claude:"); wroteHeader = true; }
            AppendChat(prefix + text);
        });

        var args = chatStarted
            ? new[] { "-p", msg, "--continue" }
            : new[] { "-p", msg };

        using var p = new Process { StartInfo = ClaudeStart(claudePath, args), EnableRaisingEvents = true };
        p.OutputDataReceived += (_, a) => { if (a.Data is not null) Line("", a.Data); };
        p.ErrorDataReceived  += (_, a) => { if (a.Data is not null) Line("⚠ ", a.Data); };   // stderr shown, visibly
        chatProc = p;
        try
        {
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            await p.WaitForExitAsync();
            if (p.ExitCode == 0)
                chatStarted = true;       // the conversation now exists on disk — --continue from here on
            else
                AppendChat($"— claude exited with code {p.ExitCode} —");
        }
        catch (Exception ex)
        {
            AppendChat("— could not run claude: " + ex.Message + " —");
        }
        finally
        {
            chatProc = null;
            AppendChat("");               // a blank line between turns keeps the transcript readable
            ChatSend.Content = "Send";
            ChatInput.IsEnabled = true;
            ChatInput.Focus();
        }
    }

    void AppendChat(string line) => Dispatcher.Invoke(() =>
    {
        if (ChatLog.Text.StartsWith("Ready.")) ChatLog.Text = "";
        ChatLog.Text += (ChatLog.Text.Length > 0 ? "\n" : "") + line;
        ChatScroll.ScrollToEnd();
    });
}
