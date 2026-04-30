using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Tosh.Runtime;

/// <summary>
/// Renders TōSh diagnostics. Three output modes:
/// <list type="bullet">
///   <item><b>Styled</b> — TTY-friendly: ANSI color, half-frame layout, dotted trail with arrow.</item>
///   <item><b>Plain</b> — GCC-shaped, ASCII-only, no color. Used when stderr is not a TTY,
///     <c>NO_COLOR</c> is set, or <c>$tosh.Config.Diagnostics.PlainOutput = true</c>.</item>
///   <item><b>JSON</b> — One NDJSON object per diagnostic. Selected when
///     <c>$tosh.Config.Diagnostics.Format = Json</c> or <c>--diagnostics=json</c> was passed.</item>
/// </list>
/// </summary>
public sealed class DiagnosticRenderer
{
    private readonly ToshDiagnosticThemeConfig? _theme;
    private readonly ToshDiagnosticsConfig? _config;
    private readonly bool _forcePlain;
    private readonly bool _forceJson;

    public DiagnosticRenderer(ToshDiagnosticThemeConfig? theme = null)
        : this(theme, config: null, forcePlain: false, forceJson: false)
    {
    }

    public DiagnosticRenderer(
        ToshDiagnosticThemeConfig? theme,
        ToshDiagnosticsConfig? config,
        bool forcePlain = false,
        bool forceJson = false)
    {
        _theme = theme;
        _config = config;
        _forcePlain = forcePlain;
        _forceJson = forceJson;
    }

    // ── Public API ──────────────────────────────────────────────────────

    public string Render(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (exception is ToshDiagnosticException diagnosticException)
        {
            return Render(diagnosticException);
        }

        return Render(ToshDiagnosticException.Create(new ToshDiagnostic(
            Code: "tosh.runtime.error",
            Title: exception.Message)));
    }

    public string Render(ToshDiagnosticException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return string.Join(
            $"{Environment.NewLine}{Environment.NewLine}",
            exception.Diagnostics.Select(Render));
    }

    public string Render(ToshDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);

        if (UseJsonMode())
        {
            return RenderJson(diagnostic);
        }

        return UsePlainMode()
            ? RenderPlain(diagnostic)
            : RenderStyled(diagnostic);
    }

    public string RenderWarning(string title, string? help = null, string? info = null)
    {
        return Render(new ToshDiagnostic(
            Code: string.Empty,
            Title: title,
            Help: help,
            Info: info,
            Severity: ToshDiagnosticSeverity.Warning));
    }

    public string RenderWarning(ToshDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);

        var promoted = diagnostic.Severity == ToshDiagnosticSeverity.Error
            ? diagnostic with { Severity = ToshDiagnosticSeverity.Warning }
            : diagnostic;

        return Render(promoted);
    }

    // ── Mode selection ──────────────────────────────────────────────────

    private bool UseJsonMode()
    {
        if (_forceJson) return true;
        return _config?.Format == ToshDiagnosticFormat.Json;
    }

    private bool UsePlainMode()
    {
        if (_forcePlain) return true;
        if (_config?.PlainOutput == true) return true;
        if (HasNoColorEnvironment()) return true;
        return false;
    }

    private static bool HasNoColorEnvironment()
    {
        var noColor = Environment.GetEnvironmentVariable("NO_COLOR");
        if (!string.IsNullOrEmpty(noColor)) return true;

        var plainEnv = Environment.GetEnvironmentVariable("TOSH_DIAG_PLAIN");
        return !string.IsNullOrEmpty(plainEnv) && plainEnv != "0";
    }

    // ── Styled (TTY) renderer ───────────────────────────────────────────

    private string RenderStyled(ToshDiagnostic diagnostic)
    {
        var lines = new List<string>();
        var severity = diagnostic.Severity;

        // Header: glyph severity-word [location] [code]
        var glyph = StyleGlyph(severity);
        var severityWord = StyleSeverityWord(severity);
        var locationText = BuildLocationText(diagnostic);
        var codeText = BuildCodeText(diagnostic);

        var headerParts = new List<string> { glyph, severityWord };
        if (!string.IsNullOrEmpty(locationText)) headerParts.Add(locationText);
        if (!string.IsNullOrEmpty(codeText)) headerParts.Add(codeText);
        lines.Add(string.Join("  ", headerParts));

        var hasSnippet =
            !string.IsNullOrWhiteSpace(diagnostic.SourceText) &&
            !string.IsNullOrWhiteSpace(diagnostic.SourceName) &&
            diagnostic.Span is TextSpan;

        // Default gutter width when there's no snippet to size against.
        var gutterWidth = 1;

        if (!hasSnippet && !string.IsNullOrWhiteSpace(diagnostic.Title))
        {
            lines.Add(StyleFrame("│"));
            lines.Add($"{StyleFrame("│")}  {Style(_theme?.Title, diagnostic.Title)}");
        }
        else if (hasSnippet)
        {
            // Surface the title above the snippet only if it adds information
            // beyond what the inline label already communicates.
            if (!string.IsNullOrWhiteSpace(diagnostic.Title) &&
                !string.Equals(diagnostic.Title, diagnostic.Label, StringComparison.Ordinal))
            {
                lines.Add(StyleFrame("│"));
                lines.Add($"{StyleFrame("│")}  {Style(_theme?.Title, diagnostic.Title)}");
            }
            lines.Add(StyleFrame("│"));
            gutterWidth = RenderStyledSnippet(lines, diagnostic);
        }

        // Footer items (each rendered as a label-prefixed string).
        var footerLines = new List<string>();
        if (!string.IsNullOrWhiteSpace(diagnostic.Help))
        {
            footerLines.Add($"{Style(_theme?.Help, "help:")} {diagnostic.Help}");
        }
        if (!string.IsNullOrWhiteSpace(diagnostic.Info))
        {
            footerLines.Add($"{Style(_theme?.Help, "info:")} {diagnostic.Info}");
        }
        if (severity != ToshDiagnosticSeverity.Error && !string.IsNullOrWhiteSpace(diagnostic.Code))
        {
            footerLines.Add($"{Style(_theme?.Help, "hush:")} {diagnostic.Code}");
        }

        // Closing rail: `╰` + `─`×(gutterWidth+3) + `┤`. The `┤` lines up with
        // the right edge of the snippet's gutter (`│  N │`), giving the
        // half-frame a clean, consistent terminator regardless of body width.
        // Footer items hang off the rail on the right.
        //
        // Layout rules:
        //   • First footer item:        ╰────┤ help: …
        //   • Subsequent footer items:  ─────┤ info: …
        //   • Wrapped lines inside an item (when the help/info text contains
        //     embedded newlines):       │ …continuation…
        //   • A trailing blank line follows the diagnostic for visual breathing room.
        var dashCount = gutterWidth + 3;
        var rail = StyleFrame("╰" + new string('─', dashCount) + "┤");
        var itemSeparator = StyleFrame(new string(' ', dashCount + 1) + "┤");
        var lineContinuation = StyleFrame(new string(' ', dashCount + 1) + "│");

        if (footerLines.Count > 0)
        {
            lines.Add(StyleFrame("│"));
            for (var i = 0; i < footerLines.Count; i++)
            {
                var entryLines = footerLines[i].Split('\n');
                var leader = i == 0 ? rail : itemSeparator;
                lines.Add($"{leader} {entryLines[0]}");
                for (var j = 1; j < entryLines.Length; j++)
                {
                    lines.Add($"{lineContinuation} {entryLines[j]}");
                }
            }
        }
        else
        {
            lines.Add(rail);
        }

        // Trailing blank line — visually separates this diagnostic from
        // whatever comes next (another diagnostic, command output, prompt).
        lines.Add(string.Empty);

        return string.Join(Environment.NewLine, lines);
    }

    private int RenderStyledSnippet(List<string> lines, ToshDiagnostic diagnostic)
    {
        var location = SourceLocation.From(diagnostic.SourceText!, diagnostic.Span!.Value);
        var lineNumberText = location.LineNumber.ToString(CultureInfo.InvariantCulture);
        var gutterWidth = Math.Max(1, lineNumberText.Length);
        var sourceLine = location.LineText.Replace("\t", "    ", StringComparison.Ordinal);

        var underlineStart = Math.Max(0, location.ColumnNumber - 1);
        var underlineLength = Math.Max(1, location.EndColumnNumber - location.ColumnNumber);

        lines.Add(
            $"{StyleFrame("│")}  {StyleFrame(lineNumberText.PadLeft(gutterWidth))} {StyleFrame("│")} {sourceLine}");

        var trail = BuildTrail(underlineLength);
        var labelText = string.IsNullOrWhiteSpace(diagnostic.Label)
            ? string.Empty
            : $" {Style(_theme?.Title, diagnostic.Label!)}";
        var pad = new string(' ', gutterWidth + 3 + underlineStart);
        lines.Add($"{StyleFrame("│")}  {pad}{StyleSeverity(diagnostic.Severity, trail)}{labelText}");
        return gutterWidth;
    }

    /// <summary>
    /// Computes the visible (printable) width of a styled line by stripping
    /// CSI escape sequences (`\x1b[...m`) and OSC 8 hyperlink envelopes
    /// (`\x1b]8;;...\x1b\\`). Currently unused but retained for future
    /// width-aware layout decisions (e.g. wrapping long footer text).
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822", Justification = "Static helper kept for symmetry with renderer instance methods.")]
    private static int VisibleWidth(string line)
    {
        if (string.IsNullOrEmpty(line)) return 0;
        var width = 0;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '\x1b' && i + 1 < line.Length)
            {
                var next = line[i + 1];
                if (next == '[')
                {
                    // CSI: ESC [ ... letter
                    i += 2;
                    while (i < line.Length && !((line[i] >= 0x40 && line[i] <= 0x7e)))
                    {
                        i++;
                    }
                    continue;
                }
                if (next == ']')
                {
                    // OSC: ESC ] ... ESC \  (or BEL)
                    i += 2;
                    while (i < line.Length)
                    {
                        if (line[i] == '\x07') break;
                        if (line[i] == '\x1b' && i + 1 < line.Length && line[i + 1] == '\\')
                        {
                            i++;
                            break;
                        }
                        i++;
                    }
                    continue;
                }
            }
            width++;
        }
        return width;
    }

    private static string BuildTrail(int underlineLength)
    {
        // Single-character span gets just the corner+arrow; longer spans get
        // a dotted leader trail terminated by `─▶`. The trail length matches
        // the underlined source so the `─` lines up just past the last char
        // and the arrow points away from the underlined region.
        if (underlineLength <= 1)
        {
            return "╰─▶";
        }
        var dots = new string('┄', underlineLength);
        return $"{dots}─▶";
    }

    // ── Plain (ASCII) renderer ──────────────────────────────────────────

    private static string RenderPlain(ToshDiagnostic diagnostic)
    {
        var builder = new StringBuilder();
        var severityWord = SeverityWord(diagnostic.Severity).TrimEnd();
        var locationText = BuildLocationTextPlain(diagnostic);
        var code = string.IsNullOrWhiteSpace(diagnostic.Code) ? string.Empty : $"[{diagnostic.Code}]";
        var prefix = $"{severityWord}{code}";

        var headParts = new List<string> { prefix };
        if (!string.IsNullOrEmpty(locationText)) headParts.Add(locationText);
        var head = string.Join(" ", headParts);

        if (!string.IsNullOrWhiteSpace(diagnostic.Title))
        {
            builder.AppendLine($"{head}: {diagnostic.Title}");
        }
        else
        {
            builder.AppendLine(head);
        }

        if (!string.IsNullOrWhiteSpace(diagnostic.SourceText) &&
            !string.IsNullOrWhiteSpace(diagnostic.SourceName) &&
            diagnostic.Span is TextSpan span)
        {
            var location = SourceLocation.From(diagnostic.SourceText!, span);
            var lineNumberText = location.LineNumber.ToString(CultureInfo.InvariantCulture);
            var gutterWidth = Math.Max(1, lineNumberText.Length);
            var sourceLine = location.LineText.Replace("\t", "    ", StringComparison.Ordinal);
            var underlineStart = Math.Max(0, location.ColumnNumber - 1);
            var underlineLength = Math.Max(1, location.EndColumnNumber - location.ColumnNumber);

            builder.AppendLine($"  {lineNumberText.PadLeft(gutterWidth)} | {sourceLine}");
            var pad = new string(' ', gutterWidth + 3 + underlineStart);
            var carets = new string('^', underlineLength);
            var labelSuffix = string.IsNullOrWhiteSpace(diagnostic.Label)
                ? string.Empty
                : $" {diagnostic.Label}";
            builder.AppendLine($"  {pad}{carets}{labelSuffix}");
        }

        if (!string.IsNullOrWhiteSpace(diagnostic.Help))
        {
            builder.AppendLine($"  help: {diagnostic.Help}");
        }
        if (!string.IsNullOrWhiteSpace(diagnostic.Info))
        {
            builder.AppendLine($"  info: {diagnostic.Info}");
        }
        if (diagnostic.Severity != ToshDiagnosticSeverity.Error &&
            !string.IsNullOrWhiteSpace(diagnostic.Code))
        {
            builder.AppendLine($"  hush: {diagnostic.Code}");
        }

        return builder.ToString().TrimEnd();
    }

    // ── JSON (NDJSON) renderer ──────────────────────────────────────────

    private static string RenderJson(ToshDiagnostic diagnostic)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteString("severity", SeverityWord(diagnostic.Severity).Trim());
            writer.WriteString("category", diagnostic.Category.ToString().ToLowerInvariant());
            writer.WriteString("lifecycle", diagnostic.Lifecycle.ToString().ToLowerInvariant());
            if (!string.IsNullOrEmpty(diagnostic.Code))
            {
                writer.WriteString("code", diagnostic.Code);
            }
            writer.WriteString("title", diagnostic.Title ?? string.Empty);
            if (!string.IsNullOrEmpty(diagnostic.SourceName))
            {
                writer.WriteString("source", diagnostic.SourceName);
            }
            if (!string.IsNullOrEmpty(diagnostic.SourceText) && diagnostic.Span is TextSpan span)
            {
                var loc = SourceLocation.From(diagnostic.SourceText!, span);
                writer.WriteNumber("line", loc.LineNumber);
                writer.WriteNumber("column", loc.ColumnNumber);
                writer.WriteNumber("endColumn", loc.EndColumnNumber);
                writer.WriteString("snippet", loc.LineText);
            }
            if (!string.IsNullOrEmpty(diagnostic.Label))
            {
                writer.WriteString("label", diagnostic.Label);
            }
            if (!string.IsNullOrEmpty(diagnostic.Help))
            {
                writer.WriteString("help", diagnostic.Help);
            }
            if (!string.IsNullOrEmpty(diagnostic.Info))
            {
                writer.WriteString("info", diagnostic.Info);
            }
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    // ── Header / styling helpers ────────────────────────────────────────

    private string StyleGlyph(ToshDiagnosticSeverity severity)
    {
        var glyph = SeverityGlyph(severity);
        var style = StyleForSeverity(severity);
        return Style(style, glyph);
    }

    private string StyleSeverityWord(ToshDiagnosticSeverity severity)
    {
        var word = SeverityWord(severity);
        var style = StyleForSeverity(severity);
        return Style(style, word);
    }

    private string StyleSeverity(ToshDiagnosticSeverity severity, string text)
    {
        var style = severity switch
        {
            ToshDiagnosticSeverity.Error => _theme?.Underline ?? _theme?.ErrorGlyph,
            ToshDiagnosticSeverity.Warning => _theme?.WarningGlyph,
            ToshDiagnosticSeverity.Info => _theme?.InfoGlyph,
            _ => _theme?.HintGlyph,
        };
        return Style(style, text);
    }

    private ToshTextStyleConfig? StyleForSeverity(ToshDiagnosticSeverity severity)
    {
        return severity switch
        {
            ToshDiagnosticSeverity.Error => _theme?.ErrorGlyph,
            ToshDiagnosticSeverity.Warning => _theme?.WarningGlyph,
            ToshDiagnosticSeverity.Info => _theme?.InfoGlyph,
            _ => _theme?.HintGlyph,
        };
    }

    private string StyleFrame(string text) => Style(_theme?.Frame ?? _theme?.SourceLocation, text);

    private string BuildLocationText(ToshDiagnostic diagnostic)
    {
        var raw = BuildLocationTextPlain(diagnostic);
        return string.IsNullOrEmpty(raw) ? string.Empty : Style(_theme?.SourceLocation, raw);
    }

    private static string BuildLocationTextPlain(ToshDiagnostic diagnostic)
    {
        if (string.IsNullOrEmpty(diagnostic.SourceName) || diagnostic.Span is null)
        {
            return string.Empty;
        }
        var loc = SourceLocation.From(diagnostic.SourceText ?? string.Empty, diagnostic.Span.Value);
        return $"{diagnostic.SourceName}:{loc.LineNumber}:{loc.ColumnNumber}";
    }

    private string BuildCodeText(ToshDiagnostic diagnostic)
    {
        if (string.IsNullOrEmpty(diagnostic.Code))
        {
            return string.Empty;
        }

        var text = diagnostic.Code;
        var styled = Style(_theme?.Code ?? _theme?.SourceLocation, text);

        // OSC 8 hyperlink wrapping when a help URI base is configured.
        var uriBase = _config?.HelpUriBase;
        if (string.IsNullOrEmpty(uriBase))
        {
            return styled;
        }
        var uri = uriBase.EndsWith('/') ? uriBase + text : uriBase + "/" + text;
        return $"\x1b]8;;{uri}\x1b\\{styled}\x1b]8;;\x1b\\";
    }

    private static string SeverityGlyph(ToshDiagnosticSeverity severity)
    {
        return severity switch
        {
            ToshDiagnosticSeverity.Error => TerminalGlyphs.IsBasicTerminal ? "x" : "\u2716",   // ✖
            ToshDiagnosticSeverity.Warning => TerminalGlyphs.IsBasicTerminal ? "!" : "\u26a0", // ⚠
            ToshDiagnosticSeverity.Info => TerminalGlyphs.IsBasicTerminal ? "i" : "\u24d8",    // ⓘ
            _ => "\u00b7",                                                                     // ·
        };
    }

    private static string SeverityWord(ToshDiagnosticSeverity severity)
    {
        // Padded to 5 chars for column-stable headers.
        return severity switch
        {
            ToshDiagnosticSeverity.Error => "error",
            ToshDiagnosticSeverity.Warning => "warn ",
            ToshDiagnosticSeverity.Info => "info ",
            _ => "hint ",
        };
    }

    private static string Style(ToshTextStyleConfig? style, string text)
    {
        return style is null ? text : style.Apply(text).ToAnsi();
    }

    // ── Source location helper ──────────────────────────────────────────

    private readonly record struct SourceLocation(
        int LineNumber,
        int ColumnNumber,
        int EndColumnNumber,
        string LineText)
    {
        public static SourceLocation From(string sourceText, TextSpan span)
        {
            var boundedStart = Math.Clamp(span.Start, 0, sourceText.Length);
            var boundedEnd = Math.Clamp(span.End, boundedStart, sourceText.Length);
            var lineStart = boundedStart;

            while (lineStart > 0 && sourceText[lineStart - 1] != '\n')
            {
                lineStart--;
            }

            var lineEnd = boundedStart;

            while (lineEnd < sourceText.Length && sourceText[lineEnd] != '\n')
            {
                lineEnd++;
            }

            var lineNumber = 1;

            for (var index = 0; index < lineStart; index++)
            {
                if (sourceText[index] == '\n')
                {
                    lineNumber++;
                }
            }

            var lineText = sourceText[lineStart..lineEnd].TrimEnd('\r');
            var columnNumber = boundedStart - lineStart + 1;
            var rawEndColumnNumber = Math.Max(columnNumber + 1, boundedEnd - lineStart + 1);
            var maxEndColumnNumber = lineText.Length + 1;
            var endColumnNumber = Math.Clamp(rawEndColumnNumber, columnNumber + 1, Math.Max(columnNumber + 1, maxEndColumnNumber));
            return new SourceLocation(lineNumber, columnNumber, endColumnNumber, lineText);
        }
    }
}
