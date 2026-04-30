using System.Globalization;
using System.Numerics;

namespace Tosh.Core.Commands.Text;

[Stdlib(StdlibCategory.Text)]
[CommandCategory("Text")]
[CommandArgument("value ...", "Optional explicit values to emit as plain text instead of rich object display.", Required = false)]
[CommandExample("echo 1317 | raw", Title = "Show a scalar without rich boxing")]
[CommandExample("echo System.DayOfWeek.Friday | raw", Title = "Show an enum's raw CLR string form")]
[CommandOutput("Returns ShellTextLine values so the final display stays plain text instead of tables or record views.")]
[PipelineInput(AcceptsScalar = true, AcceptsRecord = true, AcceptsList = true, AcceptsTable = true, Description = "Consumes pipeline values and emits one plain-text line per input item.")]
public sealed class RawCommand : ShellCommand, IImplicitGlobCommand
{
    public RawCommand()
        : base("raw", "Emits plain text without rich table or record rendering.", "raw [value...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count > 0)
        {
            foreach (var argument in context.Arguments)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                yield return new ShellTextLine(ToRawText(argument));
            }

            yield break;
        }

        await foreach (var item in context.Input.WithCancellation(context.CancellationToken))
        {
            yield return new ShellTextLine(ToRawText(item));
        }
    }

    private static string ToRawText(object? value)
    {
        return value switch
        {
            null => string.Empty,
            ShellTextLine line => line.Text,
            StyledText styled => styled.Text,
            string text => text,
            char character => character.ToString(),
            bool boolean => boolean.ToString().ToLowerInvariant(),
            Enum @enum => @enum.ToString(),
            Complex complex => ComplexShellType.FormatCompact(complex),
            DateTime dateTime => dateTime.ToString("O", CultureInfo.InvariantCulture),
            DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("O", CultureInfo.InvariantCulture),
            IFormattable formattable when value.GetType().IsPrimitive || value is decimal || value is Guid || value is TimeSpan
                => formattable.ToString(null, CultureInfo.InvariantCulture) ?? value.ToString() ?? string.Empty,
            Type type => type.FullName ?? type.Name,
            _ => value.ToString() ?? string.Empty,
        };
    }
}
