namespace Tosh.Core;

/// <summary>
/// Severity of a TōSh diagnostic. Errors halt execution; warnings, infos, and hints
/// are advisory and can be suppressed via <c>hush</c>.
/// </summary>
public enum ToshDiagnosticSeverity
{
    Error = 0,
    Warning = 1,
    Info = 2,
    Hint = 3,
}

/// <summary>
/// Subject category of a diagnostic — what kind of problem it describes.
/// Independent of the emitting subsystem (which is encoded in the code's namespace).
/// </summary>
public enum ToshDiagnosticCategory
{
    /// <summary>Catch-all bucket for diagnostics that don't fit a specific category.</summary>
    Runtime = 0,
    /// <summary>Surface-level grammar / token-shape problems.</summary>
    Syntax,
    /// <summary>Type checking, conversion, mismatched shapes.</summary>
    Type,
    /// <summary>Identifier resolution: unbound names, shadowing, duplicates.</summary>
    Naming,
    /// <summary>Use of <c>fading</c> members or otherwise sunset features.</summary>
    Deprecation,
    /// <summary>Stylistic suggestions (formatting, idiom, casing).</summary>
    Style,
    /// <summary>Performance hazards (accidental O(n²), eager materialization).</summary>
    Performance,
    /// <summary>Cross-platform or version-compatibility concerns.</summary>
    Compatibility,
    /// <summary>CLR / native interop, marshaling, FFI surface.</summary>
    Interop,
    /// <summary>Security or privilege concerns (privileged ops, secrets in env, etc.).</summary>
    Security,
}

/// <summary>
/// Lifecycle stage of a diagnostic code itself. Lets us evolve wording / behavior
/// of preview diagnostics without committing to long-term stability.
/// </summary>
public enum ToshDiagnosticLifecycle
{
    Stable = 0,
    Preview,
    Deprecated,
}
