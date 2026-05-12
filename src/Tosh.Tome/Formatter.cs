using System.Diagnostics;
using System.Text;
using Tosh.Language.Formatting;

namespace Tosh.Tome;

/// <summary>
/// Format dispatcher used by <c>:fmt</c> and (optionally) by save. Resolves
/// the formatter from the file extension and runs it. Built-in support:
/// <list type="bullet">
///   <item><c>.tosh</c> — internal <see cref="ToshFormatter"/>.</item>
///   <item>Anything else — an external CLI that reads stdin / writes stdout.</item>
/// </list>
/// External commands can be overridden via <c>TOME_FORMATTERS</c>, a
/// colon-separated list of <c>ext=cmd</c> entries. <c>{path}</c> in the
/// command template is replaced with the buffer's file path, useful for
/// tools like prettier that need a hint.
/// </summary>
internal static class Formatter
{
    public sealed record Result(bool Ok, string Text, string Message);

    private static readonly Dictionary<string, string> BuiltinExternal = new(StringComparer.OrdinalIgnoreCase)
    {
        [".rs"] = "rustfmt",
        [".go"] = "gofmt",
        [".py"] = "black -q -",
        [".js"] = "prettier --stdin-filepath {path}",
        [".jsx"] = "prettier --stdin-filepath {path}",
        [".ts"] = "prettier --stdin-filepath {path}",
        [".tsx"] = "prettier --stdin-filepath {path}",
        [".json"] = "prettier --stdin-filepath {path}",
        [".md"] = "prettier --stdin-filepath {path}",
        [".css"] = "prettier --stdin-filepath {path}",
        [".html"] = "prettier --stdin-filepath {path}",
        [".yaml"] = "prettier --stdin-filepath {path}",
        [".yml"] = "prettier --stdin-filepath {path}",
        [".c"] = "clang-format --assume-filename={path}",
        [".h"] = "clang-format --assume-filename={path}",
        [".cpp"] = "clang-format --assume-filename={path}",
        [".hpp"] = "clang-format --assume-filename={path}",
        [".cc"] = "clang-format --assume-filename={path}",
        [".hh"] = "clang-format --assume-filename={path}",
    };

    public static bool HasFormatterFor(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        if (string.IsNullOrEmpty(ext)) return false;
        if (string.Equals(ext, ".tosh", StringComparison.OrdinalIgnoreCase)) return true;
        return ResolveCommand(ext) is not null;
    }

    public static Result Format(string filePath, string text)
    {
        var ext = Path.GetExtension(filePath);
        if (string.Equals(ext, ".tosh", StringComparison.OrdinalIgnoreCase))
            return FormatTosh(text, filePath);

        var cmd = ResolveCommand(ext);
        if (string.IsNullOrEmpty(cmd))
            return new Result(false, text, $"no formatter for {ext}");

        return FormatExternal(cmd!, filePath, text);
    }

    private static Result FormatTosh(string text, string path)
    {
        try
        {
            var r = ToshFormatter.Format(text, string.IsNullOrEmpty(path) ? "<buffer>" : path);
            if (r.ParseDiagnostics.Count > 0)
                return new Result(false, text, $"format: {r.ParseDiagnostics.Count} parse error(s) — buffer unchanged");
            return new Result(true, r.FormattedText, "formatted (tosh)");
        }
        catch (Exception ex)
        {
            return new Result(false, text, $"format: {ex.Message}");
        }
    }

    private static Result FormatExternal(string template, string path, string text)
    {
        var rendered = template.Replace("{path}", string.IsNullOrEmpty(path) ? "buffer" : path);
        var (exe, args) = SplitCommand(rendered);
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return new Result(false, text, $"format: could not start {exe}");

            // Write the buffer on a background task so we don't deadlock
            // on tools that read all of stdin before producing output.
            var writeTask = Task.Run(async () =>
            {
                using var sw = p.StandardInput;
                await sw.WriteAsync(text);
            });
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            writeTask.Wait();
            p.WaitForExit(10_000);

            if (p.ExitCode != 0)
            {
                var msg = string.IsNullOrWhiteSpace(stderr) ? $"exit {p.ExitCode}" : stderr.Trim().Split('\n')[0];
                return new Result(false, text, $"format ({exe}): {msg}");
            }
            return new Result(true, stdout, $"formatted ({exe})");
        }
        catch (Exception ex)
        {
            return new Result(false, text, $"format: {ex.Message}");
        }
    }

    private static string? ResolveCommand(string ext)
    {
        if (string.IsNullOrEmpty(ext)) return null;
        var overrides = Environment.GetEnvironmentVariable("TOME_FORMATTERS");
        if (!string.IsNullOrEmpty(overrides))
        {
            foreach (var entry in overrides.Split(':', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = entry.IndexOf('=');
                if (eq <= 0) continue;
                var k = entry[..eq].Trim();
                if (!k.StartsWith('.')) k = "." + k;
                if (string.Equals(k, ext, StringComparison.OrdinalIgnoreCase))
                    return entry[(eq + 1)..].Trim();
            }
        }
        return BuiltinExternal.TryGetValue(ext, out var cmd) ? cmd : null;
    }

    private static (string Exe, string Args) SplitCommand(string cmd)
    {
        cmd = cmd.Trim();
        var sp = cmd.IndexOf(' ');
        if (sp < 0) return (cmd, string.Empty);
        return (cmd[..sp], cmd[(sp + 1)..]);
    }
}
