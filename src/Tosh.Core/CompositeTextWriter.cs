using System.Text;

namespace Tosh.Core;

public sealed class CompositeTextWriter : TextWriter
{
    private readonly IReadOnlyList<TextWriter> _writers;

    public CompositeTextWriter(IEnumerable<TextWriter> writers)
    {
        _writers = writers.ToArray();
    }

    public override Encoding Encoding => _writers.Count > 0 ? _writers[0].Encoding : Encoding.UTF8;

    public override void Flush()
    {
        foreach (var writer in _writers)
        {
            writer.Flush();
        }
    }

    public override async Task FlushAsync()
    {
        foreach (var writer in _writers)
        {
            await writer.FlushAsync();
        }
    }

    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        foreach (var writer in _writers)
        {
            await writer.FlushAsync(cancellationToken);
        }
    }

    public override void Write(char value)
    {
        foreach (var writer in _writers)
        {
            writer.Write(value);
        }
    }

    public override void Write(string? value)
    {
        foreach (var writer in _writers)
        {
            writer.Write(value);
        }
    }

    public override void WriteLine(string? value)
    {
        foreach (var writer in _writers)
        {
            writer.WriteLine(value);
        }
    }

    public override async Task WriteAsync(char value)
    {
        foreach (var writer in _writers)
        {
            await writer.WriteAsync(value);
        }
    }

    public override async Task WriteAsync(string? value)
    {
        foreach (var writer in _writers)
        {
            await writer.WriteAsync(value);
        }
    }

    public override async Task WriteLineAsync(string? value)
    {
        foreach (var writer in _writers)
        {
            await writer.WriteLineAsync(value);
        }
    }
}
