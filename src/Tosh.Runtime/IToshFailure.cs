namespace Tosh.Runtime;

/// <summary>
/// Anything the language raises: a declared error, or a diagnostic — `TOAST-0031`.
/// </summary>
/// <remarks>
/// <para>
/// Exposed to Tōast as <c>Failure</c>, with <c>Error</c> and <c>Diagnostic</c> beneath it.
/// The distinction the two carry is real and specified — one is a program raising something
/// deliberately, the other is the language reporting that an operation had no answer — and
/// before this there was no word for "either". A handler that wanted both had to ask
/// <c>$e is Exception</c>, which is a name borrowed from the host: a target without the CLR
/// has nothing to call it.
/// </para>
/// <para>
/// A marker interface rather than a base class, because the two are unrelated CLR types and
/// making either derive from the other would say something false about them. What they
/// share is a role, and an interface is how a role is spelled.
/// </para>
/// </remarks>
public interface IToshFailure
{
}
