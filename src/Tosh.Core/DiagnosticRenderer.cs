using System.Globalization;
using System.Text;

namespace Tosh.Core;

public sealed class DiagnosticRenderer
{
    public string Render(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (exception is ToshDiagnosticException diagnosticException)
        {
            return Render(diagnosticException);
        }

        return Render(ToshDiagnosticException.Create(new ToshDiagnostic(
            Code: "tosh::runtime::error",
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

        var builder = new StringBuilder();
        builder.AppendLine($"Error: {diagnostic.Code}");
        builder.AppendLine();
        builder.AppendLine($"  × {diagnostic.Title}");

        if (!string.IsNullOrWhiteSpace(diagnostic.SourceText) &&
            !string.IsNullOrWhiteSpace(diagnostic.SourceName) &&
            diagnostic.Span is TextSpan span)
        {
            RenderSourceSnippet(builder, diagnostic.SourceName, diagnostic.SourceText!, span, diagnostic.Label);
        }

        if (!string.IsNullOrWhiteSpace(diagnostic.Help))
        {
            builder.AppendLine($"  help: {diagnostic.Help}");
        }

        return builder.ToString().TrimEnd();
    }

    private static void RenderSourceSnippet(
        StringBuilder builder,
        string sourceName,
        string sourceText,
        TextSpan span,
        string? label)
    {
        var location = SourceLocation.From(sourceText, span);
        var lineNumberText = location.LineNumber.ToString(CultureInfo.InvariantCulture);
        var gutterWidth = Math.Max(1, lineNumberText.Length);
        var sourceLine = location.LineText.Replace("\t", "    ", StringComparison.Ordinal);
        var underlineStart = Math.Max(0, location.ColumnNumber - 1);
        var underlineLength = Math.Max(1, location.EndColumnNumber - location.ColumnNumber);
        var pointerOffset = underlineStart + Math.Max(0, (underlineLength - 1) / 2);
        var underline = BuildUnderline(underlineLength, pointerOffset - underlineStart);

        builder.AppendLine($"   ╭─[{sourceName}:{location.LineNumber}:{location.ColumnNumber}]");
        builder.AppendLine($" {lineNumberText.PadLeft(gutterWidth)} │ {sourceLine}");
        builder.AppendLine($" {new string(' ', gutterWidth)} · {new string(' ', underlineStart)}{underline}");

        if (!string.IsNullOrWhiteSpace(label))
        {
            builder.AppendLine($" {new string(' ', gutterWidth)} · {new string(' ', pointerOffset)}╰── {label}");
        }

        builder.AppendLine($" {new string(' ', gutterWidth)} ╰────");
    }

    private static string BuildUnderline(int length, int pointerOffset)
    {
        if (length <= 1)
        {
            return "┬";
        }

        var builder = new StringBuilder(length);

        for (var index = 0; index < length; index++)
        {
            builder.Append(index == pointerOffset ? '┬' : '─');
        }

        return builder.ToString();
    }

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

            var lineEnd = boundedEnd;

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
            var endColumnNumber = Math.Max(columnNumber + 1, boundedEnd - lineStart + 1);
            return new SourceLocation(lineNumber, columnNumber, endColumnNumber, lineText);
        }
    }
}
