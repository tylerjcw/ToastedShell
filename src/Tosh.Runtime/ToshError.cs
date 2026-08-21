namespace Tosh.Runtime;

/// <summary>
/// Base class for user-defined error types in TōSh. The recommended
/// way for tosh code to introduce a custom error is to declare a
/// class that <c>extends Error</c> (which is exposed as an alias for
/// <see cref="ToshError"/> via <see cref="DotNetTypeResolver"/>).
///
/// <para>
/// <see cref="ToshError"/> is a real <see cref="Exception"/> subclass
/// so cross-language consumers (C#, F#, VB) can <c>catch</c> tosh
/// errors directly by their concrete type instead of always going
/// through <see cref="ThrowSignalException"/>. When tosh code throws
/// a value that is itself an <see cref="Exception"/>, the engine
/// raises it verbatim (no wrap); when it throws a non-exception
/// value (string, number, record, …), the engine wraps it in a
/// <see cref="ThrowSignalException"/> so the value round-trips
/// through <c>catch (err)</c> intact, mirroring shell-style
/// throw-anything semantics.
/// </para>
///
/// <para>
/// <see cref="Cause"/> optionally carries the original raised value
/// when one was supplied (e.g. when the engine boundary-wraps a
/// non-exception throw). This is part of the public CLR ABI v1.
/// </para>
/// </summary>
public class ToshError : Exception, IToshFailure
{
    public ToshError()
        : base("An error was raised.")
    {
    }

    public ToshError(string? message)
        : base(message ?? "An error was raised.")
    {
    }

    public ToshError(string? message, Exception? innerException)
        : base(message ?? "An error was raised.", innerException)
    {
    }

    public ToshError(string? message, object? cause)
        : base(message ?? "An error was raised.")
    {
        Cause = cause;
    }

    public ToshError(string? message, TextSpan span, object? cause = null)
        : base(message ?? "An error was raised.")
    {
        Span = span;
        Cause = cause;
    }

    /// <summary>
    /// Optional source span identifying where the error was raised.
    /// Set automatically by the engine and the compiled-mode runtime
    /// host when boundary-wrapping a thrown value; user code may
    /// also assign it manually.
    /// </summary>
    public TextSpan Span { get; set; }

    /// <summary>
    /// Optional original value that triggered the error. When tosh
    /// code writes <c>throw "boom"</c> and the boundary wraps it,
    /// <see cref="Cause"/> holds the string <c>"boom"</c>; consumers
    /// who want the raw payload back can read this property.
    /// </summary>
    public object? Cause { get; }
}
