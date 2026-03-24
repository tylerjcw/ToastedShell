namespace Tosh.Core;

public sealed class DisplayPreferences
{
    public DisplayPreferences()
    {
        DateTime = new TemporalDisplayPreferences(
            ScalarMode: TemporalDisplayMode.Iso,
            TableMode: TemporalDisplayMode.Relative);
        DateTimeOffset = new TemporalDisplayPreferences(
            ScalarMode: TemporalDisplayMode.Iso,
            TableMode: TemporalDisplayMode.Relative);
        StorageSize = new StorageSizeDisplayPreferences();
    }

    public TemporalDisplayPreferences DateTime { get; }

    public TemporalDisplayPreferences DateTimeOffset { get; }

    public StorageSizeDisplayPreferences StorageSize { get; }

    public Func<System.DateTimeOffset> NowProvider { get; set; } = static () => System.DateTimeOffset.Now;
}

public sealed class TemporalDisplayPreferences(
    TemporalDisplayMode ScalarMode,
    TemporalDisplayMode TableMode,
    string? ScalarFormat = null,
    string? TableFormat = null)
{
    public TemporalDisplayMode ScalarMode { get; set; } = ScalarMode;

    public TemporalDisplayMode TableMode { get; set; } = TableMode;

    public string? ScalarFormat { get; set; } = ScalarFormat;

    public string? TableFormat { get; set; } = TableFormat;

    public void Reset()
    {
        ScalarMode = TemporalDisplayMode.Iso;
        TableMode = TemporalDisplayMode.Relative;
        ScalarFormat = null;
        TableFormat = null;
    }
}

public enum TemporalDisplayMode
{
    Iso,
    Local,
    Relative,
    Unix,
    Custom,
}

public sealed class StorageSizeDisplayPreferences
{
    public StorageSizeDisplayMode Mode { get; set; } = StorageSizeDisplayMode.Human;

    public void Reset()
    {
        Mode = StorageSizeDisplayMode.Human;
    }
}

public enum StorageSizeDisplayMode
{
    Human,
    Bytes,
}
