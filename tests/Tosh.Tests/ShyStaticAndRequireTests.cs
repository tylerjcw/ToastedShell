using Tosh.Language;
using Tosh.Language.Binding;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// Five small items from one review — <c>TS-P2-61</c> through <c>TS-P2-65</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>TS-P2-61</c>: <c>shy</c> was honoured for a nested type and ignored for a static property,
/// so <c>class B { shy static prop S = 1 }</c> answered <c>B.S</c> from anywhere. One modifier
/// cannot mean two things depending on which kind of static member wears it. Static access
/// carries no <c>$this</c> to name an accessor the way an instance access does, so the engine
/// answers "who is asking?" instead — and the nested-type check, which threw unconditionally, was
/// over-strict in the other direction: a class could not reach its own shy nested type by
/// qualified name.
/// </para>
/// <para>
/// <c>TS-P2-62</c>: the specification said <c>require</c> "executes the script in the current
/// scope"; it imports exports and nothing else. The implementation is right — <c>export</c> would
/// mean nothing otherwise, and <c>source</c> already runs a file in the current scope — so the
/// sentence was corrected and a file that exports nothing now says so rather than importing
/// nothing in silence.
/// </para>
/// <para>
/// <c>TS-P2-63</c>: a <c>HelpTopic</c> among other values had no rendering at all. The table
/// profile needs uniform shapes, so a mixed batch fell to the generic container path and the
/// topic came out as a bare <c>[HelpTopic]</c> header — <c>help ls</c> alone panelled, <c>echo
/// one</c> then <c>help ls</c> did not.
/// </para>
/// <para>
/// <c>TS-P2-64</c>: redirection wrote a UTF-8 BOM. <c>TS-P2-65</c>: a redirection failure reported
/// <c>TOSH400</c>, outside the diagnostic scheme entirely.
/// </para>
/// </remarks>
public sealed class ShyStaticAndRequireTests
{
    private static async Task<string> RunAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var results = await engine.ExecuteToListAsync(source);
        return string.Join(",", results.Select(value => value?.ToString() ?? "null"));
    }

    private static async Task<ToshDiagnostic> RunForDiagnosticAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var exception = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => engine.ExecuteToListAsync(source));
        return exception.Diagnostics[0];
    }

    // ── `TS-P2-61`: shy means shy for a static member too ──────────────────────

    [Fact]
    public async Task A_shy_static_property_is_hidden_from_outside()
    {
        var diagnostic = await RunForDiagnosticAsync("class B { shy static prop S = 1 }\nB.S");

        Assert.Contains("shy", diagnostic.Title, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_shy_static_property_cannot_be_written_from_outside()
    {
        // Reads and writes answer to `shy` alike. Enforcing one and not the other would be a
        // worse asymmetry than the leak this closes.
        var diagnostic = await RunForDiagnosticAsync("class B { shy static prop S = 1 }\nB.S = 9");

        Assert.Contains("shy", diagnostic.Title, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_shy_static_property_is_visible_inside_its_class()
    {
        Assert.Equal("1", await RunAsync(
            """
            class B {
                shy static prop S = 1
                static func read() { return B.S }
            }
            B.read()
            """));
    }

    [Fact]
    public async Task A_shy_static_property_stays_hidden_from_a_subclass()
    {
        // `shy` is private, not protected — the same rule instance members follow.
        var diagnostic = await RunForDiagnosticAsync(
            """
            class B { shy static prop S = 1 }
            class D extends B {
                static func read() { return B.S }
            }
            D.read()
            """);

        Assert.Contains("shy", diagnostic.Title, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_class_can_reach_its_own_shy_nested_type_by_qualified_name()
    {
        // The over-strict half. The nested-type check threw whenever the type was shy, with no
        // notion of who was asking, so a class could not name its own by qualified name.
        Assert.Equal("A", await RunAsync(
            """
            class B {
                shy enum E : int { A = 1 }
                static func read() { return B.E.A }
            }
            B.read()
            """));
    }

    [Fact]
    public async Task A_shy_nested_type_is_still_hidden_from_outside()
    {
        var diagnostic = await RunForDiagnosticAsync(
            "class B { shy enum E : int { A = 1 } }\nB.E.A");

        Assert.Contains("shy", diagnostic.Title, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("class B { static prop S = 1 }\nB.S", "1")]
    [InlineData("class B { proud static prop S = 1 }\nB.S", "1")]
    [InlineData("class B { enum E : int { A = 1 } }\nB.E.A", "A")]
    public async Task A_static_member_that_is_not_shy_is_unchanged(string source, string expected)
    {
        Assert.Equal(expected, await RunAsync(source));
    }

    // ── `TS-P2-62`: require imports exports, and says when there are none ──────

    [Fact]
    public async Task Requiring_a_file_that_exports_nothing_says_so()
    {
        await WithLibraryAsync(
            "func helper() { return 1 }\n",
            async (engine, path) =>
            {
                var exception = await Assert.ThrowsAsync<ToshDiagnosticException>(
                    () => engine.ExecuteToListAsync($"require \"{path}\""));

                Assert.Equal("tosh.runtime.require_exports_nothing", exception.Diagnostics[0].Code);
                Assert.Contains("source", exception.Diagnostics[0].Help!, StringComparison.Ordinal);
            });
    }

    [Fact]
    public async Task An_exported_declaration_is_imported()
    {
        await WithLibraryAsync(
            "export func shared() { return \"importable\" }\nexport var VERSION = 2\n",
            async (engine, path) =>
            {
                await engine.ExecuteToListAsync($"require \"{path}\"");

                Assert.Equal("importable", (await engine.ExecuteToListAsync("shared"))[0]?.ToString());
                Assert.Equal("2", (await engine.ExecuteToListAsync("$VERSION"))[0]?.ToString());
            });
    }

    [Fact]
    public async Task Calling_an_imported_function_is_not_reported_as_a_typo()
    {
        // The binder resolves against the registry plus this source's own functions, and an
        // import is in neither — so it refused to run a call to a function that was present.
        // `echo (sourcee)` worked while a bare `sourcee` did not, because only command position
        // is inspected, which made it look like a resolution problem rather than a binder one.
        //
        // The name is chosen to be one edit from the builtin `source`. That detail is the test:
        // the binder only speaks up when it has a suggestion to offer, so a name with no near
        // neighbour would produce no diagnostic either way and prove nothing.
        await WithLibraryAsync(
            "export func sourcee() { return \"ok\" }\n",
            async (engine, path) =>
            {
                var withRequire = $"require \"{path}\"\nsourcee";

                // Half one: without the require, the binder does flag that name.
                var control = Binder.Bind(
                    engine.Parse("sourcee", "<require-test>"),
                    engine.LanguageRuntime.Commands,
                    isExecutableOnPath: _ => false);
                Assert.Contains(control, d => d.Code == "tosh.bind.unknown_command");

                // Half two: with it, the binder holds back, because it cannot see the import.
                var bound = Binder.Bind(
                    engine.Parse(withRequire, "<require-test>"),
                    engine.LanguageRuntime.Commands,
                    isExecutableOnPath: _ => false);
                Assert.DoesNotContain(bound, d => d.Code == "tosh.bind.unknown_command");

                Assert.Equal("ok", (await engine.ExecuteToListAsync(withRequire))[0]?.ToString());
            });
    }

    [Fact]
    public async Task A_private_declaration_stays_private()
    {
        await WithLibraryAsync(
            "func helper() { return 1 }\nexport func shared() { return 2 }\n",
            async (engine, path) =>
            {
                await engine.ExecuteToListAsync($"require \"{path}\"");

                await Assert.ThrowsAnyAsync<Exception>(() => engine.ExecuteToListAsync("helper"));
            });
    }

    [Fact]
    public async Task Source_still_brings_in_everything()
    {
        // The difference between the two spellings, which is what makes `export` mean something.
        await WithLibraryAsync(
            "func helper() { return \"private\" }\n",
            async (engine, path) =>
            {
                var results = await engine.ExecuteToListAsync($"source \"{path}\"\nhelper");

                Assert.Equal("private", results[0]?.ToString());
            });
    }

    private static async Task WithLibraryAsync(string body, Func<ToshEngine, string, Task> probe)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tosh-req-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            var path = Path.Combine(directory, "lib.tosh");
            await File.WriteAllTextAsync(path, body);

            var runtime = ToshRuntime.CreateDefault();
            runtime.CurrentDirectory = directory;

            await probe(new ToshEngine(runtime.Language), path.Replace("\\", "/"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // ── `TS-P2-63`: a help topic keeps its panel among other values ────────────

    [Fact]
    public void A_help_topic_among_other_values_keeps_its_own_rendering()
    {
        var runtime = ToshRuntime.CreateDefault();
        var topic = HelpCatalog.ResolveTopic(runtime, "ls");

        Assert.NotNull(topic);

        var mixed = runtime.Display.RenderMany(["one", topic]);

        // The panel's header line, which the generic container path cannot produce.
        Assert.Contains("Built-in", mixed, StringComparison.Ordinal);
        Assert.DoesNotContain("[HelpTopic]", mixed, StringComparison.Ordinal);
    }

    [Fact]
    public void A_lone_help_topic_is_unchanged()
    {
        var runtime = ToshRuntime.CreateDefault();
        var topic = HelpCatalog.ResolveTopic(runtime, "ls");

        Assert.Contains("Built-in", runtime.Display.RenderMany([topic!]), StringComparison.Ordinal);
    }

    [Fact]
    public void Several_help_topics_still_tabulate()
    {
        // Deliberate, and worth keeping: a table is how two topics are compared.
        var runtime = ToshRuntime.CreateDefault();
        var ls = HelpCatalog.ResolveTopic(runtime, "ls");
        var cd = HelpCatalog.ResolveTopic(runtime, "cd");

        var rendered = runtime.Display.RenderMany([ls!, cd!]);

        Assert.Contains("Category", rendered, StringComparison.Ordinal);
    }

    // ── `TS-P2-64` and `TS-P2-65`: redirection ─────────────────────────────────

    [Theory]
    [InlineData("out>")]
    [InlineData("out>>")]
    public async Task Redirection_writes_no_byte_order_mark(string redirect)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tosh-redir-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            var target = Path.Combine(directory, "out.txt").Replace("\\", "/");
            await RunAsync($"echo \"one\" {redirect} \"{target}\"");

            var bytes = await File.ReadAllBytesAsync(target);

            // Asserted as bytes rather than text: the BOM compares equal either way once the
            // file is read back as a string, which is how it went unnoticed.
            Assert.True(bytes.Length >= 3);
            Assert.False(
                bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
                "the redirected file begins with a UTF-8 BOM");
            Assert.Equal((byte)'o', bytes[0]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task A_redirected_shebang_script_is_executable()
    {
        // What the BOM actually cost: three bytes in front of `#!` and the kernel will not run it.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = Path.Combine(Path.GetTempPath(), $"tosh-shebang-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            var target = Path.Combine(directory, "probe.sh").Replace("\\", "/");
            await RunAsync($"echo \"#!/bin/sh\" out> \"{target}\"");

            var bytes = await File.ReadAllBytesAsync(target);

            Assert.Equal((byte)'#', bytes[0]);
            Assert.Equal((byte)'!', bytes[1]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task A_redirection_failure_carries_a_tosh_diagnostic_code()
    {
        // `TOSH400` sat outside the scheme entirely, so it never reached the generated reference
        // and could not be hushed by code the way the documentation says any diagnostic can.
        var directory = Path.Combine(Path.GetTempPath(), $"tosh-redir-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            var missing = Path.Combine(directory, "nodir", "out.txt").Replace("\\", "/");
            var diagnostic = await RunForDiagnosticAsync($"echo \"x\" out> \"{missing}\"");

            Assert.Equal("tosh.runtime.redirection_target_unavailable", diagnostic.Code);
            Assert.StartsWith("tosh.", diagnostic.Code, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Redirection_still_creates_and_appends()
    {
        // The control for `TS-P2-57`, which was withdrawn as not reproducible: append creates a
        // missing target and adds to an existing one.
        var directory = Path.Combine(Path.GetTempPath(), $"tosh-redir-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            var target = Path.Combine(directory, "log.txt").Replace("\\", "/");
            await RunAsync($"echo \"one\" out>> \"{target}\"");
            await RunAsync($"echo \"two\" out>> \"{target}\"");

            Assert.Equal("one\ntwo\n", (await File.ReadAllTextAsync(target)).Replace("\r\n", "\n"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
