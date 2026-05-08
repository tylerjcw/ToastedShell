namespace Tosh.Cli;

internal static class TuiRequestProbe
{
    private const string RequestNamespace = "Tosh.Tui.Requests";

    private static readonly HashSet<string> RequestTypeNames = new(StringComparer.Ordinal)
    {
        "ConfigBrowseRequest",
        "HelpBrowseRequest",
        "TuiConfirmRequest",
        "TuiFilePickRequest",
        "TuiInputRequest",
        "TuiPickRequest",
        "TuiRunRequest",
    };

    public static bool IsTuiRequestBatch(IReadOnlyList<object?> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        if (values.Count != 1 || values[0] is null)
        {
            return false;
        }

        var type = values[0]!.GetType();
        return string.Equals(type.Namespace, RequestNamespace, StringComparison.Ordinal) &&
            RequestTypeNames.Contains(type.Name);
    }
}
