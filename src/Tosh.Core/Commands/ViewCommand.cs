namespace Tosh.Core.Commands;

public sealed class ViewCommand : ShellCommand
{
    public ViewCommand()
        : base("view", "Gets or sets shell display preferences.", "view [compact|detail|datetime|datetimeoffset|size]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var parsed = ParsedCommandArguments.Parse(context.Arguments);

        if (parsed.Positionals.Count == 0)
        {
            yield return new FormatterStatus(context.Runtime.Display.Style);
            yield break;
        }

        var mode = CommandArguments.RequireString(parsed.Positionals, 0, "mode");

        if (string.Equals(mode, "compact", StringComparison.OrdinalIgnoreCase))
        {
            context.Runtime.Display.Style = ObjectRenderStyle.Compact;
            yield return new FormatterStatus(context.Runtime.Display.Style);
            yield break;
        }

        if (string.Equals(mode, "detail", StringComparison.OrdinalIgnoreCase))
        {
            context.Runtime.Display.Style = ObjectRenderStyle.Detail;
            yield return new FormatterStatus(context.Runtime.Display.Style);
            yield break;
        }

        if (TryResolveTemporalPreferences(context.Runtime.DisplayPreferences, mode, out var targetName, out var preferences))
        {
            foreach (var status in ConfigureTemporalPreferences(targetName, preferences, parsed.Positionals.Skip(1).ToArray()))
            {
                yield return status;
            }

            yield break;
        }

        if (IsStorageSizeTarget(mode))
        {
            foreach (var status in ConfigureStorageSizePreferences(context.Runtime.DisplayPreferences.StorageSize, parsed.Positionals.Skip(1).ToArray()))
            {
                yield return status;
            }

            yield break;
        }

        throw new InvalidOperationException("view mode must be 'compact', 'detail', 'datetime', 'datetimeoffset', or 'size'.");
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

    private static bool IsStorageSizeTarget(string target)
    {
        return string.Equals(target, "size", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(target, "storage-size", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(target, "storagesize", StringComparison.OrdinalIgnoreCase);
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

    private enum TemporalScope
    {
        Scalar,
        Table,
        All,
    }
}
