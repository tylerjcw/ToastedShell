namespace Tosh.Tui.Widgets;

/// <summary>Configuration for a file/directory picker widget.</summary>
public sealed class TuiFilePickerConfig : ITuiWidget
{
    public TuiFilePickerConfig(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        Id = id;
    }

    public string Id { get; }

    public TuiWidgetKind Kind => TuiWidgetKind.FilePicker;

    /// <summary>Starting directory path. Defaults to the current working directory.</summary>
    public string? InitialPath { get; set; }

    /// <summary>Glob filter for visible files (e.g. "*.json"). Null shows all files.</summary>
    public string? Filter { get; set; }

    /// <summary>Restrict selection to directories only.</summary>
    public bool DirectoryOnly { get; set; }
}
