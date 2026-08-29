using System.Text.RegularExpressions;

using Tosh.Runtime;

namespace Tosh.Stdlib.Text;

[CommandCategory("Text")]
[CommandArgument("text", "A template string with {{ member.path }} placeholders.")]
[CommandExample("ls | template \"{{Name}} is {{Length}} bytes\"", Title = "Format file info")]
[CommandExample("echo {|name=\"World\"|} | template \"Hello, {{name}}!\"", Title = "Simple template rendering")]
[CommandOutput("Rendered text with placeholders replaced by each pipeline object's member values.", ClrType = typeof(IAsyncEnumerable<ShellTextLine>))]
[PipelineInput(AcceptsScalar = true, AcceptsRecord = true, Description = "Applies the template to each piped object.")]
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
                    var value = path is "." or "_"
                        ? item
                        : context.LanguageRuntime.ObjectAccessor.GetValue(item, path);
                    return value is null ? string.Empty : ToastRenderer.Render(value);
                });

            rendered = rendered.Replace("\x00LBRACE\x00", "{{").Replace("\x00RBRACE\x00", "}}");
            yield return new ShellTextLine(rendered);
        }
    }

    [GeneratedRegex("{{\\s*(?<path>[^}]+)\\s*}}", RegexOptions.Compiled)]
    private static partial Regex PlaceholderRegex();
}
