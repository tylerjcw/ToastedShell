namespace Tosh.Cli;

/// <summary>
/// Recognises the request objects the <c>tui</c> command yields for the shell to run.
/// </summary>
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
        "TuiTreeRunRequest",
    };

    public static bool IsTuiRequestBatch(IReadOnlyList<object?> values)
        => TryGetRequest(values, out _);

    /// <summary>Finds the single request in a batch, if that is all the batch holds.</summary>
    /// <remarks>
    /// Two requests in one batch are left alone rather than guessed between, and so is a
    /// batch carrying anything else: those are someone's actual results.
    /// </remarks>
    public static bool TryGetRequest(IReadOnlyList<object?> values, out object? request)
    {
        ArgumentNullException.ThrowIfNull(values);

        request = null;

        foreach (var value in values)
        {
            if (value is null || !IsRequest(value) || request is not null)
            {
                return false;
            }

            request = value;
        }

        return request is not null;
    }

    private static bool IsRequest(object value)
    {
        var type = value.GetType();

        return string.Equals(type.Namespace, RequestNamespace, StringComparison.Ordinal) &&
            RequestTypeNames.Contains(type.Name);
    }
}
