namespace Tosh.Compiler;

/// <summary>
/// How aggressive the <c>--compile</c> emitter is about rejecting
/// features that fall outside the requested compilation tier. The
/// emitter always emits the same IL for a feature regardless of
/// profile; the profile only controls whether out-of-tier features
/// produce a diagnostic that fails the compile.
/// </summary>
/// <remarks>
/// Tier definitions:
/// <list type="bullet">
///   <item><description><b>Tier 1 (pure):</b> real IL with no
///   <c>ToshHost</c> / <c>ToshEngine</c> calls at runtime. Literals,
///   arithmetic, control flow, top-level user-function calls
///   resolved at IL-emit time, real CLR module-type field reads.
///   </description></item>
///   <item><description><b>Tier 2 (runtime):</b> real IL but calls
///   <c>ToshHost</c> for builtin command dispatch, dynamic member
///   access, qualified lookup, etc. Today's default for any
///   non-trivial program.</description></item>
///   <item><description><b>Tier 3 (source-replay):</b> the emitted
///   assembly registers a span of the original tosh source and
///   re-evaluates it at runtime through <c>ToshEngine</c>. Used
///   for <c>class</c> / <c>record</c> / <c>struct</c> /
///   <c>union</c> / <c>enum</c> / <c>trait</c> bodies, complex
///   module bodies (anything beyond <c>var</c> / <c>func</c> /
///   nested <c>module</c>), and block-pipeline arguments.
///   </description></item>
/// </list>
/// See <c>docs/COMPILED_TOSH.md</c> ("Profiles &amp; the tier model" section) for the authoritative list.
/// </remarks>
public enum CompileProfile
{
    /// <summary>Default. All three tiers allowed.</summary>
    Permissive = 0,

    /// <summary>
    /// Tier 1 + Tier 2 only. Source-replay (Tier 3) is rejected.
    /// Use this profile when you want a real, redistributable .NET
    /// assembly that doesn't carry its own source for re-evaluation.
    /// </summary>
    Runtime = 1,

    /// <summary>
    /// Tier 1 only. No <c>ToshHost</c> calls at runtime, no source
    /// replay. Today this is mostly aspirational — even <c>echo</c>
    /// is a Tier-2 builtin — but the profile exists so we have a
    /// forcing function for future "lift this builtin into IL"
    /// work.
    /// </summary>
    Pure = 2,
}
