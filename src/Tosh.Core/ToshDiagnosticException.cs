namespace Tosh.Core;

public sealed class ToshDiagnosticException : Exception
{
    public ToshDiagnosticException(IReadOnlyList<ToshDiagnostic> diagnostics)
        : base(diagnostics.FirstOrDefault()?.Title ?? "A Tosh diagnostic was reported.")
    {
        Diagnostics = diagnostics;
    }

    public IReadOnlyList<ToshDiagnostic> Diagnostics { get; }

    public static ToshDiagnosticException Create(ToshDiagnostic diagnostic)
    {
        return new ToshDiagnosticException([diagnostic]);
    }
}
