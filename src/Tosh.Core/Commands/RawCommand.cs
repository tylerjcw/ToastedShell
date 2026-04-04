using System.Globalization;

namespace Tosh.Core.Commands;

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
            DateTime dateTime => dateTime.ToString("O", CultureInfo.InvariantCulture),
            DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("O", CultureInfo.InvariantCulture),
            IFormattable formattable when value.GetType().IsPrimitive || value is decimal || value is Guid || value is TimeSpan
                => formattable.ToString(null, CultureInfo.InvariantCulture) ?? value.ToString() ?? string.Empty,
            Type type => type.FullName ?? type.Name,
            _ => value.ToString() ?? string.Empty,
        };
    }
}
