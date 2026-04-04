using System.Globalization;

namespace Tosh.Core;

public static class ExternalTextSerializer
{
    public static string SerializeArgument(object? value)
    {
        return value switch
        {
            FileSystemEntry entry => entry.FullName,
            FileSystemInfo fileSystemInfo => fileSystemInfo.FullName,
            _ => Serialize(value),
        };
    }

    public static string Serialize(object? value)
    {
        return value switch
        {
            null => string.Empty,
            ShellTextLine line => line.Text,
            string text => text,
            char character => character.ToString(),
            bool boolean => boolean ? "true" : "false",
            DateTime dateTime => dateTime.ToString("O", CultureInfo.InvariantCulture),
            DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("O", CultureInfo.InvariantCulture),
            StorageSize size => size.Bytes.ToString(CultureInfo.InvariantCulture),
            Enum enumeration => enumeration.ToString(),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? value.ToString() ?? string.Empty,
            _ => value.ToString() ?? string.Empty,
        };
    }
}
