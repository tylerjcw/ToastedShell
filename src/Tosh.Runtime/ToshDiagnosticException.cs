namespace Tosh.Runtime;

public sealed class ToshDiagnosticException : Exception
{
    public ToshDiagnosticException(IReadOnlyList<ToshDiagnostic> diagnostics)
        : this(diagnostics, innerException: null)
    {
    }

    /// <summary>
    /// Wraps a diagnostic while keeping the failure that caused it reachable.
    /// </summary>
    /// <remarks>
    /// `TS-P2-95`. A diagnostic built from a CLR exception used to keep only the
    /// message, so a script could read what went wrong but never inspect it — no
    /// type to match on, no `FileName` on a missing-file error, no probing paths on
    /// a missing native library. Keeping the original as
    /// <see cref="Exception.InnerException"/> costs nothing and is what a caller
    /// reaches for.
    /// </remarks>
    public ToshDiagnosticException(IReadOnlyList<ToshDiagnostic> diagnostics, Exception? innerException)
        : base(diagnostics.FirstOrDefault()?.Title ?? "A Tosh diagnostic was reported.", innerException)
    {
        Diagnostics = diagnostics;
    }

    public IReadOnlyList<ToshDiagnostic> Diagnostics { get; }

    public static ToshDiagnosticException Create(ToshDiagnostic diagnostic)
    {
        return new ToshDiagnosticException([diagnostic]);
    }

    public static ToshDiagnosticException Create(ToshDiagnostic diagnostic, Exception? innerException)
    {
        return new ToshDiagnosticException([diagnostic], innerException);
    }
}
