using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// The language's own settings, and the shell's view of them.
///
/// `TOAST-0006`, stage 1. `MaxRecursionDepth` is a limit on the evaluator and the hushed
/// set backs `hush`, which is language syntax — yet both lived in `ToshConfig`, loaded
/// from `~/.config/tosh`. The rule is that nothing in that directory may affect Tōast, so
/// a host embedding the language with no shell had no answer for either.
///
/// `ToastOptions` is the **authority**; the config sections are a view onto it. That
/// direction is the whole point, and it is what these tests pin: a copy would satisfy any
/// test that only read a value back.
/// </summary>
public sealed class ToastOptionsTests
{
    /// <summary>
    /// A language-only host gets working defaults with no configuration of any kind.
    /// </summary>
    [Fact]
    public void Options_stand_alone_with_defaults()
    {
        var options = new ToastOptions();

        Assert.True(options.MaxRecursionDepth > 0);
        Assert.Empty(options.HushedDiagnostics);
    }

    /// <summary>
    /// The shell's view writes through to the language's storage — not to a copy of it.
    /// </summary>
    [Fact]
    public void Setting_it_through_the_shell_config_changes_the_language_options()
    {
        var runtime = ToshRuntime.CreateDefault();

        runtime.Config.Shell.MaxRecursionDepth = 37;

        Assert.Equal(37, runtime.Options.MaxRecursionDepth);
    }

    /// <summary>
    /// And the other direction, which is the half a copy would pass: setting the language
    /// option must be visible through the config, or `$tosh.Config` would show a stale
    /// value while the evaluator used the real one.
    /// </summary>
    [Fact]
    public void Setting_the_language_options_is_visible_through_the_shell_config()
    {
        var runtime = ToshRuntime.CreateDefault();

        runtime.Options.MaxRecursionDepth = 41;

        Assert.Equal(41, runtime.Config.Shell.MaxRecursionDepth);
    }

    /// <summary>
    /// The hushed set is one collection, not two. `hush` writes it through the language;
    /// `$tosh.Config.Diagnostics.Hushed` is how a user reads and edits it.
    /// </summary>
    [Fact]
    public void The_hushed_set_is_shared_in_both_directions()
    {
        var runtime = ToshRuntime.CreateDefault();

        runtime.Options.HushedDiagnostics.Add("tosh.example.one");
        Assert.Contains("tosh.example.one", runtime.Config.Diagnostics.Hushed);

        runtime.Config.Diagnostics.Hushed.Add("tosh.example.two");
        Assert.Contains("tosh.example.two", runtime.Options.HushedDiagnostics);
    }

    /// <summary>
    /// The five shell-named execution settings write through to the language options in
    /// both directions, like MaxRecursionDepth.
    /// </summary>
    /// <remarks>
    /// These arrived as shell options — pipefail, set -e, set -x, auto_cd — but each
    /// changes what the *language* does, so the storage is the language's and
    /// $tosh.Config.Shell is the view. AutoCd is the most debatable and is included
    /// deliberately: it decides what an unresolved bare word means, which is a dispatch
    /// rule.
    /// </remarks>
    [Fact]
    public void The_execution_settings_are_shared_in_both_directions()
    {
        var runtime = ToshRuntime.CreateDefault();

        runtime.Config.Shell.Pipefail = true;
        runtime.Config.Shell.ExitOnError = true;
        runtime.Config.Shell.Trace = true;
        runtime.Config.Shell.ScriptTrace = true;
        runtime.Config.Shell.AutoCd = true;

        Assert.True(runtime.Options.Pipefail);
        Assert.True(runtime.Options.ExitOnError);
        Assert.True(runtime.Options.Trace);
        Assert.True(runtime.Options.ScriptTrace);
        Assert.True(runtime.Options.AutoCd);

        runtime.Options.Pipefail = false;
        Assert.False(runtime.Config.Shell.Pipefail);
    }

    /// <summary>
    /// Validation stays with the storage rather than the view, so an invalid depth is
    /// refused however it is set.
    /// </summary>
    [Fact]
    public void An_invalid_depth_is_refused_through_either_route()
    {
        var runtime = ToshRuntime.CreateDefault();

        Assert.ThrowsAny<Exception>(() => runtime.Options.MaxRecursionDepth = 0);
        Assert.ThrowsAny<Exception>(() => runtime.Config.Shell.MaxRecursionDepth = 0);
    }

    /// <summary>
    /// The end-to-end case that made a snapshot unworkable: the limit is changed *from
    /// script*, and the evaluator must honour the new value rather than one captured at
    /// startup.
    /// </summary>
    [Fact]
    public async Task A_depth_set_from_script_reaches_the_evaluator()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);

        await engine.ExecuteToListAsync("$tosh.Config.Shell.MaxRecursionDepth = 12");

        Assert.Equal(12, runtime.Options.MaxRecursionDepth);
    }
}
