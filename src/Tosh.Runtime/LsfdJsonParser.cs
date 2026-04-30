using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Tosh.Runtime;

public static class LsfdJsonParser
{
    public static LsfdParseResult Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new LsfdParseResult(Array.Empty<FileDescriptorInfo>(), Array.Empty<SystemCounterInfo>());
        }

        var rows = new List<FileDescriptorInfo>();
        var summary = new List<SystemCounterInfo>();

        foreach (var document in ParseDocuments(json))
        {
            using (document)
            {
                var root = document.RootElement;

                if (root.TryGetProperty("lsfd", out var rowsElement) &&
                    rowsElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in rowsElement.EnumerateArray())
                    {
                        rows.Add(ParseRow(item));
                    }
                }

                if (root.TryGetProperty("lsfd-summary", out var summaryElement) &&
                    summaryElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in summaryElement.EnumerateArray())
                    {
                        var counter = GetString(item, "counter");
                        var value = GetLong(item, "value");

                        if (!string.IsNullOrWhiteSpace(counter) && value.HasValue)
                        {
                            summary.Add(new SystemCounterInfo(counter!, value.Value));
                        }
                    }
                }
            }
        }

        return new LsfdParseResult(rows, summary);
    }

    private static FileDescriptorInfo ParseRow(JsonElement element)
    {
        var additionalFields = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (var property in element.EnumerateObject())
        {
            var key = property.Name.ToUpperInvariant();

            if (IsCoreField(key))
            {
                continue;
            }

            additionalFields[key] = ConvertJsonValue(property.Value);
        }

        return new FileDescriptorInfo
        {
            Command = GetString(element, "command"),
            ProcessId = GetInt(element, "pid"),
            ThreadId = GetInt(element, "tid"),
            User = GetString(element, "user"),
            Association = GetString(element, "assoc"),
            FileDescriptor = GetInt(element, "fd"),
            Mode = GetString(element, "mode"),
            ExtendedMode = GetString(element, "xmode"),
            Type = GetString(element, "type"),
            Source = GetString(element, "source"),
            MountId = GetInt(element, "mntid"),
            Inode = GetLong(element, "inode"),
            Name = GetString(element, "name"),
            Flags = GetString(element, "flags"),
            Owner = GetString(element, "owner"),
            Size = GetStorageSize(element, "size"),
            Position = GetLong(element, "pos"),
            Deleted = GetBool(element, "deleted"),
            Device = GetString(element, "dev"),
            MajorMinor = GetString(element, "maj:min"),
            SocketType = GetString(element, "sock.type"),
            SocketState = GetString(element, "sock.state"),
            SocketListening = GetBool(element, "sock.listening"),
            ProtocolName = GetString(element, "sock.protoname"),
            UnixPath = GetString(element, "unix.path"),
            InetLocalAddress = GetString(element, "inet.laddr"),
            InetRemoteAddress = GetString(element, "inet.raddr"),
            Inet6LocalAddress = GetString(element, "inet6.laddr"),
            Inet6RemoteAddress = GetString(element, "inet6.raddr"),
            TcpLocalAddress = GetString(element, "tcp.laddr"),
            TcpRemoteAddress = GetString(element, "tcp.raddr"),
            TcpLocalPort = GetInt(element, "tcp.lport"),
            TcpRemotePort = GetInt(element, "tcp.rport"),
            UdpLocalAddress = GetString(element, "udp.laddr"),
            UdpRemoteAddress = GetString(element, "udp.raddr"),
            UdpLocalPort = GetInt(element, "udp.lport"),
            UdpRemotePort = GetInt(element, "udp.rport"),
            AdditionalFields = additionalFields,
        };
    }

    private static IReadOnlyList<JsonDocument> ParseDocuments(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        var reader = new Utf8JsonReader(
            bytes,
            new JsonReaderOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowMultipleValues = true,
            });
        var documents = new List<JsonDocument>();

        while (reader.Read())
        {
            if (reader.TokenType is JsonTokenType.Comment or JsonTokenType.None)
            {
                continue;
            }

            documents.Add(JsonDocument.ParseValue(ref reader));
        }

        return documents;
    }

    private static bool IsCoreField(string key)
    {
        return key is "COMMAND" or "PID" or "TID" or "USER" or "ASSOC" or "FD" or "MODE" or "XMODE" or
               "TYPE" or "SOURCE" or "MNTID" or "INODE" or "NAME" or "FLAGS" or "OWNER" or "SIZE" or
               "POS" or "DELETED" or "DEV" or "MAJ:MIN" or "SOCK.TYPE" or "SOCK.STATE" or "SOCK.LISTENING" or
               "SOCK.PROTONAME" or "UNIX.PATH" or "INET.LADDR" or "INET.RADDR" or "INET6.LADDR" or
               "INET6.RADDR" or "TCP.LADDR" or "TCP.RADDR" or "TCP.LPORT" or "TCP.RPORT" or
               "UDP.LADDR" or "UDP.RADDR" or "UDP.LPORT" or "UDP.RPORT";
    }

    private static object? ConvertJsonValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.String => element.GetString(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when element.TryGetInt64(out var integer) => integer,
            JsonValueKind.Number when element.TryGetDouble(out var floating) => floating,
            _ => element.GetRawText(),
        };
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Number => property.GetRawText(),
            JsonValueKind.True => bool.TrueString.ToLowerInvariant(),
            JsonValueKind.False => bool.FalseString.ToLowerInvariant(),
            _ => property.GetRawText(),
        };
    }

    private static int? GetInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var direct))
        {
            return direct;
        }

        return int.TryParse(GetString(element, propertyName), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static long? GetLong(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var direct))
        {
            return direct;
        }

        return long.TryParse(GetString(element, propertyName), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static bool? GetBool(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when property.TryGetInt64(out var number) => number != 0,
            JsonValueKind.String when bool.TryParse(property.GetString(), out var parsed) => parsed,
            _ => null,
        };
    }

    private static StorageSize? GetStorageSize(JsonElement element, string propertyName)
    {
        var bytes = GetLong(element, propertyName);
        return bytes is long value ? StorageSize.FromBytes(value) : null;
    }
}

public sealed record LsfdParseResult(
    IReadOnlyList<FileDescriptorInfo> Rows,
    IReadOnlyList<SystemCounterInfo> Summary);
