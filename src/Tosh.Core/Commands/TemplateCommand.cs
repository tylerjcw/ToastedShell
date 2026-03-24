using System.Text.RegularExpressions;

namespace Tosh.Core.Commands;

public sealed partial class TemplateCommand : ShellCommand
{
    public TemplateCommand()
        : base("template", "Renders pipeline objects into text using {{ member.path }} placeholders.", "template <text>") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count != 1)
        {
            throw new InvalidOperationException("template expects exactly one template string.");
        }

        var template = context.Arguments[0]?.ToString() ?? string.Empty;

        await foreach (var item in context.Input.WithCancellation(context.CancellationToken))
        {
            var rendered = template.Replace("\\{\\{", "\x00LBRACE\x00").Replace("\\}\\}", "\x00RBRACE\x00");

            rendered = PlaceholderRegex().Replace(
                rendered,
                match =>
                {
                    var path = match.Groups["path"].Value.Trim();
                    var value = path is "." or "it"
                        ? item
                        : context.Runtime.ObjectAccessor.GetValue(item, path);
                    return value is null ? string.Empty : context.Runtime.Formatter.Format(value);
                });

            rendered = rendered.Replace("\x00LBRACE\x00", "{{").Replace("\x00RBRACE\x00", "}}");
            yield return new ShellTextLine(rendered);
        }
    }

    [GeneratedRegex("{{\\s*(?<path>[^}]+)\\s*}}", RegexOptions.Compiled)]
    private static partial Regex PlaceholderRegex();
}
