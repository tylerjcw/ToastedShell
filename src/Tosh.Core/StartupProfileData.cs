namespace Tosh.Core;

public sealed class StartupProfileData : IShellRecordObject
{
    public string ShellTypeName => "StartupProfile";

    public TimeSpan Total { get; set; }
    public TimeSpan Config { get; set; }
    public TimeSpan Profile { get; set; }
    public TimeSpan Autoload { get; set; }
    public TimeSpan History { get; set; }
    public IReadOnlyList<StartupFileProfile> Files { get; set; } = [];

    public bool TryGetMember(string name, out object? value, bool includeHidden = false)
    {
        switch (name)
        {
            case "Total": value = Total; return true;
            case "Config": value = Config; return true;
            case "Profile": value = Profile; return true;
            case "Autoload": value = Autoload; return true;
            case "History": value = History; return true;
            case "Files": value = Files; return true;
            default: value = null; return false;
        }
    }

    public bool TrySetMember(string name, object? value) => false;

    public IReadOnlyList<KeyValuePair<string, object?>> GetMembers(bool includeHidden = false) =>
    [
        new("Total", Total),
        new("Config", Config),
        new("Profile", Profile),
        new("Autoload", Autoload),
        new("History", History),
        new("Files", Files),
    ];
}

public sealed class StartupFileProfile : IShellRecordObject
{
    public string ShellTypeName => "StartupFileProfile";

    public required string Path { get; init; }
    public required TimeSpan Duration { get; init; }

    public bool TryGetMember(string name, out object? value, bool includeHidden = false)
    {
        switch (name)
        {
            case "Path": value = Path; return true;
            case "Duration": value = Duration; return true;
            default: value = null; return false;
        }
    }

    public bool TrySetMember(string name, object? value) => false;

    public IReadOnlyList<KeyValuePair<string, object?>> GetMembers(bool includeHidden = false) =>
    [
        new("Path", Path),
        new("Duration", Duration),
    ];
}
