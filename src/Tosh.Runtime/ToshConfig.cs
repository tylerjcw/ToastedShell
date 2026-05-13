namespace Tosh.Runtime;

public interface IResettableShellConfig
{
    void Reset();
}

public sealed class ToshConfig : IResettableShellConfig
{
    public ToshConfig(DisplayEngine display, DisplayPreferences displayPreferences, string startupRootDirectory)
    {
        ArgumentNullException.ThrowIfNull(display);
        ArgumentNullException.ThrowIfNull(displayPreferences);
        ArgumentException.ThrowIfNullOrWhiteSpace(startupRootDirectory);

        Theme = new ToshThemeConfig();
        Display = new ToshDisplayConfig(display, displayPreferences);
        Repl = new ToshReplConfig();
        Prompt = new ToshPromptConfig(Theme.Prompt);
        Shell = new ToshShellConfig();
        History = new ToshHistoryConfig(ToshConfigDefaults.GetDefaultStateDirectory());
        Startup = new ToshStartupConfig(startupRootDirectory);
        Tty = new ToshTtyConfig();
        Diagnostics = new ToshDiagnosticsConfig();
        Renderers = new ToshRenderersConfig();
        Schemas = new ToshSchemasConfig();
        External = new ToshExternalConfig();
    }

    public ToshThemeConfig Theme { get; }

    public ToshDisplayConfig Display { get; }

    public ToshReplConfig Repl { get; }

    public ToshPromptConfig Prompt { get; }

    public ToshShellConfig Shell { get; }

    public ToshHistoryConfig History { get; }

    public ToshStartupConfig Startup { get; }

    public ToshTtyConfig Tty { get; }

    public ToshDiagnosticsConfig Diagnostics { get; }

    public ToshRenderersConfig Renderers { get; }

    public ToshSchemasConfig Schemas { get; }

    public ToshExternalConfig External { get; }

    public void Reset()
    {
        Theme.Reset();
        Display.Reset();
        Repl.Reset();
        Prompt.Reset();
        Shell.Reset();
        History.Reset();
        Startup.Reset();
        Tty.Reset();
        Diagnostics.Reset();
        Renderers.Reset();
        Schemas.Reset();
        External.Reset();
    }
}

public sealed class ToshShellConfig : IResettableShellConfig
{
    public bool Pipefail { get; set; }

    public bool ExitOnError { get; set; }

    public bool Trace { get; set; }

    public bool ScriptTrace { get; set; }

    public bool AutoCd { get; set; }

    public ToshDirectoryAliasConfig Dirs { get; } = new();

    public ToshUsingsConfig Usings { get; } = new();

    public void Reset()
    {
        Pipefail = false;
        ExitOnError = false;
        Trace = false;
        ScriptTrace = false;
        AutoCd = false;
        Dirs.Reset();
        Usings.Reset();
    }
}

public sealed class ToshDirectoryAliasConfig : IResettableShellConfig, IShellRecordObject
{
    private readonly Dictionary<string, string> _aliases = new(StringComparer.OrdinalIgnoreCase);

    public string ShellTypeName => "DirectoryAliases";

    public IReadOnlyDictionary<string, string> Aliases => _aliases;

    public bool TryGetMember(string name, out object? value, bool includeHidden = false)
    {
        if (_aliases.TryGetValue(name, out var path))
        {
            value = path;
            return true;
        }

        value = null;
        return false;
    }

    public bool TrySetMember(string name, object? value)
    {
        if (value is null)
        {
            _aliases.Remove(name);
            return true;
        }

        var path = value.ToString();

        if (string.IsNullOrWhiteSpace(path))
        {
            _aliases.Remove(name);
            return true;
        }

        _aliases[name] = Path.GetFullPath(path);
        return true;
    }

    public IReadOnlyList<KeyValuePair<string, object?>> GetMembers(bool includeHidden = false)
    {
        return _aliases
            .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
            .Select(entry => new KeyValuePair<string, object?>(entry.Key, entry.Value))
            .ToArray();
    }

    public bool TryResolve(string name, out string resolvedPath)
    {
        return _aliases.TryGetValue(name, out resolvedPath!);
    }

    public string? TryReverseLookup(string absolutePath)
    {
        string? bestAlias = null;
        var bestLength = 0;

        foreach (var (alias, aliasPath) in _aliases)
        {
            if (absolutePath.Equals(aliasPath, PathUtilities.GetPathComparison()) ||
                (absolutePath.StartsWith(aliasPath, PathUtilities.GetPathComparison()) &&
                 absolutePath.Length > aliasPath.Length &&
                 absolutePath[aliasPath.Length] == Path.DirectorySeparatorChar))
            {
                if (aliasPath.Length > bestLength)
                {
                    bestLength = aliasPath.Length;
                    bestAlias = alias;
                }
            }
        }

        return bestAlias;
    }

    public void Reset()
    {
        _aliases.Clear();
    }
}

public sealed class ToshThemeConfig : IResettableShellConfig
{
    public ToshThemeConfig()
    {
        Prompt = new ToshPromptThemeConfig();
        Syntax = new ToshSyntaxThemeConfig();
        Completion = new ToshCompletionThemeConfig();
        Diagnostics = new ToshDiagnosticThemeConfig();
        Tables = new ToshTableThemeConfig();
        Tui = new ToshTuiThemeConfig();
    }

    public ToshPromptThemeConfig Prompt { get; }

    public ToshSyntaxThemeConfig Syntax { get; }

    public ToshCompletionThemeConfig Completion { get; }

    public ToshDiagnosticThemeConfig Diagnostics { get; }

    public ToshTableThemeConfig Tables { get; }

    public ToshTuiThemeConfig Tui { get; }

    public void Reset()
    {
        Prompt.Reset();
        Syntax.Reset();
        Completion.Reset();
        Diagnostics.Reset();
        Tables.Reset();
        Tui.Reset();
    }
}

public enum ToshTableBoxStyle
{
    Rounded,
    Square,
    Heavy,
    Ascii,
    Double,
}

public enum ToshTuiTreeStyle
{
    Clean,
    Dense,
}

public sealed class ToshTextStyleConfig : IResettableShellConfig
{
    private readonly string? _defaultForeground;
    private readonly string? _defaultBackground;
    private readonly bool _defaultBold;
    private readonly bool _defaultItalic;
    private readonly bool _defaultUnderline;
    private readonly bool _defaultDim;

    public ToshTextStyleConfig(
        string? foreground = null,
        string? background = null,
        bool bold = false,
        bool italic = false,
        bool underline = false,
        bool dim = false)
    {
        _defaultForeground = foreground;
        _defaultBackground = background;
        _defaultBold = bold;
        _defaultItalic = italic;
        _defaultUnderline = underline;
        _defaultDim = dim;

        Foreground = foreground;
        Background = background;
        Bold = bold;
        Italic = italic;
        Underline = underline;
        Dim = dim;
    }

    public string? Foreground { get; set; }

    public string? Background { get; set; }

    public bool Bold { get; set; }

    public bool Italic { get; set; }

    public bool Underline { get; set; }

    public bool Dim { get; set; }

    public StyledText Apply(string text)
    {
        return new StyledText(text, Foreground, Background, Bold, Italic, Underline, Dim);
    }

    public void Reset()
    {
        Foreground = _defaultForeground;
        Background = _defaultBackground;
        Bold = _defaultBold;
        Italic = _defaultItalic;
        Underline = _defaultUnderline;
        Dim = _defaultDim;
    }
}

public sealed class ToshPromptThemeConfig : IResettableShellConfig
{
    public ToshPromptThemeConfig()
    {
        Time = new ToshTextStyleConfig(foreground: "gray", dim: true);
        Directory = new ToshTextStyleConfig(foreground: "blue", bold: true);
        Git = new ToshTextStyleConfig(foreground: "green");
        UserHost = new ToshTextStyleConfig(foreground: "gray", dim: true);
        HistoryId = new ToshTextStyleConfig(foreground: "gray", dim: true);
        Jobs = new ToshTextStyleConfig(foreground: "yellow");
        Duration = new ToshTextStyleConfig(foreground: "magenta", dim: true);
        ExitCode = new ToshTextStyleConfig(foreground: "red", bold: true);
        Name = new ToshTextStyleConfig(foreground: "cyan", bold: true);
        Indicator = new ToshTextStyleConfig(foreground: "bright-cyan");
    }

    public ToshTextStyleConfig Time { get; }

    public ToshTextStyleConfig Directory { get; }

    public ToshTextStyleConfig Git { get; }

    public ToshTextStyleConfig UserHost { get; }

    public ToshTextStyleConfig HistoryId { get; }

    public ToshTextStyleConfig Jobs { get; }

    public ToshTextStyleConfig Duration { get; }

    public ToshTextStyleConfig ExitCode { get; }

    public ToshTextStyleConfig Name { get; }

    public ToshTextStyleConfig Indicator { get; }

    public void Reset()
    {
        Time.Reset();
        Directory.Reset();
        Git.Reset();
        UserHost.Reset();
        HistoryId.Reset();
        Jobs.Reset();
        Duration.Reset();
        ExitCode.Reset();
        Name.Reset();
        Indicator.Reset();
    }
}

public sealed class ToshSyntaxThemeConfig : IResettableShellConfig
{
    public ToshSyntaxThemeConfig()
    {
        Keyword = new ToshTextStyleConfig(foreground: "cyan");
        ControlFlow = new ToshTextStyleConfig(foreground: "blue");       // if/else/for/while/return/throw/…
        LanguageForm = new ToshTextStyleConfig(foreground: "cyan");
        Operator = new ToshTextStyleConfig(foreground: "red");
        String = new ToshTextStyleConfig(foreground: "green");             // 'raw', """triple"""
        EscapedString = new ToshTextStyleConfig(foreground: "#73c991");   // "double-quoted", $'ansi-c'
        InterpolatedString = new ToshTextStyleConfig(foreground: "bright-green"); // $"…{expr}…"
        Number = new ToshTextStyleConfig(foreground: "yellow");            // integer
        FloatNumber = new ToshTextStyleConfig(foreground: "bright-yellow"); // 3.14, 1e10
        HexNumber = new ToshTextStyleConfig(foreground: "#d4a017");       // 0xFF
        UnitLiteral = new ToshTextStyleConfig(foreground: "magenta");     // 100`m
        Constant = new ToshTextStyleConfig(foreground: "magenta");
        Variable = new ToshTextStyleConfig(foreground: "bright-cyan");
        Flag = new ToshTextStyleConfig(foreground: "gray");
        Comment = new ToshTextStyleConfig(foreground: "gray");
        Type = new ToshTextStyleConfig(foreground: "bright-cyan");
        Namespace = new ToshTextStyleConfig(foreground: "cyan", dim: true);
        Punctuation = new ToshTextStyleConfig(foreground: "white");
        Subexpression = new ToshTextStyleConfig(foreground: "bright-cyan");
        ValidCommand = new ToshTextStyleConfig(foreground: "green", bold: true);
        InvalidCommand = new ToshTextStyleConfig(foreground: "red");
        Path = new ToshTextStyleConfig(foreground: "green", underline: true);
        Argument = new ToshTextStyleConfig(foreground: "green");
    }

    public ToshTextStyleConfig Keyword { get; }

    /// <summary>Control-flow keywords: if, else, for, while, until, break, continue, return, throw, try, catch, finally, switch, case, default, match.</summary>
    public ToshTextStyleConfig ControlFlow { get; }

    public ToshTextStyleConfig LanguageForm { get; }

    public ToshTextStyleConfig Operator { get; }

    /// <summary>Raw / single-quoted strings ('…') and triple-double-quoted raw strings ("""…""").</summary>
    public ToshTextStyleConfig String { get; }

    /// <summary>Double-quoted strings ("…") and ANSI-C escape strings ($'…').</summary>
    public ToshTextStyleConfig EscapedString { get; }

    /// <summary>Interpolated strings ($"…{expr}…") — all flavours.</summary>
    public ToshTextStyleConfig InterpolatedString { get; }

    /// <summary>Integer literals (decimal).</summary>
    public ToshTextStyleConfig Number { get; }

    /// <summary>Floating-point literals (3.14, 1e-10).</summary>
    public ToshTextStyleConfig FloatNumber { get; }

    /// <summary>Hexadecimal literals (0xFF).</summary>
    public ToshTextStyleConfig HexNumber { get; }

    /// <summary>Unit literals (100`m, 9.8`m/s^2).</summary>
    public ToshTextStyleConfig UnitLiteral { get; }

    public ToshTextStyleConfig Constant { get; }

    public ToshTextStyleConfig Variable { get; }

    public ToshTextStyleConfig Flag { get; }

    public ToshTextStyleConfig Comment { get; }

    public ToshTextStyleConfig Type { get; }

    public ToshTextStyleConfig Namespace { get; }

    public ToshTextStyleConfig Punctuation { get; }

    public ToshTextStyleConfig Subexpression { get; }

    public ToshTextStyleConfig ValidCommand { get; }

    public ToshTextStyleConfig InvalidCommand { get; }

    public ToshTextStyleConfig Path { get; }

    public ToshTextStyleConfig Argument { get; }

    public void Reset()
    {
        Keyword.Reset();
        ControlFlow.Reset();
        LanguageForm.Reset();
        Operator.Reset();
        String.Reset();
        EscapedString.Reset();
        InterpolatedString.Reset();
        Number.Reset();
        FloatNumber.Reset();
        HexNumber.Reset();
        UnitLiteral.Reset();
        Constant.Reset();
        Variable.Reset();
        Flag.Reset();
        Comment.Reset();
        Type.Reset();
        Namespace.Reset();
        Punctuation.Reset();
        Subexpression.Reset();
        ValidCommand.Reset();
        InvalidCommand.Reset();
        Path.Reset();
        Argument.Reset();
    }
}

public sealed class ToshCompletionThemeConfig : IResettableShellConfig
{
    public ToshCompletionThemeConfig()
    {
        Header = new ToshTextStyleConfig(foreground: "gray", dim: true);
        SelectedPointer = new ToshTextStyleConfig(foreground: "bright-white");
        SelectedLabel = new ToshTextStyleConfig(foreground: "cyan");
        Item = new ToshTextStyleConfig(foreground: "gray", dim: true);
        Detail = new ToshTextStyleConfig(foreground: "gray", dim: true);
        Footer = new ToshTextStyleConfig(foreground: "gray", dim: true);
        GhostText = new ToshTextStyleConfig(foreground: "gray", dim: true);
    }

    public ToshTextStyleConfig Header { get; }

    public ToshTextStyleConfig SelectedPointer { get; }

    public ToshTextStyleConfig SelectedLabel { get; }

    public ToshTextStyleConfig Item { get; }

    public ToshTextStyleConfig Detail { get; }

    public ToshTextStyleConfig Footer { get; }

    public ToshTextStyleConfig GhostText { get; }

    public void Reset()
    {
        Header.Reset();
        SelectedPointer.Reset();
        SelectedLabel.Reset();
        Item.Reset();
        Detail.Reset();
        Footer.Reset();
        GhostText.Reset();
    }
}

public sealed class ToshDiagnosticThemeConfig : IResettableShellConfig
{
    public ToshDiagnosticThemeConfig()
    {
        Heading = new ToshTextStyleConfig(foreground: "red", bold: true);
        Title = new ToshTextStyleConfig(foreground: "red");
        SourceLocation = new ToshTextStyleConfig(foreground: "gray", dim: true);
        Underline = new ToshTextStyleConfig(foreground: "red");
        Label = new ToshTextStyleConfig(foreground: "red");
        Help = new ToshTextStyleConfig(foreground: "bright-cyan");
        Frame = new ToshTextStyleConfig(foreground: "gray", dim: true);
        Code = new ToshTextStyleConfig(foreground: "gray", dim: true);
        ErrorGlyph = new ToshTextStyleConfig(foreground: "bright-red", bold: true);
        WarningGlyph = new ToshTextStyleConfig(foreground: "bright-yellow", bold: true);
        InfoGlyph = new ToshTextStyleConfig(foreground: "bright-blue", bold: true);
        HintGlyph = new ToshTextStyleConfig(foreground: "gray", dim: true);
    }

    public ToshTextStyleConfig Heading { get; }

    public ToshTextStyleConfig Title { get; }

    public ToshTextStyleConfig SourceLocation { get; }

    public ToshTextStyleConfig Underline { get; }

    public ToshTextStyleConfig Label { get; }

    public ToshTextStyleConfig Help { get; }

    /// <summary>Style applied to half-frame border characters (`│ ╰─`).</summary>
    public ToshTextStyleConfig Frame { get; }

    /// <summary>Style applied to the diagnostic code in the header (`tosh.runtime.unknown_command`).</summary>
    public ToshTextStyleConfig Code { get; }

    public ToshTextStyleConfig ErrorGlyph { get; }

    public ToshTextStyleConfig WarningGlyph { get; }

    public ToshTextStyleConfig InfoGlyph { get; }

    public ToshTextStyleConfig HintGlyph { get; }

    public void Reset()
    {
        Heading.Reset();
        Title.Reset();
        SourceLocation.Reset();
        Underline.Reset();
        Label.Reset();
        Help.Reset();
        Frame.Reset();
        Code.Reset();
        ErrorGlyph.Reset();
        WarningGlyph.Reset();
        InfoGlyph.Reset();
        HintGlyph.Reset();
    }
}

public sealed class ToshTableThemeConfig : IResettableShellConfig
{
    private readonly ToshTableBoxStyle _defaultBoxStyle = ToshTableBoxStyle.Rounded;

    public ToshTableThemeConfig()
    {
        Border = new ToshTextStyleConfig();
        Header = new ToshTextStyleConfig();
        Index = new ToshTextStyleConfig();
        RecordKey = new ToshTextStyleConfig();
        Selection = new ToshTextStyleConfig(bold: true);
        MatrixDepth0 = new ToshTextStyleConfig(foreground: "bright-cyan", bold: true);
        MatrixDepth1 = new ToshTextStyleConfig(foreground: "cyan", bold: true);
        MatrixDepth2 = new ToshTextStyleConfig(foreground: "bright-blue");
        MatrixDepth3 = new ToshTextStyleConfig(foreground: "green");
        MatrixDepth4 = new ToshTextStyleConfig(foreground: "bright-yellow");
        SuccessGlyph = new ToshTextStyleConfig(foreground: "green", bold: true);
        WarningGlyph = new ToshTextStyleConfig(foreground: "yellow", bold: true);
        ErrorGlyph = new ToshTextStyleConfig(foreground: "red", bold: true);
    }

    public ToshTableBoxStyle BoxStyle { get; set; } = ToshTableBoxStyle.Rounded;

    public ToshTextStyleConfig Border { get; }

    public ToshTextStyleConfig Header { get; }

    public ToshTextStyleConfig Index { get; }

    public ToshTextStyleConfig RecordKey { get; }

    public ToshTextStyleConfig Selection { get; }

    public ToshTextStyleConfig MatrixDepth0 { get; }

    public ToshTextStyleConfig MatrixDepth1 { get; }

    public ToshTextStyleConfig MatrixDepth2 { get; }

    public ToshTextStyleConfig MatrixDepth3 { get; }

    public ToshTextStyleConfig MatrixDepth4 { get; }

    public ToshTextStyleConfig SuccessGlyph { get; }

    public ToshTextStyleConfig WarningGlyph { get; }

    public ToshTextStyleConfig ErrorGlyph { get; }

    public void Reset()
    {
        BoxStyle = _defaultBoxStyle;
        Border.Reset();
        Header.Reset();
        Index.Reset();
        RecordKey.Reset();
        Selection.Reset();
        MatrixDepth0.Reset();
        MatrixDepth1.Reset();
        MatrixDepth2.Reset();
        MatrixDepth3.Reset();
        MatrixDepth4.Reset();
        SuccessGlyph.Reset();
        WarningGlyph.Reset();
        ErrorGlyph.Reset();
    }
}

public sealed class ToshTuiThemeConfig : IResettableShellConfig
{
    private readonly ToshTableBoxStyle _defaultBoxStyle = ToshTableBoxStyle.Rounded;
    private readonly ToshTuiTreeStyle _defaultTreeStyle = ToshTuiTreeStyle.Clean;

    public ToshTuiThemeConfig()
    {
        Border = new ToshTextStyleConfig(foreground: "gray", dim: true);
        Title = new ToshTextStyleConfig(foreground: "bright-cyan", bold: true);
        SearchLabel = new ToshTextStyleConfig(foreground: "bright-cyan", bold: true);
        SearchInput = new ToshTextStyleConfig(foreground: "bright-white");
        ListItem = new ToshTextStyleConfig();
        SelectedItem = new ToshTextStyleConfig(bold: true);
        SelectedGutter = new ToshTextStyleConfig(foreground: "bright-cyan", bold: true);
        TreeGuide = new ToshTextStyleConfig(foreground: "gray", dim: true);
        Namespace = new ToshTextStyleConfig(foreground: "bright-cyan");
        Type = new ToshTextStyleConfig(foreground: "green");
        Method = new ToshTextStyleConfig(foreground: "magenta");
        Property = new ToshTextStyleConfig(foreground: "yellow");
        Constructor = new ToshTextStyleConfig(foreground: "bright-green");
        Meta = new ToshTextStyleConfig(foreground: "gray", dim: true);
        DetailText = new ToshTextStyleConfig();
        SectionHeading = new ToshTextStyleConfig(foreground: "yellow", bold: true);
        Example = new ToshTextStyleConfig(foreground: "green");
        Footer = new ToshTextStyleConfig(foreground: "gray", dim: true);
    }

    public ToshTableBoxStyle BoxStyle { get; set; } = ToshTableBoxStyle.Rounded;

    public ToshTuiTreeStyle TreeStyle { get; set; } = ToshTuiTreeStyle.Clean;

    public ToshTextStyleConfig Border { get; }

    public ToshTextStyleConfig Title { get; }

    public ToshTextStyleConfig SearchLabel { get; }

    public ToshTextStyleConfig SearchInput { get; }

    public ToshTextStyleConfig ListItem { get; }

    public ToshTextStyleConfig SelectedItem { get; }

    public ToshTextStyleConfig SelectedGutter { get; }

    public ToshTextStyleConfig TreeGuide { get; }

    public ToshTextStyleConfig Namespace { get; }

    public ToshTextStyleConfig Type { get; }

    public ToshTextStyleConfig Method { get; }

    public ToshTextStyleConfig Property { get; }

    public ToshTextStyleConfig Constructor { get; }

    public ToshTextStyleConfig Meta { get; }

    public ToshTextStyleConfig DetailText { get; }

    public ToshTextStyleConfig SectionHeading { get; }

    public ToshTextStyleConfig Example { get; }

    public ToshTextStyleConfig Footer { get; }

    public void Reset()
    {
        BoxStyle = _defaultBoxStyle;
        TreeStyle = _defaultTreeStyle;
        Border.Reset();
        Title.Reset();
        SearchLabel.Reset();
        SearchInput.Reset();
        ListItem.Reset();
        SelectedItem.Reset();
        SelectedGutter.Reset();
        TreeGuide.Reset();
        Namespace.Reset();
        Type.Reset();
        Method.Reset();
        Property.Reset();
        Constructor.Reset();
        Meta.Reset();
        DetailText.Reset();
        SectionHeading.Reset();
        Example.Reset();
        Footer.Reset();
    }
}

public sealed class ToshDisplayConfig : IResettableShellConfig
{
    private readonly DisplayEngine _display;

    public ToshDisplayConfig(DisplayEngine display, DisplayPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(display);
        ArgumentNullException.ThrowIfNull(preferences);

        _display = display;
        DateTime = new ToshTemporalDisplayConfig(preferences.DateTime);
        DateTimeOffset = new ToshTemporalDisplayConfig(preferences.DateTimeOffset);
        DateOnly = new ToshDateOnlyDisplayConfig(preferences.DateOnly);
        TimeOnly = new ToshTimeOnlyDisplayConfig(preferences.TimeOnly);
        TimeSpan = new ToshDurationDisplayConfig(preferences.TimeSpan);
        StorageSize = new ToshStorageSizeDisplayConfig(preferences.StorageSize);
        Permissions = new ToshUnixFileModeDisplayConfig(preferences.UnixFileMode);
        FileAttributes = new ToshFileAttributesDisplayConfig(preferences.FileAttributes);
        Paging = new ToshPagingConfig();
        Profiles = new ToshDisplayProfilesConfig(preferences.Profiles);
    }

    public ObjectRenderStyle Style
    {
        get => _display.Style;
        set => _display.Style = value;
    }

    public ToshTemporalDisplayConfig DateTime { get; }

    public ToshTemporalDisplayConfig DateTimeOffset { get; }

    public ToshDateOnlyDisplayConfig DateOnly { get; }

    public ToshTimeOnlyDisplayConfig TimeOnly { get; }

    public ToshDurationDisplayConfig TimeSpan { get; }

    public ToshStorageSizeDisplayConfig StorageSize { get; }

    public ToshUnixFileModeDisplayConfig Permissions { get; }

    public ToshFileAttributesDisplayConfig FileAttributes { get; }

    public ToshPagingConfig Paging { get; }

    public ToshDisplayProfilesConfig Profiles { get; }

    public void Reset()
    {
        Style = ObjectRenderStyle.Compact;
        DateTime.Reset();
        DateTimeOffset.Reset();
        DateOnly.Reset();
        TimeOnly.Reset();
        TimeSpan.Reset();
        StorageSize.Reset();
        Permissions.Reset();
        FileAttributes.Reset();
        Paging.Reset();
        Profiles.Reset();
    }
}

public sealed class ToshDisplayProfilesConfig : IResettableShellConfig
{
    private readonly DisplayProfilePreferences _preferences;

    public ToshDisplayProfilesConfig(DisplayProfilePreferences preferences)
    {
        _preferences = preferences;
    }

    public IReadOnlyList<ToshDisplayTypeProfileConfig> Types =>
        _preferences.TypeProfiles
            .OrderBy(profile => profile.TypeName, StringComparer.OrdinalIgnoreCase)
            .Select(profile => new ToshDisplayTypeProfileConfig(profile.TypeName, profile.TableColumns.ToArray()))
            .ToArray();

    public void Reset()
    {
        _preferences.Reset();
    }
}

public sealed record ToshDisplayTypeProfileConfig(string Name, IReadOnlyList<string> TableColumns);

public sealed class ToshTemporalDisplayConfig : IResettableShellConfig
{
    private readonly TemporalDisplayPreferences _preferences;

    public ToshTemporalDisplayConfig(TemporalDisplayPreferences preferences)
    {
        _preferences = preferences;
    }

    public TemporalDisplayMode ScalarMode
    {
        get => _preferences.ScalarMode;
        set => _preferences.ScalarMode = value;
    }

    public TemporalDisplayMode TableMode
    {
        get => _preferences.TableMode;
        set => _preferences.TableMode = value;
    }

    public string? ScalarFormat
    {
        get => _preferences.ScalarFormat;
        set => _preferences.ScalarFormat = value;
    }

    public string? TableFormat
    {
        get => _preferences.TableFormat;
        set => _preferences.TableFormat = value;
    }

    public void Reset()
    {
        _preferences.Reset();
    }
}

public sealed class ToshDateOnlyDisplayConfig : IResettableShellConfig
{
    private readonly DateOnlyDisplayPreferences _preferences;

    public ToshDateOnlyDisplayConfig(DateOnlyDisplayPreferences preferences)
    {
        _preferences = preferences;
    }

    public DateOnlyDisplayMode ScalarMode
    {
        get => _preferences.ScalarMode;
        set => _preferences.ScalarMode = value;
    }

    public DateOnlyDisplayMode TableMode
    {
        get => _preferences.TableMode;
        set => _preferences.TableMode = value;
    }

    public string? ScalarFormat
    {
        get => _preferences.ScalarFormat;
        set => _preferences.ScalarFormat = value;
    }

    public string? TableFormat
    {
        get => _preferences.TableFormat;
        set => _preferences.TableFormat = value;
    }

    public void Reset()
    {
        _preferences.Reset();
    }
}

public sealed class ToshTimeOnlyDisplayConfig : IResettableShellConfig
{
    private readonly TimeOnlyDisplayPreferences _preferences;

    public ToshTimeOnlyDisplayConfig(TimeOnlyDisplayPreferences preferences)
    {
        _preferences = preferences;
    }

    public TimeOnlyDisplayMode ScalarMode
    {
        get => _preferences.ScalarMode;
        set => _preferences.ScalarMode = value;
    }

    public TimeOnlyDisplayMode TableMode
    {
        get => _preferences.TableMode;
        set => _preferences.TableMode = value;
    }

    public string? ScalarFormat
    {
        get => _preferences.ScalarFormat;
        set => _preferences.ScalarFormat = value;
    }

    public string? TableFormat
    {
        get => _preferences.TableFormat;
        set => _preferences.TableFormat = value;
    }

    public void Reset()
    {
        _preferences.Reset();
    }
}

public sealed class ToshStorageSizeDisplayConfig : IResettableShellConfig
{
    private readonly StorageSizeDisplayPreferences _preferences;

    public ToshStorageSizeDisplayConfig(StorageSizeDisplayPreferences preferences)
    {
        _preferences = preferences;
    }

    public StorageSizeDisplayMode Mode
    {
        get => _preferences.Mode;
        set => _preferences.Mode = value;
    }

    public void Reset()
    {
        _preferences.Reset();
    }
}

public sealed class ToshDurationDisplayConfig : IResettableShellConfig
{
    private readonly DurationDisplayPreferences _preferences;

    public ToshDurationDisplayConfig(DurationDisplayPreferences preferences)
    {
        _preferences = preferences;
    }

    public DurationDisplayMode ScalarMode
    {
        get => _preferences.ScalarMode;
        set => _preferences.ScalarMode = value;
    }

    public DurationDisplayMode TableMode
    {
        get => _preferences.TableMode;
        set => _preferences.TableMode = value;
    }

    public string? ScalarFormat
    {
        get => _preferences.ScalarFormat;
        set => _preferences.ScalarFormat = value;
    }

    public string? TableFormat
    {
        get => _preferences.TableFormat;
        set => _preferences.TableFormat = value;
    }

    public void Reset()
    {
        _preferences.Reset();
    }
}

public sealed class ToshUnixFileModeDisplayConfig : IResettableShellConfig
{
    private readonly UnixFileModeDisplayPreferences _preferences;

    public ToshUnixFileModeDisplayConfig(UnixFileModeDisplayPreferences preferences)
    {
        _preferences = preferences;
    }

    public UnixFileModeDisplayMode Mode
    {
        get => _preferences.Mode;
        set => _preferences.Mode = value;
    }

    public void Reset()
    {
        _preferences.Reset();
    }
}

public sealed class ToshFileAttributesDisplayConfig : IResettableShellConfig
{
    private readonly FileAttributesDisplayPreferences _preferences;

    public ToshFileAttributesDisplayConfig(FileAttributesDisplayPreferences preferences)
    {
        _preferences = preferences;
    }

    public FileAttributesDisplayMode Mode
    {
        get => _preferences.Mode;
        set => _preferences.Mode = value;
    }

    public void Reset()
    {
        _preferences.Reset();
    }
}

public sealed class ToshPagingConfig : IResettableShellConfig
{
    private const int DefaultReservedLines = 1;
    private int _reservedLines = DefaultReservedLines;

    public bool Enabled { get; set; } = true;

    public int ReservedLines
    {
        get => _reservedLines;
        set => _reservedLines = Math.Max(0, value);
    }

    public void Reset()
    {
        Enabled = true;
        ReservedLines = DefaultReservedLines;
    }
}

public sealed class ToshReplConfig : IResettableShellConfig
{
    private const int DefaultCompletionMaxVisible = 8;

    private int _completionMaxVisible = DefaultCompletionMaxVisible;
    private string _continuationPrompt = "....> ";

    public string ContinuationPrompt
    {
        get => _continuationPrompt;
        set => _continuationPrompt = string.IsNullOrEmpty(value) ? "....> " : value;
    }

    public bool SyntaxHighlightingEnabled { get; set; } = true;

    public bool GhostTextEnabled { get; set; } = true;

    public int CompletionMaxVisible
    {
        get => _completionMaxVisible;
        set => _completionMaxVisible = Math.Max(1, value);
    }

    // Default behavior: Enter executes unless continuation is active/required;
    // Shift+Enter (or Ctrl+J fallback) executes explicitly.
    public bool ShiftEnterExecutes { get; set; } = true;

    // Draw a visual right-edge separator at the gutter boundary for multiline input.
    public bool ContinuationGutterRightBorder { get; set; } = true;

    // Optionally stamp continuation line numbers into the gutter.
    public bool ContinuationLineNumbers { get; set; } = true;

    public void Reset()
    {
        ContinuationPrompt = "....> ";
        SyntaxHighlightingEnabled = true;
        GhostTextEnabled = true;
        CompletionMaxVisible = DefaultCompletionMaxVisible;
        ShiftEnterExecutes = true;
        ContinuationGutterRightBorder = true;
        ContinuationLineNumbers = true;
    }
}

public sealed class ToshPromptConfig : IResettableShellConfig
{
    private string _nameText = "tosh";
    private string _indicatorText = " \u276f ";
    private string _timeFormat = "HH:mm";
    private string _headerLeftLayout = "Directory, Git";
    private string _headerRightLayout = "UserHost, Jobs, Duration";
    private string _promptLeftLayout = "ExitCode, Name, Indicator";
    private int _durationThresholdMilliseconds = 500;
    private readonly ToshPromptThemeConfig _theme;

    public ToshPromptConfig(ToshPromptThemeConfig theme)
    {
        _theme = theme;
    }

    public bool TimeEnabled { get; set; }

    public string HeaderLeftLayout
    {
        get => _headerLeftLayout;
        set => _headerLeftLayout = NormalizeLayout(value, "Directory, Git");
    }

    public string HeaderRightLayout
    {
        get => _headerRightLayout;
        set => _headerRightLayout = NormalizeLayout(value, "UserHost, Jobs, Duration");
    }

    public string PromptLeftLayout
    {
        get => _promptLeftLayout;
        set => _promptLeftLayout = NormalizeLayout(value, "ExitCode, Name, Indicator");
    }

    public string TimeFormat
    {
        get => _timeFormat;
        set => _timeFormat = string.IsNullOrWhiteSpace(value) ? "HH:mm" : value;
    }

    public string? TimeColor
    {
        get => _theme.Time.Foreground;
        set => _theme.Time.Foreground = value;
    }

    public bool TimeBold
    {
        get => _theme.Time.Bold;
        set => _theme.Time.Bold = value;
    }

    public string? DirectoryColor
    {
        get => _theme.Directory.Foreground;
        set => _theme.Directory.Foreground = value;
    }

    public bool DirectoryBold
    {
        get => _theme.Directory.Bold;
        set => _theme.Directory.Bold = value;
    }

    public int? DirectoryDepth { get; set; }

    public bool GitEnabled { get; set; } = true;

    public string? GitForeground
    {
        get => _theme.Git.Foreground;
        set => _theme.Git.Foreground = value;
    }

    public bool GitBold
    {
        get => _theme.Git.Bold;
        set => _theme.Git.Bold = value;
    }

    public bool UserHostEnabled { get; set; } = true;

    public string? UserHostColor
    {
        get => _theme.UserHost.Foreground;
        set => _theme.UserHost.Foreground = value;
    }

    public bool UserHostBold
    {
        get => _theme.UserHost.Bold;
        set => _theme.UserHost.Bold = value;
    }

    public bool HistoryIdEnabled { get; set; } = true;

    public string? HistoryIdColor
    {
        get => _theme.HistoryId.Foreground;
        set => _theme.HistoryId.Foreground = value;
    }

    public bool HistoryIdBold
    {
        get => _theme.HistoryId.Bold;
        set => _theme.HistoryId.Bold = value;
    }

    public bool JobsEnabled { get; set; } = true;

    public string? JobsColor
    {
        get => _theme.Jobs.Foreground;
        set => _theme.Jobs.Foreground = value;
    }

    public bool JobsBold
    {
        get => _theme.Jobs.Bold;
        set => _theme.Jobs.Bold = value;
    }

    public bool DurationEnabled { get; set; } = true;

    public int DurationThresholdMilliseconds
    {
        get => _durationThresholdMilliseconds;
        set => _durationThresholdMilliseconds = Math.Max(0, value);
    }

    public string? DurationColor
    {
        get => _theme.Duration.Foreground;
        set => _theme.Duration.Foreground = value;
    }

    public bool DurationBold
    {
        get => _theme.Duration.Bold;
        set => _theme.Duration.Bold = value;
    }

    public bool ExitCodeEnabled { get; set; } = true;

    public string? ExitCodeColor
    {
        get => _theme.ExitCode.Foreground;
        set => _theme.ExitCode.Foreground = value;
    }

    public bool ExitCodeBold
    {
        get => _theme.ExitCode.Bold;
        set => _theme.ExitCode.Bold = value;
    }

    public string NameText
    {
        get => _nameText;
        set => _nameText = string.IsNullOrEmpty(value) ? "tosh" : value;
    }

    public string? NameColor
    {
        get => _theme.Name.Foreground;
        set => _theme.Name.Foreground = value;
    }

    public bool NameBold
    {
        get => _theme.Name.Bold;
        set => _theme.Name.Bold = value;
    }

    public string IndicatorText
    {
        get => _indicatorText;
        set => _indicatorText = string.IsNullOrEmpty(value) ? " \u276f " : value;
    }

    public string? IndicatorColor
    {
        get => _theme.Indicator.Foreground;
        set => _theme.Indicator.Foreground = value;
    }

    public void Reset()
    {
        HeaderLeftLayout = "Directory, Git";
        HeaderRightLayout = "UserHost, Jobs, Duration";
        PromptLeftLayout = "ExitCode, Name, Indicator";
        TimeEnabled = false;
        TimeFormat = "HH:mm";
        DirectoryDepth = null;
        GitEnabled = true;
        UserHostEnabled = true;
        HistoryIdEnabled = true;
        JobsEnabled = true;
        DurationEnabled = true;
        DurationThresholdMilliseconds = 500;
        ExitCodeEnabled = true;
        NameText = "tosh";
        IndicatorText = " \u276f ";
        _theme.Reset();
    }

    private static string NormalizeLayout(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var segments = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 0 ? fallback : string.Join(", ", segments);
    }
}

public sealed class ToshHistoryConfig : IResettableShellConfig
{
    private readonly string _defaultRootDirectory;
    private readonly string _defaultFilePath;
    private string _filePath;
    private int? _maxEntries;

    public ToshHistoryConfig(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        _defaultRootDirectory = Path.GetFullPath(rootDirectory);
        _defaultFilePath = Path.Combine(_defaultRootDirectory, "history.jsonl");
        _filePath = _defaultFilePath;
        Reset();
    }

    public bool Persistent { get; set; }

    public string FilePath
    {
        get => _filePath;
        set => _filePath = NormalizeConfiguredPath(value, _defaultFilePath);
    }

    public bool IgnoreLeadingSpace { get; set; }

    public ToshHistoryDeduplicationMode Deduplication { get; set; }

    public int? MaxEntries
    {
        get => _maxEntries;
        set
        {
            if (value is int maxEntries && maxEntries < 0)
            {
                throw new InvalidOperationException("History.MaxEntries must be null or greater than or equal to zero.");
            }

            _maxEntries = value;
        }
    }

    public void Reset()
    {
        Persistent = true;
        FilePath = _defaultFilePath;
        IgnoreLeadingSpace = false;
        Deduplication = ToshHistoryDeduplicationMode.Consecutive;
        MaxEntries = 5000;
    }

    private string NormalizeConfiguredPath(string? configuredPath, string fallback)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return fallback;
        }

        return Path.IsPathRooted(configuredPath)
            ? Path.GetFullPath(configuredPath)
            : Path.GetFullPath(Path.Combine(_defaultRootDirectory, configuredPath));
    }
}

public enum ToshHistoryDeduplicationMode
{
    None,
    Consecutive,
    All,
}

public sealed class ToshStartupConfig : IResettableShellConfig
{
    private readonly string _defaultRootDirectory;
    private string _rootDirectory;
    private string _configFilePath;
    private string _profilePath;
    private string _autoloadDirectory;

    public ToshStartupConfig(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        _defaultRootDirectory = Path.GetFullPath(rootDirectory);
        _rootDirectory = _defaultRootDirectory;
        _configFilePath = Path.Combine(_rootDirectory, "config.tosh");
        _profilePath = Path.Combine(_rootDirectory, "profile.tosh");
        _autoloadDirectory = Path.Combine(_rootDirectory, "autoload");
    }

    public string RootDirectory
    {
        get => _rootDirectory;
        set => ApplyRootDirectory(value);
    }

    public string ConfigFilePath
    {
        get => _configFilePath;
        set => _configFilePath = NormalizeConfiguredPath(value, Path.Combine(_rootDirectory, "config.tosh"));
    }

    public string ProfilePath
    {
        get => _profilePath;
        set => _profilePath = NormalizeConfiguredPath(value, Path.Combine(_rootDirectory, "profile.tosh"));
    }

    public string AutoloadDirectory
    {
        get => _autoloadDirectory;
        set => _autoloadDirectory = NormalizeConfiguredPath(value, Path.Combine(_rootDirectory, "autoload"));
    }

    public void ApplyRootDirectory(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        _rootDirectory = Path.GetFullPath(rootDirectory);
        _configFilePath = Path.Combine(_rootDirectory, "config.tosh");
        _profilePath = Path.Combine(_rootDirectory, "profile.tosh");
        _autoloadDirectory = Path.Combine(_rootDirectory, "autoload");
    }

    public string ResolvePath(string configuredPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configuredPath);
        return Path.IsPathRooted(configuredPath)
            ? Path.GetFullPath(configuredPath)
            : Path.GetFullPath(Path.Combine(_rootDirectory, configuredPath));
    }

    public void Reset()
    {
        ApplyRootDirectory(_defaultRootDirectory);
    }

    private string NormalizeConfiguredPath(string? configuredPath, string fallback)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return fallback;
        }

        return configuredPath;
    }
}

public sealed class ToshTtyConfig : IResettableShellConfig
{
    private ToshTableBoxStyle _boxStyle = ToshTableBoxStyle.Square;
    private string _indicator = " > ";
    private string _errorMarker = "x";

    public bool Enabled { get; set; } = true;

    public ToshTableBoxStyle BoxStyle
    {
        get => _boxStyle;
        set => _boxStyle = value is ToshTableBoxStyle.Rounded ? ToshTableBoxStyle.Square : value;
    }

    public string Indicator
    {
        get => _indicator;
        set => _indicator = string.IsNullOrEmpty(value) ? " > " : value;
    }

    public string ErrorMarker
    {
        get => _errorMarker;
        set => _errorMarker = string.IsNullOrEmpty(value) ? "x" : value;
    }

    public ToshTtyGlyphConfig Glyphs { get; } = new();

    public void Reset()
    {
        Enabled = true;
        _boxStyle = ToshTableBoxStyle.Square;
        _indicator = " > ";
        _errorMarker = "x";
        Glyphs.Reset();
    }
}

public sealed class ToshTtyGlyphConfig : IResettableShellConfig, IShellRecordObject
{
    private readonly Dictionary<string, string> _glyphs = new(StringComparer.Ordinal);

    public string ShellTypeName => "TtyGlyphs";

    public ToshTtyGlyphConfig()
    {
        SetDefaults();
    }

    public string? Resolve(string glyph)
    {
        return _glyphs.TryGetValue(glyph, out var fallback) ? fallback : null;
    }

    public bool TryGetMember(string name, out object? value, bool includeHidden = false)
    {
        if (_glyphs.TryGetValue(name, out var fallback))
        {
            value = fallback;
            return true;
        }

        value = null;
        return false;
    }

    public bool TrySetMember(string name, object? value)
    {
        if (value is null)
        {
            _glyphs.Remove(name);
            return true;
        }

        _glyphs[name] = value.ToString() ?? string.Empty;
        return true;
    }

    public IReadOnlyList<KeyValuePair<string, object?>> GetMembers(bool includeHidden = false)
    {
        return _glyphs
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => new KeyValuePair<string, object?>(entry.Key, entry.Value))
            .ToArray();
    }

    public void Reset()
    {
        _glyphs.Clear();
        SetDefaults();
    }

    private void SetDefaults()
    {
        _glyphs["\u2718"] = "x";      // ✘ → x
        _glyphs["\u276f"] = ">";      // ❯ → >
        _glyphs["\ue0a0"] = "";       //  → (empty, Nerd Font)
        _glyphs["\u00d7"] = "x";      // × → x
    }
}

public sealed class ToshUsingsConfig : IResettableShellConfig, IShellRecordObject
{
    private DotNetTypeResolver? _resolver;

    public string ShellTypeName => "Usings";

    internal void Bind(DotNetTypeResolver resolver)
    {
        _resolver = resolver;
    }

    public bool TryGetMember(string name, out object? value, bool includeHidden = false)
    {
        value = null;
        return false;
    }

    public bool TrySetMember(string name, object? value)
    {
        if (value is null or false)
        {
            _resolver?.RemoveUsing(name);
            return true;
        }

        _resolver?.AddUsing(name);
        return true;
    }

    public IReadOnlyList<KeyValuePair<string, object?>> GetMembers(bool includeHidden = false)
    {
        if (_resolver is null) return [];

        return _resolver.GetImports()
            .OrderBy(ns => ns, StringComparer.OrdinalIgnoreCase)
            .Select(ns => new KeyValuePair<string, object?>(ns, true))
            .ToArray();
    }

    public void Add(string namespacePath)
    {
        _resolver?.AddUsing(namespacePath);
    }

    public bool Remove(string namespacePath)
    {
        return _resolver?.RemoveUsing(namespacePath) ?? false;
    }

    public void Reset()
    {
        // Reset to defaults by clearing and re-adding
        if (_resolver is null) return;

        foreach (var ns in _resolver.GetImports().ToArray())
        {
            _resolver.RemoveUsing(ns);
        }

        foreach (var ns in DotNetTypeResolver.GetDefaultImplicitUsings())
        {
            _resolver.AddUsing(ns);
        }
    }
}
