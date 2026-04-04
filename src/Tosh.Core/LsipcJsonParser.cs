using System.Dynamic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Text.Json;

namespace Tosh.Core;

public static class LsipcJsonParser
{
    public static IReadOnlyList<ExpandoObject> ParseRows(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<ExpandoObject>();
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("Expected the root lsipc JSON value to be an object.");
        }

        var firstArray = root.EnumerateObject()
            .FirstOrDefault(property => property.Value.ValueKind == JsonValueKind.Array);

        if (string.IsNullOrWhiteSpace(firstArray.Name))
        {
            return Array.Empty<ExpandoObject>();
        }

        var rows = new List<ExpandoObject>();

        foreach (var item in firstArray.Value.EnumerateArray())
        {
            rows.Add(ParseRow(item));
        }

        return rows;
    }

    private static ExpandoObject ParseRow(JsonElement element)
    {
        var fields = new List<KeyValuePair<string, object?>>();

        foreach (var property in element.EnumerateObject())
        {
            var name = GetShellPropertyName(property.Name);
            fields.Add(new KeyValuePair<string, object?>(name, ConvertValue(property.Name, property.Value)));
        }

        return ShellRecordUtilities.CreateExpando(fields);
    }

    private static object? ConvertValue(string jsonName, JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number)
        {
            if (value.TryGetInt64(out var integer))
            {
                return integer;
            }

            if (value.TryGetDouble(out var floating))
            {
                return floating;
            }
        }

        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return value.GetBoolean();
        }

        var text = value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : value.GetRawText();

        if (string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        if (IsTimeField(jsonName) &&
            DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var instant))
        {
            return instant;
        }

        if (IsCountField(jsonName) &&
            long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count))
        {
            return count;
        }

        if (IsSizeLikeField(jsonName))
        {
            if (TryParseLooseSize(text, out var size))
            {
                return size;
            }

            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bytes))
            {
                return StorageSize.FromBytes(bytes);
            }
        }

        if ((string.Equals(jsonName, "limit", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(jsonName, "used", StringComparison.OrdinalIgnoreCase)) &&
            long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var genericInteger))
        {
            return genericInteger;
        }

        return text;
    }

    private static bool IsTimeField(string name)
    {
        return name.Equals("ctime", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("mtime", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("attach", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("detach", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("send", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("recv", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("otime", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCountField(string name)
    {
        return name.Equals("id", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("nattch", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("cpid", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("lpid", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("lspid", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("lrpid", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("nsems", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("sval", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("msgs", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("cuid", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("cgid", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("uid", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("gid", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSizeLikeField(string name)
    {
        return name.Equals("size", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("usedbytes", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseLooseSize(string text, out StorageSize size)
    {
        if (StorageSize.TryParse(text, out size))
        {
            return true;
        }

        var normalized = text
            .Replace("KiB", "kib", StringComparison.OrdinalIgnoreCase)
            .Replace("MiB", "mib", StringComparison.OrdinalIgnoreCase)
            .Replace("GiB", "gib", StringComparison.OrdinalIgnoreCase)
            .Replace("TiB", "tib", StringComparison.OrdinalIgnoreCase)
            .Replace("PiB", "pib", StringComparison.OrdinalIgnoreCase)
            .Replace("KB", "kb", StringComparison.OrdinalIgnoreCase)
            .Replace("MB", "mb", StringComparison.OrdinalIgnoreCase)
            .Replace("GB", "gb", StringComparison.OrdinalIgnoreCase)
            .Replace("TB", "tb", StringComparison.OrdinalIgnoreCase)
            .Replace("PB", "pb", StringComparison.OrdinalIgnoreCase);

        normalized = Regex.Replace(normalized, "(?<=[0-9])K\\b", "kb", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, "(?<=[0-9])M\\b", "mb", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, "(?<=[0-9])G\\b", "gb", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, "(?<=[0-9])T\\b", "tb", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, "(?<=[0-9])P\\b", "pb", RegexOptions.IgnoreCase);

        return StorageSize.TryParse(normalized, out size);
    }

    private static string GetShellPropertyName(string jsonName)
    {
        return jsonName.ToLowerInvariant() switch
        {
            "key" => "Key",
            "id" => "Id",
            "owner" => "Owner",
            "perms" => "Permissions",
            "cuid" => "CreatorUid",
            "cuser" => "CreatorUser",
            "cgid" => "CreatorGid",
            "cgroup" => "CreatorGroup",
            "uid" => "Uid",
            "user" => "User",
            "gid" => "Gid",
            "group" => "Group",
            "ctime" => "Changed",
            "mtime" => "Modified",
            "size" => "Size",
            "nattch" => "AttachCount",
            "status" => "Status",
            "attach" => "Attached",
            "detach" => "Detached",
            "command" => "Command",
            "cpid" => "CreatorPid",
            "lpid" => "LastPid",
            "usedbytes" => "UsedBytes",
            "msgs" => "Messages",
            "send" => "LastSent",
            "recv" => "LastReceived",
            "lspid" => "LastSenderPid",
            "lrpid" => "LastReceiverPid",
            "nsems" => "SemaphoreCount",
            "otime" => "LastOperation",
            "name" => "Name",
            "sval" => "SemaphoreValue",
            "resource" => "Resource",
            "description" => "Description",
            "limit" => "Limit",
            "used" => "Used",
            "use%" => "UsePercent",
            _ => NormalizePropertyName(jsonName),
        };
    }

    private static string NormalizePropertyName(string jsonName)
    {
        var parts = jsonName
            .Split(['-', '_', '.', '/', ' ', '%'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length == 0)
        {
            return jsonName;
        }

        return string.Concat(parts.Select(part => char.ToUpperInvariant(part[0]) + part[1..]));
    }
}
