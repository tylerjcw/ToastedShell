namespace Tosh.Runtime;

/// <summary>
/// Marks the host-specific state made available to commands invoked by Tōast.
/// </summary>
/// <remarks>
/// The language carries this value opaquely: a command package may require its own host
/// type through <see cref="CommandContext.RequireCommandHost{THost}"/>, while the evaluator
/// and language runtime need not reference that type or its assembly. TōSh supplies its
/// session runtime during the compatibility phase of `TOAST-0006`; another host
/// can supply an unrelated implementation.
/// </remarks>
public interface IToastCommandHost
{
}
