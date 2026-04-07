namespace Tosh.Core;

/// <summary>
/// Allows interactive tools to queue or insert text into the command line.
/// In the CLI REPL this is inserted into the active line at the current cursor
/// when one is being edited, and otherwise consumed by the next prompt.
/// </summary>
public interface ICommandLineInsertionSink
{
    bool TryInsertText(string text);
}
