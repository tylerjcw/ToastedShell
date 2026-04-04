namespace Tosh.Cli.Tui;

internal enum TuiValidationSeverity
{
    Info,
    Warning,
    Error,
}

internal sealed record TuiValidationMessage(string Path, TuiValidationSeverity Severity, string Text);

internal static class TuiValidationFormatter
{
    public static string BuildSummary(IReadOnlyList<TuiValidationMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        if (messages.Count == 0)
        {
            return "No validation issues.";
        }

        var errorCount = messages.Count(message => message.Severity == TuiValidationSeverity.Error);
        var warningCount = messages.Count(message => message.Severity == TuiValidationSeverity.Warning);
        var infoCount = messages.Count(message => message.Severity == TuiValidationSeverity.Info);
        var parts = new List<string>(3);

        if (errorCount > 0)
        {
            parts.Add($"{errorCount} error{(errorCount == 1 ? string.Empty : "s")}");
        }

        if (warningCount > 0)
        {
            parts.Add($"{warningCount} warning{(warningCount == 1 ? string.Empty : "s")}");
        }

        if (infoCount > 0)
        {
            parts.Add($"{infoCount} info");
        }

        return string.Join(", ", parts);
    }

    public static IReadOnlyList<string> BuildEntries(IReadOnlyList<TuiValidationMessage> messages, int width)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var entries = new List<string>();

        if (messages.Count == 0)
        {
            entries.Add("No validation issues.");
            return entries;
        }

        entries.Add(BuildSummary(messages));
        entries.Add(string.Empty);

        foreach (var message in messages)
        {
            var prefix = message.Severity switch
            {
                TuiValidationSeverity.Error => "[error]",
                TuiValidationSeverity.Warning => "[warning]",
                _ => "[info]",
            };

            var text = $"{prefix} {message.Path}: {message.Text}";
            entries.AddRange(TextDocumentFormatter.WrapParagraph(text, width));
        }

        return entries;
    }
}
