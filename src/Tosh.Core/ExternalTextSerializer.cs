using System.Collections;
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
            IDictionary dictionary => SerializeDictionary(dictionary),
            IEnumerable enumerable when value is not IFormattable => SerializeEnumerable(enumerable),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? value.ToString() ?? string.Empty,
            _ => value.ToString() ?? string.Empty,
        };
    }

    private static string SerializeEnumerable(IEnumerable enumerable)
    {
        var builder = new System.Text.StringBuilder();
        var first = true;
        foreach (var item in enumerable)
        {
            if (!first) builder.Append('\n');
            builder.Append(Serialize(item));
            first = false;
        }
        return builder.ToString();
    }

    private static string SerializeDictionary(IDictionary dictionary)
    {
        var builder = new System.Text.StringBuilder();
        var first = true;
        foreach (DictionaryEntry entry in dictionary)
        {
            if (!first) builder.Append('\n');
            builder.Append(Serialize(entry.Key));
            builder.Append('\t');
            builder.Append(Serialize(entry.Value));
            first = false;
        }
        return builder.ToString();
    }
}
