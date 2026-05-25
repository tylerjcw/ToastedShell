using System.Diagnostics;
using System.Text;

namespace Tosh.Tome;

internal sealed partial class TomeApp
{
    private Tab? _gitTab;

    private void HandleGit(string arg)
    {
        if (string.IsNullOrWhiteSpace(arg)) arg = "status";
        var parts = arg.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var sub = parts[0].ToLowerInvariant();
        var rest = parts.Length > 1 ? parts[1].Trim() : string.Empty;

        var workDir = FindGitWorkDir();
        if (workDir is null) { _message = "git: no repository found"; return; }

        switch (sub)
        {
            case "status":
            case "st":
                RunGitToTab(workDir, "status", "--short", "--branch");
                return;
            case "diff":
                if (string.IsNullOrEmpty(rest))
                    RunGitToTab(workDir, "diff", "HEAD");
                else
                    RunGitToTab(workDir, "diff", "HEAD", "--", rest);
                return;
            case "log":
                var n = int.TryParse(rest, out var cnt) ? cnt.ToString() : "20";
                RunGitToTab(workDir, "log", "--oneline", $"-{n}");
                return;
            case "stage":
            case "add":
                var stagePath = string.IsNullOrEmpty(rest) ? Current.FilePath : rest;
                if (string.IsNullOrEmpty(stagePath)) { _message = "git stage: no file"; return; }
                RunGitQuiet(workDir, "stage", "add", "--", stagePath);
                _explorerGitStatus?.Invalidate();
                return;
            case "unstage":
                var unstagePath = string.IsNullOrEmpty(rest) ? Current.FilePath : rest;
                if (string.IsNullOrEmpty(unstagePath)) { _message = "git unstage: no file"; return; }
                RunGitQuiet(workDir, "unstage", "restore", "--staged", "--", unstagePath);
                _explorerGitStatus?.Invalidate();
                return;
            case "commit":
                GitCommit(workDir, rest);
                return;
            default:
                // Pass-through: `:git push`, `:git stash`, etc.
                var rawArgs = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                RunGitToTabRaw(workDir, rawArgs);
                return;
        }
    }

    private void GitCommit(string workDir, string messageArg)
    {
        var msg = messageArg;
        if (string.IsNullOrWhiteSpace(msg))
        {
            msg = PromptText("commit: ");
            if (string.IsNullOrWhiteSpace(msg)) { _message = "git commit: cancelled"; return; }
        }
        RunGitToTab(workDir, "commit", "-m", msg.Trim());
        Current.GitDiff?.Invalidate();
        _explorerGitStatus?.Invalidate();
    }

    // ─── Helpers ─────────────────────────────────────────────────────────

    private void RunGitToTab(string workDir, params string[] args)
    {
        var (stdout, stderr, exitCode) = RunGitProcess(workDir, args);
        ShowGitOutput(BuildGitOutput("git " + string.Join(' ', args), stdout, stderr, exitCode));
        _message = exitCode == 0 ? "git: ok" : $"git: exit {exitCode}";
    }

    private void RunGitToTabRaw(string workDir, string[] args) => RunGitToTab(workDir, args);

    private void RunGitQuiet(string workDir, string verb, params string[] args)
    {
        var (_, stderr, exitCode) = RunGitProcess(workDir, args);
        _message = exitCode == 0
            ? $"git {verb}: ok"
            : string.IsNullOrWhiteSpace(stderr) ? $"git {verb}: exit {exitCode}" : $"git: {stderr.Trim()}";
    }

    private void ShowGitOutput(string text)
    {
        if (_gitTab is null || !_tabs.Contains(_gitTab))
        {
            _gitTab = new Tab("*Git*", text, colorizer: null);
            _tabs.Add(_gitTab);
        }
        else
        {
            _gitTab.Buffer.LoadText(text);
            _gitTab.Buffer.MarkClean();
        }
        _active = _tabs.IndexOf(_gitTab);
        _mode = EditorMode.Command;
    }

    private static string BuildGitOutput(string header, string stdout, string stderr, int exitCode)
    {
        var sb = new StringBuilder();
        sb.Append("$ ").AppendLine(header);
        if (stdout.Length > 0) sb.Append(stdout);
        if (stderr.Length > 0)
        {
            if (sb.Length > 0 && sb[^1] != '\n') sb.AppendLine();
            sb.AppendLine("─── stderr ───");
            sb.Append(stderr);
        }
        if (sb.Length > 0 && sb[^1] != '\n') sb.AppendLine();
        sb.Append($"[exit {exitCode}]").AppendLine();
        return sb.ToString();
    }

    private static (string Stdout, string Stderr, int ExitCode) RunGitProcess(string workDir, string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("--no-pager");
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var proc = Process.Start(psi);
            if (proc is null) return ("", "failed to start git", 1);
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(30_000))
            {
                try { proc.Kill(); } catch { }
                return ("", "git timed out", 1);
            }
            return (stdoutTask.GetAwaiter().GetResult(), stderrTask.GetAwaiter().GetResult(), proc.ExitCode);
        }
        catch (Exception ex)
        {
            return ("", ex.Message, 1);
        }
    }

    private string? FindGitWorkDir() => GitInfo.FindRoot(Current.FilePath);
}
