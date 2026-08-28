using Tosh.Language;
using Tosh.Runtime;
using Tosh.Runtime.Formats;
using Tosh.Stdlib;

namespace Tosh.Tests;

/// <summary>
/// The built-in command table divides into a language half and a shell half —
/// <c>TOAST-0007</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>map</c>, <c>where</c>, <c>count</c> and <c>sort</c> are as much part of Tōast as
/// <c>for</c> is; <c>ls</c>, <c>ps</c> and <c>systemctl</c> are not. This is the slice that
/// makes the division explicit while both halves still live in one assembly, so that moving
/// them to separate projects afterwards is a relocation rather than a behavioural change.
/// </para>
/// <para>
/// The line was drawn from evidence rather than from where a file sits. A command that reaches
/// <c>context.Shell()</c> or <c>RequireCommandHost&lt;ToshRuntime&gt;()</c> cannot be
/// language-level, and that scan is what the second test here re-runs — it confirmed every
/// reclassification the item had argued by hand, including that Pipeline's one shell-dependent
/// command is <c>inspect</c> and Text's one is <c>wc</c>.
/// </para>
/// </remarks>
public sealed class BuiltInCommandSplitTests
{
    private static string RepositoryRoot() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));

    private static IReadOnlyList<string> NamesOf(Action<ShellCommandRegistry, DataFormatRegistry> register)
    {
        var registry = new ShellCommandRegistry();
        register(registry, BuiltInCommands.CreateDefaultFormats());
        return registry.AllNames.OrderBy(name => name, StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// The two halves are exactly the whole, with nothing lost and nothing registered twice.
    /// </summary>
    /// <remarks>
    /// This is what makes the split safe to make: <c>RegisterDefaults</c> is now their
    /// composition, so an existing host sees the same table it always did.
    /// </remarks>
    [Fact]
    public void The_two_registrars_partition_the_whole_table()
    {
        var language = NamesOf(BuiltInCommands.RegisterLanguageDefaults);
        var shell = NamesOf(BuiltInCommands.RegisterShellDefaults);

        var whole = new ShellCommandRegistry();
        BuiltInCommands.RegisterDefaults(whole);
        var expected = whole.AllNames.OrderBy(name => name, StringComparer.Ordinal).ToArray();

        Assert.Empty(language.Intersect(shell, StringComparer.Ordinal));
        Assert.Equal(expected, language.Concat(shell).OrderBy(n => n, StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// Both halves are substantial — a split that put almost everything on one side would
    /// satisfy the test above and mean nothing.
    /// </summary>
    [Fact]
    public void Both_halves_are_substantial()
    {
        Assert.InRange(NamesOf(BuiltInCommands.RegisterLanguageDefaults).Count, 150, 220);
        Assert.InRange(NamesOf(BuiltInCommands.RegisterShellDefaults).Count, 70, 130);
    }

    /// <summary>
    /// No language-level command reaches the shell host.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the mechanical form of "works in a host with no shell present": a command can
    /// only obtain a <c>ToshRuntime</c> through <c>context.Shell()</c> or
    /// <c>RequireCommandHost</c>, so a language command that reaches neither cannot need one.
    /// It is a source scan, which is coupled to file layout in a way a behavioural test is not
    /// — but it is the only check that covers all of them rather than a sample.
    /// </para>
    /// <para>
    /// Shared helpers are followed one level, because the first version of this test did not
    /// and passed while <c>read-file</c> was still broken: the dependency reached it through
    /// <c>FileIoUtilities.ResolveRequiredPath</c>, which asked the shell for its working
    /// directory. <c>File_io_works_with_no_shell_present</c> caught that by running the command
    /// instead of reading it, and one level of following is what makes this scan agree.
    /// </para>
    /// <para>
    /// A failure here means either a command was classified wrongly, or one that was
    /// language-level has newly reached for the shell. Both are worth stopping for.
    /// </para>
    /// </remarks>
    [Fact]
    public void No_language_command_reaches_the_shell_host()
    {
        var stdlib = Path.Combine(RepositoryRoot(), "src/Tosh.Stdlib");
        Assert.True(Directory.Exists(stdlib), $"stdlib sources not found at {stdlib}");

        // Type name to the source that declares it, so a file holding several commands is
        // found for each of them.
        var declaringSource = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var file in Directory.EnumerateFiles(stdlib, "*.cs", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);

            foreach (var line in text.Split('\n'))
            {
                var index = line.IndexOf("class ", StringComparison.Ordinal);
                if (index < 0 || !line.Contains("Command", StringComparison.Ordinal)) { continue; }

                var name = new string(line[(index + 6)..].TakeWhile(char.IsLetterOrDigit).ToArray());
                if (name.Length > 0) { declaringSource.TryAdd(name, text); }
            }
        }

        var registry = new ShellCommandRegistry();
        BuiltInCommands.RegisterLanguageDefaults(registry, BuiltInCommands.CreateDefaultFormats());

        // Helpers shared between the two halves, which a command reaches the shell through
        // without naming it. Keyed by type name so a command's source can be searched for it.
        var helpers = Directory
            .EnumerateFiles(stdlib, "*.cs", SearchOption.TopDirectoryOnly)
            .ToDictionary(Path.GetFileNameWithoutExtension!, File.ReadAllText, StringComparer.Ordinal);

        var offenders = new List<string>();

        foreach (var command in registry.All.Select(c => c.GetType().Name).Distinct(StringComparer.Ordinal))
        {
            if (!declaringSource.TryGetValue(command, out var source)) { continue; }

            var reachable = source;

            foreach (var (helper, text) in helpers)
            {
                if (source.Contains(helper + ".", StringComparison.Ordinal)) { reachable += text; }
            }

            if (reachable.Contains(".Shell()", StringComparison.Ordinal) ||
                reachable.Contains("RequireCommandHost", StringComparison.Ordinal))
            {
                offenders.Add(command);
            }
        }

        Assert.True(
            offenders.Count == 0,
            "these are registered as language-level but reach the shell host: "
                + string.Join(", ", offenders.OrderBy(n => n, StringComparer.Ordinal)));
    }

    /// <summary>
    /// The acceptance the item names: language commands run in a host with no shell.
    /// </summary>
    [Fact]
    public async Task A_language_pipeline_runs_with_no_shell_present()
    {
        using var output = new StringWriter();
        var language = new ToastRuntime { Output = ToastStreams.FromWriter(output) };
        BuiltInCommands.RegisterLanguageDefaults(
            (ShellCommandRegistry)language.Commands, BuiltInCommands.CreateDefaultFormats());

        var engine = new ToshEngine(language);

        var results = await engine.ExecuteToListAsync(
            "var items = [{| Name: \"beta\", Size: 2 |}, {| Name: \"alpha\", Size: 9 |}]\n" +
            "$items | where { $_.Size > 1 } | sort Name | get Name | collect");

        Assert.Equal(["alpha", "beta"], (IEnumerable<object?>)Assert.Single(results)!);
        Assert.Null(engine.LanguageRuntime.CommandHost);
    }

    /// <summary>
    /// Reading and writing a file is language-level, which is the reclassification the item
    /// argued hardest: a self-hosting Tōast has to read its own source, and that is
    /// <c>Stream</c> in C#, not a shell verb.
    /// </summary>
    [Fact]
    public async Task File_io_works_with_no_shell_present()
    {
        var path = Path.Combine(Path.GetTempPath(), $"toast-0007-{Guid.NewGuid():N}.txt");

        try
        {
            var language = new ToastRuntime();
            BuiltInCommands.RegisterLanguageDefaults(
                (ShellCommandRegistry)language.Commands, BuiltInCommands.CreateDefaultFormats());

            var engine = new ToshEngine(language);

            var results = await engine.ExecuteToListAsync(
                $"write-file \"{path}\" \"hello\"\nread-file \"{path}\"");

            Assert.Equal("hello", results[^1]?.ToString());
            Assert.Null(engine.LanguageRuntime.CommandHost);
        }
        finally
        {
            if (File.Exists(path)) { File.Delete(path); }
        }
    }

    /// <summary>
    /// The boundary is real rather than incidental: a shell command in a bare host fails, and
    /// says which capability is missing instead of throwing something opaque.
    /// </summary>
    [Fact]
    public async Task A_shell_command_in_a_bare_host_reports_the_missing_capability()
    {
        var language = new ToastRuntime();
        BuiltInCommands.RegisterShellDefaults(
            (ShellCommandRegistry)language.Commands, BuiltInCommands.CreateDefaultFormats());

        var engine = new ToshEngine(language);

        var error = await Assert.ThrowsAnyAsync<Exception>(
            async () => await engine.ExecuteToListAsync("pwd"));

        Assert.Contains("host", error.Message, StringComparison.OrdinalIgnoreCase);
    }
}
