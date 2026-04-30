namespace Tosh.Runtime;

public static class ToshConfigDefaults
{
    public static string GetDefaultConfigDirectory()
    {
        var explicitConfigHome = Environment.GetEnvironmentVariable("TOSH_CONFIG_HOME");

        if (!string.IsNullOrWhiteSpace(explicitConfigHome))
        {
            return Path.GetFullPath(explicitConfigHome);
        }

        var xdgConfigHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");

        if (!string.IsNullOrWhiteSpace(xdgConfigHome))
        {
            return Path.Combine(Path.GetFullPath(xdgConfigHome), "tosh");
        }

        if (OperatingSystem.IsWindows())
        {
            return GetWindowsConfigDirectory();
        }

        return Path.Combine(PathUtilities.UserHomeDirectory, ".config", "tosh");
    }

    public static string GetDefaultStateDirectory()
    {
        var explicitStateHome = Environment.GetEnvironmentVariable("TOSH_STATE_HOME");

        if (!string.IsNullOrWhiteSpace(explicitStateHome))
        {
            return Path.GetFullPath(explicitStateHome);
        }

        var xdgStateHome = Environment.GetEnvironmentVariable("XDG_STATE_HOME");

        if (!string.IsNullOrWhiteSpace(xdgStateHome))
        {
            return Path.Combine(Path.GetFullPath(xdgStateHome), "tosh");
        }

        if (OperatingSystem.IsWindows())
        {
            return GetWindowsStateDirectory();
        }

        return Path.Combine(PathUtilities.UserHomeDirectory, ".local", "state", "tosh");
    }

    public static string GetDefaultHistoryFilePath()
    {
        return Path.Combine(GetDefaultStateDirectory(), "history.jsonl");
    }

    private static string GetWindowsConfigDirectory()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        if (!string.IsNullOrWhiteSpace(appData))
        {
            return Path.Combine(appData, "tosh");
        }

        return Path.Combine(PathUtilities.UserHomeDirectory, "AppData", "Roaming", "tosh");
    }

    private static string GetWindowsStateDirectory()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            return Path.Combine(localAppData, "tosh", "state");
        }

        return Path.Combine(PathUtilities.UserHomeDirectory, "AppData", "Local", "tosh", "state");
    }
}
