using Tosh.Tui;

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
        "TuiRunRequest",
        "TuiTreeRunRequest",
    };

    public static bool IsTuiRequestBatch(IReadOnlyList<object?> values)
        => TryGetRequest(values, out _);

    /// <summary>
    /// Finds the single request in a batch, allowing for the screens a builder leaves
    /// behind.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each screen-builder subcommand re-yields the screen so it can be piped onwards.
    /// Written as one pipeline that is invisible, but written a line at a time — which is
    /// how anyone builds a screen with more than two widgets — every line is a statement
    /// whose value reaches the display:
    /// </para>
    /// <code>
    /// var screen = tui screen --title "Reactor Block Viewer"
    /// $screen | tui add-list  --id ReactorType $reactorTypes
    /// $screen | tui add-input --id Width --prompt "Width?"
    /// $screen | tui run
    /// </code>
    /// <para>
    /// That batch is three screens and a request, so a rule of "exactly one request"
    /// declined it and the shell printed the objects instead: a table of identical
    /// screens, then the request, and no TUI. The only way to avoid it was to know to
    /// write <c>| ignore</c> after every builder line, which nothing says.
    /// </para>
    /// <para>
    /// A <see cref="TuiScreen"/> rendered as a table is never output anyone wanted, so a
    /// batch of screens around one request is taken as the request. Anything else in the
    /// batch means the values are someone's actual results and are left alone.
    /// </para>
    /// </remarks>
    public static bool TryGetRequest(IReadOnlyList<object?> values, out object? request)
    {
        ArgumentNullException.ThrowIfNull(values);

        request = null;

        foreach (var value in values)
        {
            if (value is null)
            {
                return false;
            }

            if (IsRequest(value))
            {
                // Two requests in one batch is not a builder pipeline; leave it alone
                // rather than guessing which one to run.
                if (request is not null)
                {
                    return false;
                }

                request = value;
                continue;
            }

            if (value is TuiScreen)
            {
                continue;
            }

            return false;
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
