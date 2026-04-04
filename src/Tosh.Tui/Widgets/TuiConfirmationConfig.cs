namespace Tosh.Tui.Widgets;

/// <summary>Configuration for a yes/no confirmation widget.</summary>
public sealed class TuiConfirmationConfig : ITuiWidget
{
    public TuiConfirmationConfig(string id, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        Id = id;
        Message = message;
    }

    public string Id { get; }

    public TuiWidgetKind Kind => TuiWidgetKind.Confirmation;

    /// <summary>Message displayed to the user.</summary>
    public string Message { get; }

    /// <summary>Label for the confirm button.</summary>
    public string ConfirmLabel { get; set; } = "Yes";

    /// <summary>Label for the cancel button.</summary>
    public string CancelLabel { get; set; } = "No";

    /// <summary>Which button is selected by default.</summary>
    public bool DefaultConfirm { get; set; } = true;
}
