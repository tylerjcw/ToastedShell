using System.Collections;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Drawing;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.IO.Compression;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Tosh.Stdlib.Shell;
using Tosh.Stdlib.Sys;
using Tosh.Runtime;
using Tosh.Stdlib.Net;

namespace Tosh.Stdlib;

public static partial class BuiltInDisplayProfiles
{
    public static void RegisterDefaults(DisplayProfileRegistry registry, DisplayPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(preferences);

        RegisterDataProfiles(registry, preferences);
        RegisterFilesystemProfiles(registry, preferences);
        RegisterProcessesProfiles(registry, preferences);
        RegisterSystemProfiles(registry, preferences);
        RegisterScientificProfiles(registry, preferences);
    }

    private static string FormatDictKey(object key)
    {
        return key is string s ? $"\"{s}\"" : key?.ToString() ?? "null";
    }

    private static string FormatJsonElementPreview(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => $"{{...}} ({element.EnumerateObject().Count()} properties)",
            JsonValueKind.Array => $"[...] ({element.GetArrayLength()} items)",
            _ => FormatJsonElementValue(element),
        };
    }

    private static string FormatJsonElementValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? "null",
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => "null",
            JsonValueKind.Undefined => "<undefined>",
            JsonValueKind.Object => $"{{...}} ({element.EnumerateObject().Count()} properties)",
            JsonValueKind.Array => $"[...] ({element.GetArrayLength()} items)",
            _ => element.GetRawText(),
        };
    }

    private static string FormatJsonNodePreview(JsonNode node)
    {
        return node switch
        {
            JsonObject obj => $"{{...}} ({obj.Count} properties)",
            JsonArray arr => $"[...] ({arr.Count} items)",
            JsonValue val => val.ToJsonString(),
            _ => node.ToJsonString(),
        };
    }

    private static string FormatByteArrayHexPreview(byte[] bytes, int maxBytes = 16)
    {
        if (bytes.Length == 0)
        {
            return string.Empty;
        }

        var slice = bytes.Take(maxBytes).Select(value => value.ToString("X2", CultureInfo.InvariantCulture));
        var text = string.Join(" ", slice);
        return bytes.Length > maxBytes ? $"{text} …" : text;
    }

    private static string? NullIfEmpty(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static object? GetKeyValuePairComponent(object row, string propertyName)
    {
        return row.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public)?.GetValue(row);
    }

    private static string FormatTuplePreview(ITuple tuple)
    {
        if (tuple.Length == 0)
        {
            return "()";
        }

        var items = Enumerable.Range(0, tuple.Length)
            .Select(index => FormatDisplaySummaryValue(tuple[index]));
        return $"({string.Join(", ", items)})";
    }

    private static string FormatHashSetPreview(object value)
    {
        if (value is not IEnumerable enumerable)
        {
            return "{::}";
        }

        var items = new List<string>();
        var count = 0;

        foreach (var item in enumerable)
        {
            if (count >= 6)
            {
                items.Add("...");
                break;
            }

            items.Add(FormatDisplaySummaryValue(item));
            count++;
        }

        return items.Count == 0
            ? "{::}"
            : $"{{: {string.Join(", ", items)} :}}";
    }

    private static string FormatDisplaySummaryValue(object? value)
    {
        if (value is null)
        {
            return "null";
        }

        if (ObjectFormatter.TryFormatSimple(value, isRoot: false, out var simpleText))
        {
            return simpleText;
        }

        return value switch
        {
            Type type => ReflectionMetadataUtilities.GetDisplayName(type),
            MethodBase methodBase => FormatMethodBaseSummary(methodBase),
            PropertyInfo property => FormatPropertyInfoSummary(property),
            ITuple tuple => FormatTuplePreview(tuple),
            EndPoint endPoint => FormatEndPointValue(endPoint),
            DictionaryEntry entry => FormatDictionaryEntrySummary(entry),
            AssemblyName assemblyName => assemblyName.FullName ?? assemblyName.Name ?? "<unknown>",
            Cookie cookie => FormatCookieSummary(cookie),
            CookieCollection cookies => FormatCookieCollectionSummary(cookies),
            CookieContainer cookieContainer => FormatCookieContainerSummary(cookieContainer),
            NetworkCredential credential => FormatNetworkCredentialSummary(credential),
            PhysicalAddress physicalAddress => FormatPhysicalAddressValue(physicalAddress),
            IPHostEntry hostEntry => FormatIpHostEntrySummary(hostEntry),
            WebHeaderCollection webHeaders => FormatWebHeaderCollectionSummary(webHeaders),
            FileVersionInfo fileVersionInfo => FormatFileVersionInfoSummary(fileVersionInfo),
            NetworkInterface networkInterface => FormatNetworkInterfaceSummary(networkInterface),
            AssemblyLoadContext loadContext => FormatAssemblyLoadContextSummary(loadContext),
            ProcessStartInfo startInfo => FormatProcessStartInfoSummary(startInfo),
            ProcessModule processModule => FormatProcessModuleSummary(processModule),
            FileSystemWatcher watcher => FormatFileSystemWatcherSummary(watcher),
            HttpRequestDefinition requestDefinition => FormatHttpRequestDefinitionSummary(requestDefinition),
            HttpResponseInfo responseInfo => FormatHttpResponseInfoSummary(responseInfo),
            HttpFileServerHandle serverHandle => FormatHttpFileServerHandleSummary(serverHandle),
            HttpRequestMessage request => FormatHttpRequestMessageSummary(request),
            HttpResponseMessage response => FormatHttpResponseMessageSummary(response),
            Assembly assembly => assembly.GetName().FullName ?? assembly.GetName().Name ?? "<unknown>",
            FieldInfo field => FormatFieldInfoSummary(field),
            EventInfo eventInfo => FormatEventInfoSummary(eventInfo),
            DriveInfo drive => FormatDriveInfoSummary(drive),
            Process process => FormatProcessSummary(process),
            ParameterInfo parameter => FormatParameterInfoSummary(parameter),
            HttpHeaders headers => FormatHttpHeaders(headers),
            HttpContent content => FormatHttpContentSummary(content),
            StackFrame frame => FormatStackFrameSummary(frame),
            StackTrace trace => FormatStackTraceSummary(trace),
            _ => value.ToString() ?? ObjectFormatter.GetTypeName(value.GetType()),
        };
    }

    private static string FormatDictionaryEntrySummary(DictionaryEntry entry)
    {
        return $"{FormatDisplaySummaryValue(entry.Key)} => {FormatDisplaySummaryValue(entry.Value)}";
    }

    private static string FormatCookieSummary(Cookie cookie)
    {
        var name = NullIfEmpty(cookie.Name) ?? "<unnamed>";
        var value = NullIfEmpty(cookie.Value) ?? "<empty>";
        var path = NullIfEmpty(cookie.Path);
        var domain = NullIfEmpty(cookie.Domain);

        var location = string.Join(
            " ",
            new[] { domain, path }
                .Where(part => !string.IsNullOrWhiteSpace(part)));

        return string.IsNullOrWhiteSpace(location)
            ? $"{name}={value}"
            : $"{name}={value} ({location})";
    }

    private static string FormatCookieCollectionSummary(CookieCollection cookies)
    {
        var count = cookies.Count.ToString(CultureInfo.InvariantCulture);
        var items = FormatCookieCollectionItems(cookies, maxItems: 4);
        return $"{count} cookie{(cookies.Count == 1 ? string.Empty : "s")}: {items}";
    }

    private static string FormatCookieCollectionItems(CookieCollection cookies, int maxItems = 12)
    {
        if (cookies.Count == 0)
        {
            return "<none>";
        }

        var items = cookies
            .Cast<Cookie>()
            .Select(FormatCookieSummary)
            .Take(maxItems + 1)
            .ToList();

        if (items.Count > maxItems)
        {
            items = items.Take(maxItems).Append("…").ToList();
        }

        return string.Join(Environment.NewLine, items);
    }

    private static string FormatCookieContainerSummary(CookieContainer container)
    {
        return $"{container.Count.ToString(CultureInfo.InvariantCulture)} cookies (capacity {container.Capacity.ToString(CultureInfo.InvariantCulture)})";
    }

    private static string FormatNetworkCredentialSummary(NetworkCredential credential)
    {
        var userName = NullIfEmpty(credential.UserName) ?? "<anonymous>";
        var domain = NullIfEmpty(credential.Domain);
        return string.IsNullOrWhiteSpace(domain)
            ? userName
            : $"{domain}\\{userName}";
    }

    private static string FormatPhysicalAddressValue(PhysicalAddress address)
    {
        var bytes = address.GetAddressBytes();

        if (bytes.Length == 0)
        {
            return "<none>";
        }

        return string.Join("-", bytes.Select(value => value.ToString("X2", CultureInfo.InvariantCulture)));
    }

    private static string FormatIpHostEntrySummary(IPHostEntry hostEntry)
    {
        var hostName = NullIfEmpty(hostEntry.HostName) ?? "<unknown>";
        return $"{hostName} ({hostEntry.AddressList.Length.ToString(CultureInfo.InvariantCulture)} addresses)";
    }

    private static string FormatWebHeaderCollectionSummary(WebHeaderCollection headers)
    {
        var count = headers.Count.ToString(CultureInfo.InvariantCulture);
        var keys = FormatStringCollection(headers.AllKeys.Where(static key => !string.IsNullOrWhiteSpace(key)).Cast<string>());
        return $"{count} headers: {keys}";
    }

    private static string FormatFileVersionInfoSummary(FileVersionInfo versionInfo)
    {
        var name = NullIfEmpty(versionInfo.ProductName) ??
                   NullIfEmpty(versionInfo.FileDescription) ??
                   NullIfEmpty(versionInfo.OriginalFilename) ??
                   Path.GetFileName(NullIfEmpty(versionInfo.FileName)) ??
                   "<unknown>";

        var version = NullIfEmpty(versionInfo.ProductVersion) ??
                      NullIfEmpty(versionInfo.FileVersion);

        return string.IsNullOrWhiteSpace(version)
            ? name
            : $"{name} {version}";
    }

    private static string FormatNetworkInterfaceSummary(NetworkInterface networkInterface)
    {
        var name = NullIfEmpty(networkInterface.Name) ?? "<unnamed>";
        return $"{name} ({networkInterface.NetworkInterfaceType}, {networkInterface.OperationalStatus})";
    }

    private static string FormatAssemblyLoadContextSummary(AssemblyLoadContext loadContext)
    {
        var name = NullIfEmpty(loadContext.Name) ?? "<anonymous>";
        var assemblyCount = GetAssemblyLoadContextAssemblyCount(loadContext);
        return $"{name} ({assemblyCount.ToString(CultureInfo.InvariantCulture)} assemblies)";
    }

    private static int GetAssemblyLoadContextAssemblyCount(AssemblyLoadContext loadContext)
    {
        return loadContext.Assemblies.Count();
    }

    private static string FormatProcessStartInfoSummary(ProcessStartInfo startInfo)
    {
        var fileName = NullIfEmpty(startInfo.FileName) ?? "<none>";
        var arguments = NullIfEmpty(startInfo.Arguments);

        return string.IsNullOrWhiteSpace(arguments)
            ? fileName
            : $"{fileName} {arguments}";
    }

    private static string FormatProcessModuleSummary(ProcessModule module)
    {
        var name = NullIfEmpty(module.ModuleName) ?? "<unnamed>";
        var size = StorageSize.FromBytes(module.ModuleMemorySize).ToString();
        return $"{name} ({size})";
    }

    private static string FormatFileSystemWatcherSummary(FileSystemWatcher watcher)
    {
        var path = NullIfEmpty(watcher.Path) ?? "<none>";
        var filter = NullIfEmpty(watcher.Filter) ?? "*.*";
        var state = watcher.EnableRaisingEvents ? "watching" : "idle";
        return $"{path} [{filter}] ({state})";
    }

    private static string FormatDriveInfoSummary(DriveInfo drive)
    {
        if (!drive.IsReady)
        {
            return $"{drive.Name} (not ready)";
        }

        var totalSize = SafeGetDriveSize(drive, static value => value.TotalSize);
        return $"{drive.Name} ({SafeGetDriveValue(drive, static value => value.DriveType)}, {totalSize})";
    }

    private static string FormatProcessSummary(Process process)
    {
        var info = ProcessInfo.From(process);
        return $"{info.Id.ToString(CultureInfo.InvariantCulture)} {info.Name}";
    }

    private static string FormatIndexValue(Index value)
    {
        return value.IsFromEnd
            ? $"^{value.Value.ToString(CultureInfo.InvariantCulture)}"
            : value.Value.ToString(CultureInfo.InvariantCulture);
    }

    private static string FormatRangeValue(Range value)
    {
        return $"{FormatIndexValue(value.Start)}..{FormatIndexValue(value.End)}";
    }

    private static string FormatEndPointValue(EndPoint value)
    {
        return value switch
        {
            IPEndPoint ipEndPoint => $"{ipEndPoint.Address}:{ipEndPoint.Port.ToString(CultureInfo.InvariantCulture)}",
            DnsEndPoint dnsEndPoint => $"{dnsEndPoint.Host}:{dnsEndPoint.Port.ToString(CultureInfo.InvariantCulture)}",
            _ => value.ToString() ?? ObjectFormatter.GetTypeName(value.GetType()),
        };
    }

    private static string FormatHttpRequestMessageSummary(HttpRequestMessage request)
    {
        var method = request.Method.Method;
        var target = request.RequestUri?.ToString() ?? "<no-uri>";
        return $"{method} {target}";
    }

    private static string FormatHttpRequestDefinitionSummary(HttpRequestDefinition request)
    {
        return $"{request.Method} {request.RequestUri}";
    }

    private static string FormatHttpResponseMessageSummary(HttpResponseMessage response)
    {
        return FormatHttpResponseStatus(response);
    }

    private static string FormatHttpResponseInfoSummary(HttpResponseInfo response)
    {
        var target = response.FinalUri ?? response.RequestUri;
        return target is null
            ? response.Status
            : $"{response.Status} {response.Method} {target}";
    }

    private static string FormatHttpFileServerHandleSummary(HttpFileServerHandle handle)
    {
        var status = handle.IsOpen ? "open" : "closed";
        var protection = handle.RequiresToken ? " protected" : string.Empty;
        return $"{status}{protection} {handle.ShareUrl} -> {handle.RootPath}";
    }

    private static string FormatHttpResponseStatus(HttpResponseMessage response)
    {
        var code = ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture);
        var reason = string.IsNullOrWhiteSpace(response.ReasonPhrase)
            ? response.StatusCode.ToString()
            : response.ReasonPhrase;
        return $"{code} {reason}";
    }

    private static string? FormatHttpVersion(Version? version)
    {
        return version is null ? null : version.ToString(2);
    }

    private static string FormatHttpContentSummary(HttpContent content)
    {
        var kind = ObjectFormatter.GetTypeName(content.GetType());
        var contentType = content.Headers.ContentType?.ToString();
        var length = content.Headers.ContentLength;

        if (!string.IsNullOrWhiteSpace(contentType) && length is long contentLength)
        {
            return $"{kind} ({contentType}, {contentLength.ToString(CultureInfo.InvariantCulture)} bytes)";
        }

        if (!string.IsNullOrWhiteSpace(contentType))
        {
            return $"{kind} ({contentType})";
        }

        if (length is long byteLength)
        {
            return $"{kind} ({byteLength.ToString(CultureInfo.InvariantCulture)} bytes)";
        }

        return kind;
    }

    private static string FormatProductInfoHeader(IEnumerable<ProductInfoHeaderValue> values)
    {
        return FormatCollectionHeader(values);
    }

    private static string FormatCollectionHeader<T>(IEnumerable<T> values)
    {
        if (values is null)
        {
            return "<none>";
        }

        var items = values
            .Select(value => value?.ToString())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Take(8 + 1)
            .ToList();

        if (items.Count == 0)
        {
            return "<none>";
        }

        if (items.Count > 8)
        {
            items = items.Take(8).Append("…").ToList();
        }

        return string.Join(", ", items);
    }

    private static string FormatStringCollection(IEnumerable<string> values)
    {
        return FormatCollectionHeader(values);
    }

    private static string FormatIpAddressCollection(IEnumerable<IPAddress> addresses)
    {
        var items = addresses
            .Select(address => address.ToString())
            .Where(address => !string.IsNullOrWhiteSpace(address))
            .Take(12 + 1)
            .ToList();

        if (items.Count == 0)
        {
            return "<none>";
        }

        if (items.Count > 12)
        {
            items = items.Take(12).Append("…").ToList();
        }

        return string.Join(Environment.NewLine, items);
    }

    private static string FormatHttpHeaders(HttpHeaders? headers, int maxEntries = 8)
    {
        if (headers is null)
        {
            return "<none>";
        }

        var entries = headers
            .Select(header => $"{header.Key}: {string.Join(", ", header.Value)}")
            .Take(maxEntries + 1)
            .ToList();

        if (entries.Count == 0)
        {
            return "<none>";
        }

        if (entries.Count > maxEntries)
        {
            entries = entries.Take(maxEntries).Append("…").ToList();
        }

        return string.Join(Environment.NewLine, entries);
    }

    private static string FormatHttpHeaderDictionary(IReadOnlyDictionary<string, IReadOnlyList<string>>? headers, int maxEntries = 8)
    {
        if (headers is null || headers.Count == 0)
        {
            return "<none>";
        }

        var entries = headers
            .Select(header => $"{header.Key}: {string.Join(", ", header.Value)}")
            .Take(maxEntries + 1)
            .ToList();

        if (entries.Count > maxEntries)
        {
            entries = entries.Take(maxEntries).Append("…").ToList();
        }

        return string.Join(Environment.NewLine, entries);
    }

    private static string FormatFieldInfoSummary(FieldInfo field)
    {
        var modifiers = new List<string>(3);

        if (field.IsStatic)
        {
            modifiers.Add("static");
        }

        if (field.IsInitOnly)
        {
            modifiers.Add("readonly");
        }

        if (field.IsLiteral)
        {
            modifiers.Add("const");
        }

        var prefix = modifiers.Count == 0 ? string.Empty : string.Join(' ', modifiers) + " ";
        return $"{prefix}{GetReadableTypeName(field.FieldType)} {field.Name}";
    }

    private static string FormatEventInfoSummary(EventInfo eventInfo)
    {
        return $"{GetReadableTypeName(eventInfo.EventHandlerType)} {eventInfo.Name}";
    }

    private static string FormatParameterInfoSummary(ParameterInfo parameter)
    {
        var prefix = parameter.IsOut ? "out " : parameter.ParameterType.IsByRef ? "ref " : string.Empty;
        return $"{prefix}{GetReadableTypeName(UnwrapByRef(parameter.ParameterType))} {parameter.Name}";
    }

    private static string? GetReadableTypeName(Type? type)
    {
        return type is null ? null : ReflectionMetadataUtilities.GetDisplayName(type);
    }

    private static Type UnwrapByRef(Type type)
    {
        return type.IsByRef ? type.GetElementType() ?? type : type;
    }

    private static string FormatMethodBaseSummary(MethodBase methodBase)
    {
        return methodBase switch
        {
            MethodInfo methodInfo => ReflectionMetadataUtilities.FormatMethodSignature(methodInfo),
            ConstructorInfo constructorInfo => ReflectionMetadataUtilities.FormatConstructorSignature(constructorInfo),
            _ => methodBase.Name,
        };
    }

    private static string FormatPropertyInfoSummary(PropertyInfo property)
    {
        var accessors = new List<string>(2);

        if (property.CanRead)
        {
            accessors.Add("get;");
        }

        if (property.CanWrite)
        {
            accessors.Add("set;");
        }

        var accessorBlock = accessors.Count == 0
            ? "{ }"
            : $"{{ {string.Join(' ', accessors)} }}";
        return $"{GetReadableTypeName(property.PropertyType)} {property.Name} {accessorBlock}";
    }

    private static string FormatStackFrameSummary(StackFrame frame)
    {
        var method = frame.GetMethod();
        var methodText = method is null ? "<unknown>" : FormatMethodBaseSummary(method);
        var file = NullIfEmpty(frame.GetFileName());
        var line = frame.GetFileLineNumber();

        if (!string.IsNullOrWhiteSpace(file) && line > 0)
        {
            return $"{methodText} at {file}:{line.ToString(CultureInfo.InvariantCulture)}";
        }

        return methodText;
    }

    private static int GetStackFrameCount(StackTrace trace)
    {
        return trace.FrameCount;
    }

    private static string FormatStackTraceSummary(StackTrace trace)
    {
        var count = GetStackFrameCount(trace);
        return $"{count.ToString(CultureInfo.InvariantCulture)} frame{(count == 1 ? string.Empty : "s")}";
    }

    private static string FormatDateTimeOffset(
        DateTimeOffset value,
        TemporalDisplayMode mode,
        string? format,
        Func<DateTimeOffset> nowProvider)
    {
        return mode switch
        {
            TemporalDisplayMode.Iso => value.ToString("O", CultureInfo.InvariantCulture),
            TemporalDisplayMode.Local => value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            TemporalDisplayMode.Relative => FormatRelativeTime(value, nowProvider()),
            TemporalDisplayMode.Unix => value.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            TemporalDisplayMode.Custom => value.ToString(format ?? "O", CultureInfo.InvariantCulture),
            _ => value.ToString("O", CultureInfo.InvariantCulture),
        };
    }

    private static string FormatRelativeTime(DateTimeOffset value, DateTimeOffset now)
    {
        var delta = value - now;
        var isFuture = delta >= TimeSpan.Zero;
        var elapsed = isFuture ? delta : -delta;

        if (elapsed < TimeSpan.FromSeconds(45))
        {
            return isFuture ? "in a few seconds" : "just now";
        }

        if (elapsed < TimeSpan.FromMinutes(90))
        {
            var minutes = Math.Max(1, (int)Math.Round(elapsed.TotalMinutes, MidpointRounding.AwayFromZero));
            return FormatRelativeUnit(minutes, "minute", isFuture);
        }

        if (elapsed < TimeSpan.FromHours(36))
        {
            var hours = Math.Max(1, (int)Math.Round(elapsed.TotalHours, MidpointRounding.AwayFromZero));
            return FormatRelativeUnit(hours, "hour", isFuture);
        }

        if (elapsed < TimeSpan.FromDays(30))
        {
            var days = Math.Max(1, (int)Math.Round(elapsed.TotalDays, MidpointRounding.AwayFromZero));
            return FormatRelativeUnit(days, "day", isFuture);
        }

        if (elapsed < TimeSpan.FromDays(365))
        {
            var months = Math.Max(1, (int)Math.Round(elapsed.TotalDays / 30d, MidpointRounding.AwayFromZero));
            return FormatRelativeUnit(months, "month", isFuture);
        }

        var years = Math.Max(1, (int)Math.Round(elapsed.TotalDays / 365d, MidpointRounding.AwayFromZero));
        return FormatRelativeUnit(years, "year", isFuture);
    }

    private static string FormatRelativeUnit(int value, string unit, bool isFuture)
    {
        var suffix = value == 1 ? unit : $"{unit}s";
        return isFuture ? $"in {value} {suffix}" : $"{value} {suffix} ago";
    }

    private static string FormatTimeSpan(TimeSpan value, DurationDisplayMode mode, string? format)
    {
        return mode switch
        {
            DurationDisplayMode.Raw => value.ToString("c", CultureInfo.InvariantCulture),
            DurationDisplayMode.Short => FormatShortDuration(value),
            DurationDisplayMode.Long => FormatLongDuration(value),
            DurationDisplayMode.TotalSeconds => FormatTotalSeconds(value),
            DurationDisplayMode.Custom => FormatCustomDuration(value, format),
            _ => value.ToString("c", CultureInfo.InvariantCulture),
        };
    }

    private static string FormatShortDuration(TimeSpan duration)
    {
        if (duration == TimeSpan.Zero)
        {
            return "0s";
        }

        var negative = duration < TimeSpan.Zero;
        var remaining = duration.Duration();
        var parts = new List<string>();

        if (remaining.Days > 0)
        {
            parts.Add($"{remaining.Days}d");
        }

        if (remaining.Hours > 0)
        {
            parts.Add($"{remaining.Hours}h");
        }

        if (remaining.Minutes > 0)
        {
            parts.Add($"{remaining.Minutes}m");
        }

        if (remaining.Seconds > 0)
        {
            parts.Add($"{remaining.Seconds}s");
        }

        if (parts.Count == 0)
        {
            if (remaining.Milliseconds > 0)
            {
                parts.Add($"{remaining.Milliseconds}ms");
            }
            else
            {
                parts.Add("0s");
            }
        }

        return negative ? "-" + string.Join(" ", parts) : string.Join(" ", parts);
    }

    private static string FormatLongDuration(TimeSpan duration)
    {
        if (duration == TimeSpan.Zero)
        {
            return "0 seconds";
        }

        var negative = duration < TimeSpan.Zero;
        var remaining = duration.Duration();
        var parts = new List<string>();

        AppendLongDurationUnit(parts, remaining.Days, "day");
        AppendLongDurationUnit(parts, remaining.Hours, "hour");
        AppendLongDurationUnit(parts, remaining.Minutes, "minute");
        AppendLongDurationUnit(parts, remaining.Seconds, "second");

        if (parts.Count == 0)
        {
            if (remaining.Milliseconds > 0)
            {
                AppendLongDurationUnit(parts, remaining.Milliseconds, "millisecond");
            }
            else
            {
                parts.Add("0 seconds");
            }
        }

        var text = string.Join(", ", parts);
        return negative ? "-" + text : text;
    }

    private static void AppendLongDurationUnit(List<string> parts, int value, string unit)
    {
        if (value == 0)
        {
            return;
        }

        parts.Add(value == 1 ? $"1 {unit}" : $"{value} {unit}s");
    }

    private static string FormatTotalSeconds(TimeSpan value)
    {
        var seconds = value.TotalSeconds;
        var wholeSeconds = Math.Round(seconds, MidpointRounding.AwayFromZero);

        if (Math.Abs(seconds - wholeSeconds) < 0.0000001d)
        {
            return wholeSeconds.ToString(CultureInfo.InvariantCulture);
        }

        return seconds.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static string FormatCustomDuration(TimeSpan value, string? format)
    {
        try
        {
            return value.ToString(string.IsNullOrWhiteSpace(format) ? "c" : format, CultureInfo.InvariantCulture);
        }
        catch (FormatException)
        {
            return value.ToString("c", CultureInfo.InvariantCulture);
        }
    }

    private static string FormatStorageSize(StorageSize size, StorageSizeDisplayMode mode)
    {
        if (mode == StorageSizeDisplayMode.Bytes)
        {
            return $"{size.Bytes.ToString(CultureInfo.InvariantCulture)} B";
        }

        var absoluteBytes = Math.Abs((decimal)size.Bytes);
        var sign = size.Bytes < 0 ? "-" : string.Empty;
        var units = new[] { "B", "kB", "MB", "GB", "TB", "PB" };
        var unitIndex = 0;
        var scaled = absoluteBytes;

        while (scaled >= 1000m && unitIndex < units.Length - 1)
        {
            scaled /= 1000m;
            unitIndex++;
        }

        var format = unitIndex == 0 || scaled >= 100m ? "0" : "0.#";
        return $"{sign}{scaled.ToString(format, CultureInfo.InvariantCulture)} {units[unitIndex]}";
    }

    private static string FormatUsePercent(int? percent)
    {
        return percent is null
            ? string.Empty
            : $"{percent.Value.ToString(CultureInfo.InvariantCulture)}%";
    }

    private static FileSystemEntry GetDisplayEntry(FileSystemInfo value)
    {
        return FileSystemEntry.From(value, preferLongDisplay: false);
    }

    private static object? SafeGetDriveValue<T>(DriveInfo drive, Func<DriveInfo, T> getValue)
    {
        try
        {
            return getValue(drive);
        }
        catch
        {
            return null;
        }
    }

    private static StorageSize? SafeGetDriveSize(DriveInfo drive, Func<DriveInfo, long> getValue)
    {
        try
        {
            return StorageSize.FromBytes(getValue(drive));
        }
        catch
        {
            return null;
        }
    }

    private static string FormatPermissions(UnixFileMode mode, UnixFileModeDisplayMode displayMode)
    {
        Span<char> characters = stackalloc char[9];
        characters[0] = HasMode(mode, UnixFileMode.UserRead) ? 'r' : '-';
        characters[1] = HasMode(mode, UnixFileMode.UserWrite) ? 'w' : '-';
        characters[2] = GetExecuteCharacter(mode, UnixFileMode.UserExecute, UnixFileMode.SetUser, 's', 'S');
        characters[3] = HasMode(mode, UnixFileMode.GroupRead) ? 'r' : '-';
        characters[4] = HasMode(mode, UnixFileMode.GroupWrite) ? 'w' : '-';
        characters[5] = GetExecuteCharacter(mode, UnixFileMode.GroupExecute, UnixFileMode.SetGroup, 's', 'S');
        characters[6] = HasMode(mode, UnixFileMode.OtherRead) ? 'r' : '-';
        characters[7] = HasMode(mode, UnixFileMode.OtherWrite) ? 'w' : '-';
        characters[8] = GetExecuteCharacter(mode, UnixFileMode.OtherExecute, UnixFileMode.StickyBit, 't', 'T');
        var symbolic = new string(characters);
        var octal = $"0{Convert.ToString((int)mode, 8)}";

        return displayMode switch
        {
            UnixFileModeDisplayMode.Octal => octal,
            UnixFileModeDisplayMode.Both => $"{symbolic} ({octal})",
            _ => symbolic,
        };
    }

    private static string FormatFileAttributes(FileAttributes attributes, FileAttributesDisplayMode displayMode)
    {
        var names = attributes.ToString();
        var hex = $"0x{((int)attributes):X}";

        return displayMode switch
        {
            FileAttributesDisplayMode.Hex => hex,
            FileAttributesDisplayMode.Both => $"{names} ({hex})",
            _ => names,
        };
    }

    private static string FormatColorName(Color color)
    {
        if (color.IsEmpty)
        {
            return "<empty>";
        }

        if (color.IsNamedColor || color.IsKnownColor)
        {
            return color.Name;
        }

        return FormatColorHex(color);
    }

    private static string FormatColorHex(Color color)
    {
        return color.A == byte.MaxValue
            ? $"#{color.R:X2}{color.G:X2}{color.B:X2}"
            : $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    private static IReadOnlyList<DisplayTableColumn> BuildRecordColumns(IReadOnlyList<object> rows)
    {
        var fieldNames = rows
            .SelectMany(row => ShellRecordUtilities.TryGetVisibleFields(row, out var fields)
                ? fields.Select(field => field.Key)
                : Array.Empty<string>())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return fieldNames
            .Select((fieldName, index) => new DisplayTableColumn(
                fieldName,
                row => ShellRecordUtilities.TryGetValue(row, fieldName, out var value) ? value : null,
                Alignment: ShouldRightAlignRecordField(rows, fieldName) ? DisplayTableAlignment.Right : DisplayTableAlignment.Left,
                Priority: index,
                CanHide: index > 0))
            .ToArray();
    }

    private static bool ShouldRightAlignRecordField(IReadOnlyList<object> rows, string fieldName)
    {
        foreach (var row in rows)
        {
            if (!ShellRecordUtilities.TryGetValue(row, fieldName, out var value) || value is null)
            {
                continue;
            }

            var effectiveType = Nullable.GetUnderlyingType(value.GetType()) ?? value.GetType();

            return effectiveType == typeof(byte) ||
                   effectiveType == typeof(short) ||
                   effectiveType == typeof(int) ||
                   effectiveType == typeof(long) ||
                   effectiveType == typeof(StorageSize) ||
                   effectiveType == typeof(float) ||
                   effectiveType == typeof(double) ||
                   effectiveType == typeof(decimal);
        }

        return false;
    }

    private static bool HasMode(UnixFileMode value, UnixFileMode flag) => (value & flag) == flag;

    private static char GetExecuteCharacter(
        UnixFileMode mode,
        UnixFileMode executeFlag,
        UnixFileMode specialFlag,
        char specialWhenExecute,
        char specialWhenNotExecute)
    {
        var hasExecute = HasMode(mode, executeFlag);
        var hasSpecial = HasMode(mode, specialFlag);

        if (hasSpecial)
        {
            return hasExecute ? specialWhenExecute : specialWhenNotExecute;
        }

        return hasExecute ? 'x' : '-';
    }


}
