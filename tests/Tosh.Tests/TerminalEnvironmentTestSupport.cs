using Tosh.Runtime;

namespace Tosh.Tests;

internal static class TerminalEnvironmentTestSupport
{
    public static InlineTablePlan.BoxChars RoundedTableBox =>
        InlineTablePlan.GetBoxCharacters(ToshTableBoxStyle.Rounded);

    public static char RoundedTableTopLeft => RoundedTableBox.TopLeft;

    public static char RoundedTableBottomLeft => RoundedTableBox.BottomLeft;

    public static string ExitCodeText(int exitCode) =>
        $"{TerminalGlyphs.ExitCodePrefix} {exitCode}";

    public static bool DiagnosticsPlainModeIsActive
    {
        get
        {
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR")))
            {
                return true;
            }

            var plainEnv = Environment.GetEnvironmentVariable("TOSH_DIAG_PLAIN");
            return !string.IsNullOrEmpty(plainEnv) && plainEnv != "0";
        }
    }
}
