using Tosh.Language;
using Tosh.Language.Binding;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// Tilde expansion — <c>TS-P2-60</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>echo ~</c> printed a literal tilde. So did <c>/bin/echo ~</c>, and <c>echo ~/projects</c>,
/// and <c>echo ~komrad</c>. Meanwhile <c>cd ~</c>, <c>ls ~</c> and <c>read-file ~/x</c> all
/// worked — which is what made the gap look like "externals expand, builtins do not". They do
/// not: <em>nothing</em> expanded a tilde at the shell's own argument layer. A <c>~</c> reached
/// a command only when that command happened to path-resolve its arguments itself, so whether
/// <c>~</c> meant the home directory was decided separately by every command that took a path.
/// </para>
/// <para>
/// Expansion now happens once, for every command, where globbing already happened. Only
/// barewords are expanded: <c>echo "~"</c> stays literal, because quoting is how a tilde is
/// written when a tilde is what is wanted.
/// </para>
/// <para>
/// A bare <c>~</c> was a second, separate problem, and it lived in the binder rather than the
/// engine: <c>LooksLikeExplicitPath</c> knew <c>/</c> and <c>.</c> but not <c>~</c>, so
/// <c>~/projects</c> passed only by containing a separator while a bare <c>~</c> fell through to
/// the typo machinery and came back "did you mean 'f'?". Edit distance answers any question it
/// is asked; it is now asked only about words that could be names.
/// </para>
/// </remarks>
public sealed class TildeExpansionTests
{
    private static string Home => PathUtilities.UserHomeDirectory;

    private static async Task<string> RunAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(source);
        return string.Join(",", results.Select(value => value?.ToString() ?? "null"));
    }

    // ── The reported gap ───────────────────────────────────────────────────────

    [Fact]
    public async Task A_bare_tilde_argument_expands_to_the_home_directory()
    {
        Assert.Equal(Home, await RunAsync("echo ~"));
    }

    [Fact]
    public async Task A_tilde_path_argument_expands()
    {
        Assert.Equal(Path.Combine(Home, "projects"), await RunAsync("echo ~/projects"));
    }

    [Fact]
    public async Task The_current_user_resolves_by_name()
    {
        Assert.Equal(Home, await RunAsync($"echo ~{Environment.UserName}"));
    }

    [Fact]
    public async Task A_named_user_path_keeps_its_remainder()
    {
        Assert.Equal(
            Path.Combine(Home, "projects", "tosh"),
            await RunAsync($"echo ~{Environment.UserName}/projects/tosh"));
    }

    [Fact]
    public async Task Expansion_reaches_a_command_that_does_not_resolve_paths_itself()
    {
        // The point of moving this to the argument layer. `count` has no interest in paths at
        // all, and still sees an expanded word rather than a tilde.
        Assert.Equal("1", await RunAsync("echo ~ | count"));
        Assert.DoesNotContain("~", await RunAsync("echo ~/a/b"), StringComparison.Ordinal);
    }

    // ── Only barewords, and only at the front ──────────────────────────────────

    [Fact]
    public async Task A_quoted_tilde_stays_literal()
    {
        Assert.Equal("~", await RunAsync("echo \"~\""));
        Assert.Equal("~/projects", await RunAsync("echo \"~/projects\""));
    }

    [Fact]
    public async Task A_tilde_held_in_a_variable_stays_literal()
    {
        // Matching every POSIX shell: expansion is lexical, so it happens to the word as
        // written and not to whatever a variable turns out to hold.
        Assert.Equal("~", await RunAsync("var p = \"~\"\necho $p"));
    }

    [Theory]
    [InlineData("echo notes.txt~", "notes.txt~")]
    [InlineData("echo a~b", "a~b")]
    [InlineData("echo --out=~/x", "--out=~/x")]
    public async Task A_tilde_that_is_not_the_first_character_stays_literal(string source, string expected)
    {
        // Editors leave `file~` backups all over a working tree, and expanding a tilde in the
        // middle of a word would rewrite them.
        Assert.Equal(expected, await RunAsync(source));
    }

    // ── An unresolvable name is refused rather than passed on ──────────────────

    [Fact]
    public async Task An_unknown_tilde_name_is_a_diagnostic()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var exception = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => engine.ExecuteToListAsync("echo ~nosuchuseranywhere"));
        var diagnostic = Assert.Single(exception.Diagnostics);

        Assert.Equal("tosh.runtime.unknown_tilde_target", diagnostic.Code);
        Assert.Contains("nosuchuseranywhere", diagnostic.Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_diagnostic_names_a_spelling_that_actually_works()
    {
        // The help text offers a directory-alias assignment. An earlier draft named a config
        // path that does not exist, which is the kind of advice that costs a reader more time
        // than the original error did — so the suggested spelling is executed here.
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var exception = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => engine.ExecuteToListAsync("echo ~nosuchuseranywhere"));

        Assert.Contains("$tosh.Config.Shell.Dirs", exception.Diagnostics[0].Help!, StringComparison.Ordinal);
        Assert.Equal(
            "/tmp",
            await RunAsync("$tosh.Config.Shell.Dirs.nosuchuseranywhere = \"/tmp\"\necho ~nosuchuseranywhere"));
    }

    // ── Directory aliases ──────────────────────────────────────────────────────

    [Fact]
    public async Task A_directory_alias_expands_with_its_remainder()
    {
        Assert.Equal(
            Path.Combine("/tmp", "inner"),
            await RunAsync("$tosh.Config.Shell.Dirs.aliasprobe = \"/tmp\"\necho ~aliasprobe/inner"));
    }

    [Fact]
    public async Task A_directory_alias_beats_a_user_of_the_same_name()
    {
        // Whoever runs the shell wrote the alias down on purpose; the accounts on the machine
        // are not their doing.
        Assert.Equal(
            "/tmp",
            await RunAsync($"$tosh.Config.Shell.Dirs.{Environment.UserName} = \"/tmp\"\necho ~{Environment.UserName}"));
    }

    // ── Globbing still works, and works underneath a tilde ─────────────────────

    [Fact]
    public async Task A_glob_under_a_tilde_matches()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var probeDirectory = Path.Combine(Path.GetTempPath(), $"tosh-tilde-{Guid.NewGuid():N}");
        Directory.CreateDirectory(probeDirectory);

        try
        {
            var marker = Path.Combine(probeDirectory, "marker.probe");
            await File.WriteAllTextAsync(marker, "x");

            await engine.ExecuteToListAsync($"$tosh.Config.Shell.Dirs.tildeglob = \"{probeDirectory}\"");
            var results = await engine.ExecuteToListAsync("echo ~tildeglob/*.probe");

            Assert.Equal(marker, Assert.Single(results)?.ToString());
        }
        finally
        {
            Directory.Delete(probeDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task A_glob_with_no_tilde_is_unchanged()
    {
        var probeDirectory = Path.Combine(Path.GetTempPath(), $"tosh-glob-{Guid.NewGuid():N}");
        Directory.CreateDirectory(probeDirectory);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(probeDirectory, "one.probe"), "x");
            await File.WriteAllTextAsync(Path.Combine(probeDirectory, "two.probe"), "x");

            var engine = new ToshEngine(ToshRuntime.CreateDefault());
            var results = await engine.ExecuteToListAsync(
                $"echo {probeDirectory}/*.probe");

            Assert.Equal(2, results.Count);
        }
        finally
        {
            Directory.Delete(probeDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task A_glob_that_matches_nothing_is_still_passed_through_literally()
    {
        Assert.Equal(
            "/nonexistent-tosh-probe/*.nope",
            await RunAsync("echo /nonexistent-tosh-probe/*.nope"));
    }

    // ── Commands that resolved paths themselves are unaffected ─────────────────

    [Fact]
    public async Task Cd_still_follows_a_tilde()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        await engine.ExecuteToListAsync("cd ~");

        Assert.Equal(Home, engine.Runtime.CurrentDirectory);
    }

    [Fact]
    public async Task Cd_still_follows_a_tilde_path()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var probeDirectory = Path.Combine(Home, $".tosh-tilde-{Guid.NewGuid():N}");
        Directory.CreateDirectory(probeDirectory);

        try
        {
            await engine.ExecuteToListAsync($"cd ~/{Path.GetFileName(probeDirectory)}");
            Assert.Equal(probeDirectory, engine.Runtime.CurrentDirectory);
        }
        finally
        {
            Directory.Delete(probeDirectory, recursive: true);
        }
    }

    // ── The bare `~` at the prompt ─────────────────────────────────────────────

    /// <summary>
    /// Binds <paramref name="source"/> against a registry that also holds a one-character
    /// command.
    /// </summary>
    /// <remarks>
    /// That detail is the whole test. A default registry has nothing within one edit of a
    /// single punctuation character, so binding `~` against it produces no diagnostic whatever
    /// the rule says — an earlier draft of these tests passed against the unfixed binder for
    /// exactly that reason. The reporter had a `func f` in their profile, which is where
    /// "did you mean 'f'?" came from, so the collision is recreated here rather than assumed.
    /// </remarks>
    private static async Task<IReadOnlyList<ToshDiagnostic>> BindWithShortCommandAsync(string source)
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);
        await engine.ExecuteToListAsync("func f => ls");
        var parse = engine.Parse(source, "<tilde-test>");

        return Binder.Bind(parse, runtime.Commands, isExecutableOnPath: _ => false);
    }

    [Fact]
    public async Task The_collision_that_produced_the_report_is_real()
    {
        // Half one of every test below: `f` really is within one edit of `~`, so a rule that
        // declines to suggest is doing work rather than describing an empty set.
        var diagnostics = await BindWithShortCommandAsync("lz");

        Assert.Contains(diagnostics, d => d.Label?.Contains("'ls'", StringComparison.Ordinal) == true);
    }

    [Theory]
    [InlineData("~")]
    [InlineData("~work")]
    [InlineData("~/projects")]
    public async Task No_tilde_form_is_flagged_as_a_misspelled_command(string source)
    {
        // `~/projects` passed before only by containing a separator; the other two did not.
        Assert.Empty(await BindWithShortCommandAsync(source));
    }

    [Theory]
    // Punctuation is not a misspelling of anything. Edit distance answered anyway, and a bare
    // `~` came back as a possible `f`. `!` is not among these: history expansion consumes it
    // before the binder ever sees a command, so a case for it would pass either way.
    [InlineData("~")]
    [InlineData("%")]
    public async Task A_word_that_is_not_name_shaped_gets_no_suggestion(string source)
    {
        var diagnostics = await BindWithShortCommandAsync(source);

        Assert.DoesNotContain(diagnostics, d => d.Label?.Contains("did you mean", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public async Task A_real_typo_is_still_suggested_against()
    {
        // The control for the rule above: declining to suggest for punctuation must not
        // silence suggestions for words.
        var diagnostics = await BindWithShortCommandAsync("flatmap");

        Assert.Contains(diagnostics, d => d.Label?.Contains("did you mean", StringComparison.OrdinalIgnoreCase) == true);
    }

    // ── The tilde as a command head ────────────────────────────────────────────

    [Fact]
    public async Task A_bare_tilde_in_command_position_changes_to_the_home_directory()
    {
        // With `auto_cd` on, `~` moves exactly as a bare `/tmp` does. There is no special case
        // for the tilde here: it expands to a path and the existing rule applies. The setting
        // is written down rather than assumed — the runtime default is off, and the shell's
        // shipped config is what turns it on.
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        engine.Runtime.Config.Shell.AutoCd = true;
        await engine.ExecuteToListAsync("~");

        Assert.Equal(Home, engine.Runtime.CurrentDirectory);
    }

    [Fact]
    public async Task A_tilde_path_in_command_position_changes_directory()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        engine.Runtime.Config.Shell.AutoCd = true;
        var probeDirectory = Path.Combine(Home, $".tosh-cmd-{Guid.NewGuid():N}");
        Directory.CreateDirectory(probeDirectory);

        try
        {
            await engine.ExecuteToListAsync($"~/{Path.GetFileName(probeDirectory)}");
            Assert.Equal(probeDirectory, engine.Runtime.CurrentDirectory);
        }
        finally
        {
            Directory.Delete(probeDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task With_auto_cd_off_a_bare_tilde_reports_a_directory_like_any_path()
    {
        // The command head is expanded too, so `~` and the absolute path it stands for get the
        // same answer. Without that it came back "Command '~' was not found" — one input,
        // described two different ways depending on how it was spelled.
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        engine.Runtime.Config.Shell.AutoCd = false;

        var exception = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => engine.ExecuteToListAsync("~"));

        Assert.Equal("tosh.runtime.external_command_is_directory", exception.Diagnostics[0].Code);
        Assert.Contains(Home, exception.Diagnostics[0].Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_auto_cd_off_an_absolute_path_reports_the_same_way()
    {
        // The control that makes the test above mean something: the two spellings agree.
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        engine.Runtime.Config.Shell.AutoCd = false;

        var exception = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => engine.ExecuteToListAsync($"{Home}"));

        Assert.Equal("tosh.runtime.external_command_is_directory", exception.Diagnostics[0].Code);
    }

    [Fact]
    public async Task The_runtimes_own_suggestion_helper_also_declines_on_punctuation()
    {
        // The binder is not the only place that guesses. With `auto_cd` off and nothing to
        // resolve, the engine writes its own "did you mean" — and answered `~` with `bg`
        // until the rule moved to the registry both of them read.
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        engine.Runtime.Config.Shell.AutoCd = false;

        var exception = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => engine.ExecuteToListAsync("%%%"));

        Assert.DoesNotContain(
            "did you mean",
            exception.Diagnostics[0].Help ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_name_shape_rule_still_admits_real_command_names()
    {
        Assert.True(ShellCommandRegistry.IsNameShaped("ls"));
        Assert.True(ShellCommandRegistry.IsNameShaped("flat-map"));
        Assert.True(ShellCommandRegistry.IsNameShaped("_private"));
        Assert.True(ShellCommandRegistry.IsNameShaped("Mod.fn"));
        Assert.False(ShellCommandRegistry.IsNameShaped("~"));
        Assert.False(ShellCommandRegistry.IsNameShaped("%"));
        Assert.False(ShellCommandRegistry.IsNameShaped("2fast"));
        Assert.False(ShellCommandRegistry.IsNameShaped(string.Empty));
    }

    // ── The rule itself ────────────────────────────────────────────────────────

    [Fact]
    public void The_expansion_rule_reports_what_it_did()
    {
        Assert.Equal(PathUtilities.TildeExpansionKind.NotATilde, PathUtilities.ExpandTilde("plain").Kind);
        Assert.Equal(PathUtilities.TildeExpansionKind.NotATilde, PathUtilities.ExpandTilde("a~b").Kind);
        Assert.Equal(PathUtilities.TildeExpansionKind.Expanded, PathUtilities.ExpandTilde("~").Kind);
        Assert.Equal(PathUtilities.TildeExpansionKind.Expanded, PathUtilities.ExpandTilde("~/x").Kind);
        Assert.Equal(PathUtilities.TildeExpansionKind.UnknownName, PathUtilities.ExpandTilde("~nosuchuseranywhere").Kind);
    }

    [Fact]
    public void An_empty_word_is_not_a_tilde()
    {
        Assert.Equal(PathUtilities.TildeExpansionKind.NotATilde, PathUtilities.ExpandTilde(string.Empty).Kind);
    }
}
