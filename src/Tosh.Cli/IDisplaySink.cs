namespace Tosh.Cli;

/// <summary>
/// Receives pipeline output values one at a time and decides how to render them.
/// Callers iterate the engine's IAsyncEnumerable and call EmitAsync per value;
/// DisposeAsync performs the final flush (bottom border, buffered render, etc.).
/// </summary>
internal interface IDisplaySink : IAsyncDisposable
{
    ValueTask EmitAsync(object? value, CancellationToken cancellationToken = default);
}
