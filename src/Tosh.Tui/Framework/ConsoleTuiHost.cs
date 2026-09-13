
namespace Tosh.Tui;

public sealed class ConsoleTuiHost : ITuiHost
{
    private readonly TuiInputReader _inputReader = new();

    private static readonly Lazy<Stream> _standardOutput = new(Console.OpenStandardOutput);

    public bool IsInteractive => !Console.IsInputRedirected && !Console.IsOutputRedirected;

    public TuiSize? TryGetSize()
    {
        try
        {
            if (!IsInteractive)
            {
                return null;
            }

            var width = Console.WindowWidth;
            var height = Console.WindowHeight;

            return width > 0 && height > 0
                ? new TuiSize(width, height)
                : null;
        }
        catch
        {
            return null;
        }
    }

    public ConsoleKeyInfo ReadKey(bool intercept = true)
    {
        return Console.ReadKey(intercept);
    }

    public bool TryReadPendingKey(out ConsoleKeyInfo key, bool intercept = true)
    {
        if (!Console.KeyAvailable)
        {
            key = default;
            return false;
        }

        key = Console.ReadKey(intercept);
        return true;
    }

    public TuiInputEvent ReadInput() => _inputReader.Read();

    public bool TryReadPendingInput(out TuiInputEvent inputEvent) => _inputReader.TryReadPending(out inputEvent);

    public void Write(string text)
    {
        Console.Write(text);
    }

    /// <inheritdoc />
    public void WriteUrgent(string text)
    {
        // Straight to the file descriptor: no TextWriter, no console lock, no wait on
        // whichever thread is blocked reading a key.
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        _standardOutput.Value.Write(bytes, 0, bytes.Length);
        _standardOutput.Value.Flush();
    }
}
