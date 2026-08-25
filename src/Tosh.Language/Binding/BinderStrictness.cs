namespace Tosh.Language.Binding;

/// <summary>
/// Controls how the binder reacts to command-name resolution failures
/// where a likely typo is detected (i.e. the unresolved name has a close
/// Levenshtein match against a registered builtin or a same-source
/// function declaration).
/// </summary>
/// <remarks>
/// The binder never raises diagnostics for names that <em>could</em> resolve
/// as externals at runtime (no registry suggestion, not an explicit path).
/// External resolution remains deferred to the evaluator.
/// </remarks>
public enum BinderStrictness
{
    /// <summary>
    /// Binder produces no diagnostics. Resolution annotations are still
    /// applied as a fast path for the evaluator. This is the bailout mode
    /// activated by <c>TOSH_DISABLE_BINDER=1</c>.
    /// </summary>
    Lenient,

    /// <summary>
    /// Binder reports diagnostics through the language runtime's diagnostic sink, but
    /// execution proceeds — the evaluator still attempts runtime resolution
    /// (which may succeed via PATH or fail with the runtime's
    /// <c>command_not_found</c> diagnostic). Default for the REPL,
    /// startup files, and <c>autoload/</c>.
    /// </summary>
    Warn,

    /// <summary>
    /// Binder throws <see cref="Tosh.Runtime.ToshDiagnosticException"/>
    /// before evaluation begins when any unresolved-with-suggestion is
    /// found. Default for <c>tosh -c</c>, script files, and the
    /// <c>source</c> command.
    /// </summary>
    Strict,
}
