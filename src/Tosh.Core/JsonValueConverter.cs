using System.Text.Json;

namespace Tosh.Core;

public static class JsonValueConverter
{
    public static object? Convert(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => ConvertObject(element),
            JsonValueKind.Array => element.EnumerateArray().Select(Convert).ToArray(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => ConvertNumber(element),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Undefined => null,
            _ => element.ToString(),
        };
    }

    private static object ConvertObject(JsonElement element)
    {
        return ShellRecordUtilities.CreateExpando(element
            .EnumerateObject()
            .Select(property => new KeyValuePair<string, object?>(property.Name, Convert(property.Value))));
    }

    private static object ConvertNumber(JsonElement element)
    {
        if (element.TryGetInt64(out var integer))
        {
            return integer;
        }

        if (element.TryGetDecimal(out var decimalValue))
        {
            return decimalValue;
        }

        if (element.TryGetDouble(out var doubleValue))
        {
            return doubleValue;
        }

        return element.GetRawText();
    }
}
