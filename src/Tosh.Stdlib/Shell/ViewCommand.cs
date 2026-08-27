using Tosh.Runtime;

namespace Tosh.Stdlib.Shell;

[CommandCategory("Shell")]
[CommandExample("view dateonly scalar relative")]
[CommandExample("view timeonly table 24h")]
[CommandExample("view duration table seconds")]
[CommandOutput("Emits nothing; opens the supplied content in the configured pager/viewer as a side effect.")]
public sealed class ViewCommand : ShellCommand
{
    public ViewCommand()
        : base("view", "Gets or sets shell display preferences.", "view [compact|detail|datetime|datetimeoffset|dateonly|timeonly|timespan|size|permissions|attributes|columns]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var parsed = ParsedCommandArguments.Parse(context.Arguments);

        if (parsed.Positionals.Count == 0)
        {
            yield return new FormatterStatus(context.Shell().Display.Style);
            yield break;
        }

        var mode = CommandArguments.RequireString(parsed.Positionals, 0, "mode");

        if (string.Equals(mode, "compact", StringComparison.OrdinalIgnoreCase))
        {
            context.Shell().Display.Style = ObjectRenderStyle.Compact;
            yield return new FormatterStatus(context.Shell().Display.Style);
            yield break;
        }

        if (string.Equals(mode, "detail", StringComparison.OrdinalIgnoreCase))
        {
            context.Shell().Display.Style = ObjectRenderStyle.Detail;
            yield return new FormatterStatus(context.Shell().Display.Style);
            yield break;
        }

        if (TryResolveTemporalPreferences(context.Shell().DisplayPreferences, mode, out var targetName, out var preferences))
        {
            foreach (var status in ConfigureTemporalPreferences(targetName, preferences, parsed.Positionals.Skip(1).ToArray()))
            {
                yield return status;
            }

            yield break;
        }

        if (TryResolveDurationPreferences(context.Shell().DisplayPreferences, mode, out var durationTargetName, out var durationPreferences))
        {
            foreach (var status in ConfigureDurationPreferences(durationTargetName, durationPreferences, parsed.Positionals.Skip(1).ToArray()))
            {
                yield return status;
            }

            yield break;
        }

        if (TryResolveDateOnlyPreferences(context.Shell().DisplayPreferences, mode, out var dateOnlyTargetName, out var dateOnlyPreferences))
        {
            foreach (var status in ConfigureDateOnlyPreferences(dateOnlyTargetName, dateOnlyPreferences, parsed.Positionals.Skip(1).ToArray()))
            {
                yield return status;
            }

            yield break;
        }

        if (TryResolveTimeOnlyPreferences(context.Shell().DisplayPreferences, mode, out var timeOnlyTargetName, out var timeOnlyPreferences))
        {
            foreach (var status in ConfigureTimeOnlyPreferences(timeOnlyTargetName, timeOnlyPreferences, parsed.Positionals.Skip(1).ToArray()))
            {
                yield return status;
            }

            yield break;
        }

        if (IsStorageSizeTarget(mode))
        {
            foreach (var status in ConfigureStorageSizePreferences(context.Shell().DisplayPreferences.StorageSize, parsed.Positionals.Skip(1).ToArray()))
            {
                yield return status;
            }

            yield break;
        }

        if (IsUnixFileModeTarget(mode))
        {
            foreach (var status in ConfigureUnixFileModePreferences(context.Shell().DisplayPreferences.UnixFileMode, parsed.Positionals.Skip(1).ToArray()))
            {
                yield return status;
            }

            yield break;
        }

        if (IsFileAttributesTarget(mode))
        {
            foreach (var status in ConfigureFileAttributesPreferences(context.Shell().DisplayPreferences.FileAttributes, parsed.Positionals.Skip(1).ToArray()))
            {
                yield return status;
            }

            yield break;
        }

        if (string.Equals(mode, "columns", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var status in ConfigureTypeProfileColumns(context.Shell().DisplayPreferences.Profiles, parsed.Positionals.Skip(1).ToArray()))
            {
                yield return status;
            }

            yield break;
        }

        throw new InvalidOperationException("view mode must be 'compact', 'detail', 'datetime', 'datetimeoffset', 'dateonly', 'timeonly', 'timespan', 'size', 'permissions', 'attributes', or 'columns'.");
    }

    private static IEnumerable<DisplayPreferenceStatus> ConfigureTemporalPreferences(
        string targetName,
        TemporalDisplayPreferences preferences,
        IReadOnlyList<object?> arguments)
    {
        if (arguments.Count == 0)
        {
            return GetTemporalStatuses(targetName, preferences);
        }

        var scope = TemporalScope.All;
        var modeIndex = 0;

        if (TryParseScope(CommandArguments.RequireString(arguments, 0, "scope"), out var parsedScope))
        {
            scope = parsedScope;
            modeIndex = 1;
        }

        var modeText = CommandArguments.RequireString(arguments, modeIndex, "display mode");

        if (string.Equals(modeText, "default", StringComparison.OrdinalIgnoreCase))
        {
            preferences.Reset();
            return GetTemporalStatuses(targetName, preferences);
        }

        if (!TryParseTemporalMode(modeText, out var mode))
        {
            throw new InvalidOperationException("temporal display mode must be 'iso', 'local', 'relative', 'unix', 'format', or 'default'.");
        }

        string? format = null;

        if (mode == TemporalDisplayMode.Custom)
        {
            format = CommandArguments.RequireString(arguments, modeIndex + 1, "format");
        }

        ApplyTemporalMode(preferences, scope, mode, format);
        return GetTemporalStatuses(targetName, preferences);
    }

    private static IEnumerable<DisplayPreferenceStatus> ConfigureStorageSizePreferences(
        StorageSizeDisplayPreferences preferences,
        IReadOnlyList<object?> arguments)
    {
        if (arguments.Count == 0)
        {
            return GetStorageSizeStatuses(preferences);
        }

        var modeText = CommandArguments.RequireString(arguments, 0, "display mode");

        if (string.Equals(modeText, "default", StringComparison.OrdinalIgnoreCase))
        {
            preferences.Reset();
            return GetStorageSizeStatuses(preferences);
        }

        if (!Enum.TryParse<StorageSizeDisplayMode>(modeText, ignoreCase: true, out var mode))
        {
            throw new InvalidOperationException("size display mode must be 'human', 'bytes', or 'default'.");
        }

        preferences.Mode = mode;
        return GetStorageSizeStatuses(preferences);
    }

    private static IEnumerable<DisplayPreferenceStatus> ConfigureDurationPreferences(
        string targetName,
        DurationDisplayPreferences preferences,
        IReadOnlyList<object?> arguments)
    {
        if (arguments.Count == 0)
        {
            return GetDurationStatuses(targetName, preferences);
        }

        var scope = TemporalScope.All;
        var modeIndex = 0;

        if (TryParseScope(CommandArguments.RequireString(arguments, 0, "scope"), out var parsedScope))
        {
            scope = parsedScope;
            modeIndex = 1;
        }

        var modeText = CommandArguments.RequireString(arguments, modeIndex, "display mode");

        if (string.Equals(modeText, "default", StringComparison.OrdinalIgnoreCase))
        {
            preferences.Reset();
            return GetDurationStatuses(targetName, preferences);
        }

        if (!TryParseDurationMode(modeText, out var mode))
        {
            throw new InvalidOperationException("timespan display mode must be 'raw', 'short', 'long', 'seconds', 'format', or 'default'.");
        }

        string? format = null;

        if (mode == DurationDisplayMode.Custom)
        {
            format = CommandArguments.RequireString(arguments, modeIndex + 1, "format");
        }

        ApplyDurationMode(preferences, scope, mode, format);
        return GetDurationStatuses(targetName, preferences);
    }

    private static IEnumerable<DisplayPreferenceStatus> ConfigureDateOnlyPreferences(
        string targetName,
        DateOnlyDisplayPreferences preferences,
        IReadOnlyList<object?> arguments)
    {
        if (arguments.Count == 0)
        {
            return GetDateOnlyStatuses(targetName, preferences);
        }

        var scope = TemporalScope.All;
        var modeIndex = 0;

        if (TryParseScope(CommandArguments.RequireString(arguments, 0, "scope"), out var parsedScope))
        {
            scope = parsedScope;
            modeIndex = 1;
        }

        var modeText = CommandArguments.RequireString(arguments, modeIndex, "display mode");

        if (string.Equals(modeText, "default", StringComparison.OrdinalIgnoreCase))
        {
            preferences.Reset();
            return GetDateOnlyStatuses(targetName, preferences);
        }

        if (!TryParseDateOnlyMode(modeText, out var mode))
        {
            throw new InvalidOperationException("dateonly display mode must be 'iso', 'long', 'relative', 'format', or 'default'.");
        }

        string? format = null;

        if (mode == DateOnlyDisplayMode.Custom)
        {
            format = CommandArguments.RequireString(arguments, modeIndex + 1, "format");
        }

        ApplyDateOnlyMode(preferences, scope, mode, format);
        return GetDateOnlyStatuses(targetName, preferences);
    }

    private static IEnumerable<DisplayPreferenceStatus> ConfigureTimeOnlyPreferences(
        string targetName,
        TimeOnlyDisplayPreferences preferences,
        IReadOnlyList<object?> arguments)
    {
        if (arguments.Count == 0)
        {
            return GetTimeOnlyStatuses(targetName, preferences);
        }

        var scope = TemporalScope.All;
        var modeIndex = 0;

        if (TryParseScope(CommandArguments.RequireString(arguments, 0, "scope"), out var parsedScope))
        {
            scope = parsedScope;
            modeIndex = 1;
        }

        var modeText = CommandArguments.RequireString(arguments, modeIndex, "display mode");

        if (string.Equals(modeText, "default", StringComparison.OrdinalIgnoreCase))
        {
            preferences.Reset();
            return GetTimeOnlyStatuses(targetName, preferences);
        }

        if (!TryParseTimeOnlyMode(modeText, out var mode))
        {
            throw new InvalidOperationException("timeonly display mode must be '24h', '12h', 'format', or 'default'.");
        }

        string? format = null;

        if (mode == TimeOnlyDisplayMode.Custom)
        {
            format = CommandArguments.RequireString(arguments, modeIndex + 1, "format");
        }

        ApplyTimeOnlyMode(preferences, scope, mode, format);
        return GetTimeOnlyStatuses(targetName, preferences);
    }

    private static IEnumerable<DisplayPreferenceStatus> GetTemporalStatuses(string targetName, TemporalDisplayPreferences preferences)
    {
        yield return new DisplayPreferenceStatus(
            targetName,
            Scope: "scalar",
            Mode: preferences.ScalarMode.ToString().ToLowerInvariant(),
            Format: preferences.ScalarFormat);
        yield return new DisplayPreferenceStatus(
            targetName,
            Scope: "table",
            Mode: preferences.TableMode.ToString().ToLowerInvariant(),
            Format: preferences.TableFormat);
    }

    private static IEnumerable<DisplayPreferenceStatus> GetStorageSizeStatuses(StorageSizeDisplayPreferences preferences)
    {
        yield return new DisplayPreferenceStatus(
            Target: "storage-size",
            Scope: "all",
            Mode: preferences.Mode.ToString().ToLowerInvariant());
    }

    private static IEnumerable<DisplayPreferenceStatus> GetDurationStatuses(string targetName, DurationDisplayPreferences preferences)
    {
        yield return new DisplayPreferenceStatus(
            targetName,
            Scope: "scalar",
            Mode: GetDurationModeText(preferences.ScalarMode),
            Format: preferences.ScalarFormat);
        yield return new DisplayPreferenceStatus(
            targetName,
            Scope: "table",
            Mode: GetDurationModeText(preferences.TableMode),
            Format: preferences.TableFormat);
    }

    private static IEnumerable<DisplayPreferenceStatus> GetDateOnlyStatuses(string targetName, DateOnlyDisplayPreferences preferences)
    {
        yield return new DisplayPreferenceStatus(
            targetName,
            Scope: "scalar",
            Mode: GetDateOnlyModeText(preferences.ScalarMode),
            Format: preferences.ScalarFormat);
        yield return new DisplayPreferenceStatus(
            targetName,
            Scope: "table",
            Mode: GetDateOnlyModeText(preferences.TableMode),
            Format: preferences.TableFormat);
    }

    private static IEnumerable<DisplayPreferenceStatus> GetTimeOnlyStatuses(string targetName, TimeOnlyDisplayPreferences preferences)
    {
        yield return new DisplayPreferenceStatus(
            targetName,
            Scope: "scalar",
            Mode: GetTimeOnlyModeText(preferences.ScalarMode),
            Format: preferences.ScalarFormat);
        yield return new DisplayPreferenceStatus(
            targetName,
            Scope: "table",
            Mode: GetTimeOnlyModeText(preferences.TableMode),
            Format: preferences.TableFormat);
    }

    private static IEnumerable<DisplayPreferenceStatus> ConfigureUnixFileModePreferences(
        UnixFileModeDisplayPreferences preferences,
        IReadOnlyList<object?> arguments)
    {
        if (arguments.Count == 0)
        {
            return GetUnixFileModeStatuses(preferences);
        }

        var modeText = CommandArguments.RequireString(arguments, 0, "display mode");

        if (string.Equals(modeText, "default", StringComparison.OrdinalIgnoreCase))
        {
            preferences.Reset();
            return GetUnixFileModeStatuses(preferences);
        }

        if (!Enum.TryParse<UnixFileModeDisplayMode>(modeText, ignoreCase: true, out var mode))
        {
            throw new InvalidOperationException("permissions display mode must be 'symbolic', 'octal', 'both', or 'default'.");
        }

        preferences.Mode = mode;
        return GetUnixFileModeStatuses(preferences);
    }

    private static IEnumerable<DisplayPreferenceStatus> GetUnixFileModeStatuses(UnixFileModeDisplayPreferences preferences)
    {
        yield return new DisplayPreferenceStatus(
            Target: "permissions",
            Scope: "all",
            Mode: preferences.Mode.ToString().ToLowerInvariant());
    }

    private static IEnumerable<DisplayPreferenceStatus> ConfigureFileAttributesPreferences(
        FileAttributesDisplayPreferences preferences,
        IReadOnlyList<object?> arguments)
    {
        if (arguments.Count == 0)
        {
            return GetFileAttributesStatuses(preferences);
        }

        var modeText = CommandArguments.RequireString(arguments, 0, "display mode");

        if (string.Equals(modeText, "default", StringComparison.OrdinalIgnoreCase))
        {
            preferences.Reset();
            return GetFileAttributesStatuses(preferences);
        }

        if (!Enum.TryParse<FileAttributesDisplayMode>(modeText, ignoreCase: true, out var mode))
        {
            throw new InvalidOperationException("attributes display mode must be 'names', 'hex', 'both', or 'default'.");
        }

        preferences.Mode = mode;
        return GetFileAttributesStatuses(preferences);
    }

    private static IEnumerable<DisplayPreferenceStatus> GetFileAttributesStatuses(FileAttributesDisplayPreferences preferences)
    {
        yield return new DisplayPreferenceStatus(
            Target: "file-attributes",
            Scope: "all",
            Mode: preferences.Mode.ToString().ToLowerInvariant());
    }

    private static IEnumerable<object?> ConfigureTypeProfileColumns(
        DisplayProfilePreferences preferences,
        IReadOnlyList<object?> arguments)
    {
        if (arguments.Count == 0)
        {
            throw new InvalidOperationException("view columns requires a type name.");
        }

        var parsedTypeName = CommandArguments.RequireParsedTypeName(arguments, 0, "type name");
        var typeName = parsedTypeName.TypeName;

        if (arguments.Count == parsedTypeName.ConsumedArgumentCount)
        {
            yield return CreateTypeProfileStatus(typeName, preferences);
            yield break;
        }

        var firstValue = CommandArguments.RequireString(arguments, parsedTypeName.ConsumedArgumentCount, "column name or 'default'");

        if (string.Equals(firstValue, "default", StringComparison.OrdinalIgnoreCase))
        {
            preferences.Remove(typeName);
            yield return CreateTypeProfileStatus(typeName, preferences);
            yield break;
        }

        var profile = preferences.GetOrCreate(typeName);
        profile.SetTableColumns(arguments
            .Skip(parsedTypeName.ConsumedArgumentCount)
            .Select((argument, index) => CommandArguments.RequireString(arguments, parsedTypeName.ConsumedArgumentCount + index, "column name")));
        yield return CreateTypeProfileStatus(typeName, preferences);
    }

    private static IDictionary<string, object?> CreateTypeProfileStatus(string typeName, DisplayProfilePreferences preferences)
    {
        var hasOverride = preferences.TryGet(typeName, out var profile);
        var columns = hasOverride ? profile.TableColumns.ToArray() : Array.Empty<string>();

        return ShellRecordUtilities.CreateExpando(
        [
            new KeyValuePair<string, object?>("Type", typeName),
            new KeyValuePair<string, object?>("Source", hasOverride ? "custom" : "default"),
            new KeyValuePair<string, object?>("TableColumns", columns),
        ]);
    }

    private static void ApplyTemporalMode(
        TemporalDisplayPreferences preferences,
        TemporalScope scope,
        TemporalDisplayMode mode,
        string? format)
    {
        if (scope is TemporalScope.All or TemporalScope.Scalar)
        {
            preferences.ScalarMode = mode;
            preferences.ScalarFormat = mode == TemporalDisplayMode.Custom ? format : null;
        }

        if (scope is TemporalScope.All or TemporalScope.Table)
        {
            preferences.TableMode = mode;
            preferences.TableFormat = mode == TemporalDisplayMode.Custom ? format : null;
        }
    }

    private static bool TryResolveTemporalPreferences(
        DisplayPreferences preferences,
        string target,
        out string targetName,
        out TemporalDisplayPreferences temporalPreferences)
    {
        if (string.Equals(target, "datetime", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(target, "date-time", StringComparison.OrdinalIgnoreCase))
        {
            targetName = "datetime";
            temporalPreferences = preferences.DateTime;
            return true;
        }

        if (string.Equals(target, "datetimeoffset", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(target, "date-time-offset", StringComparison.OrdinalIgnoreCase))
        {
            targetName = "datetimeoffset";
            temporalPreferences = preferences.DateTimeOffset;
            return true;
        }

        targetName = string.Empty;
        temporalPreferences = null!;
        return false;
    }

    private static bool TryResolveDurationPreferences(
        DisplayPreferences preferences,
        string target,
        out string targetName,
        out DurationDisplayPreferences durationPreferences)
    {
        if (string.Equals(target, "timespan", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(target, "time-span", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(target, "duration", StringComparison.OrdinalIgnoreCase))
        {
            targetName = "timespan";
            durationPreferences = preferences.TimeSpan;
            return true;
        }

        targetName = string.Empty;
        durationPreferences = null!;
        return false;
    }

    private static bool TryResolveDateOnlyPreferences(
        DisplayPreferences preferences,
        string target,
        out string targetName,
        out DateOnlyDisplayPreferences dateOnlyPreferences)
    {
        if (string.Equals(target, "dateonly", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(target, "date-only", StringComparison.OrdinalIgnoreCase))
        {
            targetName = "dateonly";
            dateOnlyPreferences = preferences.DateOnly;
            return true;
        }

        targetName = string.Empty;
        dateOnlyPreferences = null!;
        return false;
    }

    private static bool TryResolveTimeOnlyPreferences(
        DisplayPreferences preferences,
        string target,
        out string targetName,
        out TimeOnlyDisplayPreferences timeOnlyPreferences)
    {
        if (string.Equals(target, "timeonly", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(target, "time-only", StringComparison.OrdinalIgnoreCase))
        {
            targetName = "timeonly";
            timeOnlyPreferences = preferences.TimeOnly;
            return true;
        }

        targetName = string.Empty;
        timeOnlyPreferences = null!;
        return false;
    }

    private static bool IsStorageSizeTarget(string target)
    {
        return string.Equals(target, "size", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(target, "storage-size", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(target, "storagesize", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUnixFileModeTarget(string target)
    {
        return string.Equals(target, "permissions", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(target, "permission", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(target, "mode", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(target, "unix-file-mode", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFileAttributesTarget(string target)
    {
        return string.Equals(target, "attributes", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(target, "file-attributes", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(target, "fileattributes", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseScope(string text, out TemporalScope scope)
    {
        if (string.Equals(text, "scalar", StringComparison.OrdinalIgnoreCase))
        {
            scope = TemporalScope.Scalar;
            return true;
        }

        if (string.Equals(text, "table", StringComparison.OrdinalIgnoreCase))
        {
            scope = TemporalScope.Table;
            return true;
        }

        if (string.Equals(text, "all", StringComparison.OrdinalIgnoreCase))
        {
            scope = TemporalScope.All;
            return true;
        }

        scope = TemporalScope.All;
        return false;
    }

    private static bool TryParseTemporalMode(string text, out TemporalDisplayMode mode)
    {
        if (string.Equals(text, "format", StringComparison.OrdinalIgnoreCase))
        {
            mode = TemporalDisplayMode.Custom;
            return true;
        }

        return Enum.TryParse(text, ignoreCase: true, out mode);
    }

    private static bool TryParseDurationMode(string text, out DurationDisplayMode mode)
    {
        if (string.Equals(text, "format", StringComparison.OrdinalIgnoreCase))
        {
            mode = DurationDisplayMode.Custom;
            return true;
        }

        if (string.Equals(text, "seconds", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(text, "total-seconds", StringComparison.OrdinalIgnoreCase))
        {
            mode = DurationDisplayMode.TotalSeconds;
            return true;
        }

        return Enum.TryParse(text, ignoreCase: true, out mode);
    }

    private static bool TryParseDateOnlyMode(string text, out DateOnlyDisplayMode mode)
    {
        if (string.Equals(text, "format", StringComparison.OrdinalIgnoreCase))
        {
            mode = DateOnlyDisplayMode.Custom;
            return true;
        }

        return Enum.TryParse(text, ignoreCase: true, out mode);
    }

    private static bool TryParseTimeOnlyMode(string text, out TimeOnlyDisplayMode mode)
    {
        if (string.Equals(text, "format", StringComparison.OrdinalIgnoreCase))
        {
            mode = TimeOnlyDisplayMode.Custom;
            return true;
        }

        if (string.Equals(text, "24h", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(text, "24-hour", StringComparison.OrdinalIgnoreCase))
        {
            mode = TimeOnlyDisplayMode.TwentyFourHour;
            return true;
        }

        if (string.Equals(text, "12h", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(text, "12-hour", StringComparison.OrdinalIgnoreCase))
        {
            mode = TimeOnlyDisplayMode.TwelveHour;
            return true;
        }

        return Enum.TryParse(text, ignoreCase: true, out mode);
    }

    private static void ApplyDurationMode(
        DurationDisplayPreferences preferences,
        TemporalScope scope,
        DurationDisplayMode mode,
        string? format)
    {
        if (scope is TemporalScope.All or TemporalScope.Scalar)
        {
            preferences.ScalarMode = mode;
            preferences.ScalarFormat = mode == DurationDisplayMode.Custom ? format : null;
        }

        if (scope is TemporalScope.All or TemporalScope.Table)
        {
            preferences.TableMode = mode;
            preferences.TableFormat = mode == DurationDisplayMode.Custom ? format : null;
        }
    }

    private static void ApplyDateOnlyMode(
        DateOnlyDisplayPreferences preferences,
        TemporalScope scope,
        DateOnlyDisplayMode mode,
        string? format)
    {
        if (scope is TemporalScope.All or TemporalScope.Scalar)
        {
            preferences.ScalarMode = mode;
            preferences.ScalarFormat = mode == DateOnlyDisplayMode.Custom ? format : null;
        }

        if (scope is TemporalScope.All or TemporalScope.Table)
        {
            preferences.TableMode = mode;
            preferences.TableFormat = mode == DateOnlyDisplayMode.Custom ? format : null;
        }
    }

    private static void ApplyTimeOnlyMode(
        TimeOnlyDisplayPreferences preferences,
        TemporalScope scope,
        TimeOnlyDisplayMode mode,
        string? format)
    {
        if (scope is TemporalScope.All or TemporalScope.Scalar)
        {
            preferences.ScalarMode = mode;
            preferences.ScalarFormat = mode == TimeOnlyDisplayMode.Custom ? format : null;
        }

        if (scope is TemporalScope.All or TemporalScope.Table)
        {
            preferences.TableMode = mode;
            preferences.TableFormat = mode == TimeOnlyDisplayMode.Custom ? format : null;
        }
    }

    private static string GetDurationModeText(DurationDisplayMode mode)
    {
        return mode == DurationDisplayMode.TotalSeconds
            ? "seconds"
            : mode.ToString().ToLowerInvariant();
    }

    private static string GetDateOnlyModeText(DateOnlyDisplayMode mode)
    {
        return mode == DateOnlyDisplayMode.Custom
            ? "format"
            : mode.ToString().ToLowerInvariant();
    }

    private static string GetTimeOnlyModeText(TimeOnlyDisplayMode mode)
    {
        return mode switch
        {
            TimeOnlyDisplayMode.TwentyFourHour => "24h",
            TimeOnlyDisplayMode.TwelveHour => "12h",
            TimeOnlyDisplayMode.Custom => "format",
            _ => mode.ToString().ToLowerInvariant(),
        };
    }

    private enum TemporalScope
    {
        Scalar,
        Table,
        All,
    }
}
