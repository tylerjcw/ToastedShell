using System.Text.RegularExpressions;
using Tosh.Language;
using Tosh.Language.Parsing;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// The specification's multi-line worked examples, executed rather than read.
///
/// `TS-P2-26`: the "CSV Processing" example used <c>from-csv</c>, <c>to-csv</c>
/// and <c>select Date, Customer, Amount</c>; the real spellings are
/// <c>from csv</c>, <c>to csv</c> and space-separated arguments. "JSON API
/// Processing" used <c>from-json</c> and <c>to-json</c>. Three of the four
/// commands in one example did not exist.
///
/// <see cref="SpecConformanceTests"/> could not have caught any of it. Its corpus
/// is harvested from lines carrying a *documented expected value*, which excludes
/// every multi-line pipeline — precisely the examples a new user copies first.
///
/// <para><b>These are harvested, not curated.</b> That is the whole point. The
/// examples rotted because inclusion was opt-in, so an example nobody added was
/// an example nobody checked. Here every <c>lstlisting</c> in the Common Tasks
/// chapter is picked up automatically and a new one is covered the day it is
/// written; the only way out is <see cref="NonToastScriptBlocks"/>, which names
/// each exclusion and why.</para>
/// </summary>
public sealed class SpecWorkedExampleTests : IClassFixture<ToshRuntimeFixture>
{
    private readonly ToshRuntime _runtime;

    public SpecWorkedExampleTests(ToshRuntimeFixture fixture) => _runtime = fixture.Runtime;

    private static readonly string SpecPath = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "../../../../../docs/spec/toastscript-spec.tex"));

    /// <summary>
    /// Blocks in the chapter that are not ToastScript source. Each needs a reason:
    /// an exclusion without one is how the rot started.
    /// </summary>
    private static readonly Dictionary<string, string> NonToastScriptBlocks = new()
    {
        ["Delimited Column Types"] =
            "the block is CSV input data plus a table of inferred column types, not code",
    };

    /// <summary>
    /// External programs the examples invoke. Supplied to the binder explicitly so
    /// the corpus asserts the same thing on a machine without systemd as on one
    /// with it — otherwise the test would be checking the runner's package list.
    /// </summary>
    private static readonly HashSet<string> ExternalTools = new(StringComparer.Ordinal)
    {
        "find", "mv", "du", "grep", "ps", "systemctl", "journalctl", "ping",
        "dirname", "/usr/bin/dotnet",
    };

    public static IEnumerable<object[]> WorkedExamples() =>
        Harvest().Select(e => new object[] { e.Title, e.Source });

    private static List<(string Title, string Source)> Harvest()
    {
        var text = File.ReadAllText(SpecPath);

        var chapter = text.IndexOf(@"\chapter{Common Tasks}", StringComparison.Ordinal);
        Assert.True(chapter >= 0, $"Common Tasks chapter not found in {SpecPath}");

        // The chapter runs to the next chapter or to the end of the document.
        var next = text.IndexOf(@"\chapter{", chapter + 1, StringComparison.Ordinal);
        var body = next >= 0 ? text[chapter..next] : text[chapter..];

        var examples = new List<(string, string)>();
        var title = "(untitled)";

        // Subsections and listings interleave, so one scan keeps each block
        // attached to the heading above it.
        var scan = new Regex(
            @"\\subsection\{(?<title>[^}]*)\}|\\begin\{lstlisting\}(\[[^\]]*\])?(?<body>.*?)\\end\{lstlisting\}",
            RegexOptions.Singleline);

        foreach (Match match in scan.Matches(body))
        {
            if (match.Groups["title"].Success)
            {
                title = match.Groups["title"].Value;
                continue;
            }

            if (NonToastScriptBlocks.ContainsKey(title))
            {
                continue;
            }

            examples.Add((title, match.Groups["body"].Value.Trim('\n')));
        }

        return examples;
    }

    /// <summary>
    /// The guard on the harvester itself. A regex that silently matches nothing
    /// would make every theory below pass with no cases at all — the same shape of
    /// failure as the original rot, where nothing was checked and nothing
    /// complained. The count is asserted loosely so ordinary editing of the
    /// chapter does not fail the suite, but deleting the chapter or breaking the
    /// pattern does.
    /// </summary>
    [Fact]
    public void The_harvester_finds_the_chapter_and_its_examples()
    {
        var examples = Harvest();

        Assert.True(examples.Count >= 15,
            $"harvested only {examples.Count} worked examples; the extractor or the chapter changed shape.");

        Assert.Contains(examples, e => e.Title == "CSV Processing");
        Assert.Contains(examples, e => e.Title == "JSON API Processing");
        Assert.All(examples, e => Assert.False(string.IsNullOrWhiteSpace(e.Source)));
    }

    /// <summary>
    /// The guard on the command walk, for the same reason as the one above. The
    /// walk is reflective, so a change to the syntax records could quietly stop it
    /// finding anything and every case would pass on an empty set. Asserting a
    /// few names that are unmistakably present makes that failure loud.
    /// </summary>
    [Fact]
    public void The_command_walk_actually_finds_commands()
    {
        var engine = new ToshEngine(_runtime.Language);

        var found = Harvest()
            .SelectMany(e => CollectCommandNames(engine.Parse(e.Source, $"<spec:{e.Title}>").Statement))
            .ToHashSet(StringComparer.Ordinal);

        Assert.True(found.Count >= 20,
            $"the syntax walk found only {found.Count} command names across the chapter: " +
            string.Join(", ", found.OrderBy(n => n, StringComparer.Ordinal)));

        foreach (var expected in new[] { "from", "sort", "where", "map", "to" })
        {
            Assert.Contains(expected, found);
        }
    }

    [Theory]
    [MemberData(nameof(WorkedExamples))]
    public void A_worked_example_parses(string title, string source)
    {
        var parse = new ToshEngine(_runtime.Language).Parse(source, $"<spec:{title}>");

        Assert.True(parse.Diagnostics.Count == 0,
            $"'{title}' does not parse:\n  " +
            string.Join("\n  ", parse.Diagnostics.Select(d => $"{d.Code}: {d.Title}")));
    }

    /// <summary>
    /// The check that would have caught `TS-P2-26` outright: every command the
    /// example names must actually exist.
    ///
    /// <para>The binder is deliberately <em>not</em> used for this, and finding
    /// out why was the point of controlling the test. <c>Binder.Bind</c> is a
    /// typo detector: it runs Levenshtein against the registry and, when nothing
    /// is close, returns silently on the grounds that the name may be a program on
    /// <c>$PATH</c> (<c>Binder.cs</c>, "Could be an external; defer silently").
    /// That is right for a shell and useless here — reintroducing the original
    /// <c>from-csv</c> did not raise a single binder diagnostic, because
    /// <c>from-csv</c> is not within an edit or two of anything. <c>case</c> was
    /// caught only because it is one edit from <c>cast</c>.</para>
    ///
    /// <para>A specification corpus can be stricter than a shell, because every
    /// command in it is supposed to be known. So the command names are collected
    /// from the syntax tree and each must be a registered command, a function the
    /// example declares itself, or one of the two named lists below.</para>
    /// </summary>
    [Theory]
    [MemberData(nameof(WorkedExamples))]
    public void A_worked_example_names_only_commands_that_exist(string title, string source)
    {
        var engine = new ToshEngine(_runtime.Language);
        var parse = engine.Parse(source, $"<spec:{title}>");

        var declared = CollectDeclaredFunctionNames(parse.Statement);

        var unknown = CollectCommandNames(parse.Statement)
            .Where(name =>
                !_runtime.Commands.TryGet(name, out _) &&
                !declared.Contains(name) &&
                !ExternalTools.Contains(name) &&
                !IllustrativePlaceholders.Contains(name))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.True(unknown.Length == 0,
            $"'{title}' names commands that do not exist: {string.Join(", ", unknown)}\n" +
            "If one is a real external tool add it to ExternalTools; if it is a stand-in " +
            "for the reader's own code, add it to IllustrativePlaceholders with a reason.");
    }

    /// <summary>
    /// Names that are stand-ins for the reader's own code rather than commands
    /// that should exist. Kept separate from <see cref="ExternalTools"/> so the
    /// two claims stay distinguishable: one says "this program exists", the other
    /// says "this is deliberately not real".
    /// </summary>
    private static readonly HashSet<string> IllustrativePlaceholders = new(StringComparer.Ordinal)
    {
        // Memoization with Records — the operation being memoized is the reader's.
        "some-slow-operation",
    };

    /// <summary>
    /// Every <see cref="CommandSyntax"/> head in the tree.
    ///
    /// Walked by reflection rather than by a hand-written switch over node types.
    /// A switch would silently miss a shape nobody remembered to add — which is
    /// the failure this whole item is about — whereas a reflective walk covers
    /// nodes added later for free.
    /// </summary>
    private static List<string> CollectCommandNames(object? node)
    {
        var names = new List<string>();
        Walk(node, names, new HashSet<object>(ReferenceEqualityComparer.Instance));
        return names;

        static void Walk(object? current, List<string> names, HashSet<object> seen)
        {
            switch (current)
            {
                case null or string:
                    return;
                case CommandSyntax command:
                    names.Add(command.Name);
                    break;
            }

            if (!seen.Add(current!))
            {
                return;
            }

            if (current is System.Collections.IEnumerable sequence)
            {
                foreach (var item in sequence)
                {
                    Walk(item, names, seen);
                }

                return;
            }

            // Only syntax nodes are descended into; a resolved IShellCommand or a
            // TextSpan has nothing to contribute and would widen the walk.
            if (current!.GetType().Namespace != typeof(CommandSyntax).Namespace)
            {
                return;
            }

            foreach (var property in current.GetType().GetProperties())
            {
                if (property.GetIndexParameters().Length != 0)
                {
                    continue;
                }

                Walk(property.GetValue(current), names, seen);
            }
        }
    }

    /// <summary>
    /// Functions an example declares for itself. `power-set` calling `power-set`
    /// is not an unknown command.
    /// </summary>
    private static HashSet<string> CollectDeclaredFunctionNames(object? node)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        Walk(node, names, new HashSet<object>(ReferenceEqualityComparer.Instance));
        return names;

        static void Walk(object? current, HashSet<string> names, HashSet<object> seen)
        {
            if (current is null or string || !seen.Add(current))
            {
                return;
            }

            if (current is FunctionDefinitionStatementSyntax function)
            {
                names.Add(function.Name);
            }

            if (current is System.Collections.IEnumerable sequence)
            {
                foreach (var item in sequence)
                {
                    Walk(item, names, seen);
                }

                return;
            }

            if (current.GetType().Namespace != typeof(CommandSyntax).Namespace)
            {
                return;
            }

            foreach (var property in current.GetType().GetProperties())
            {
                if (property.GetIndexParameters().Length == 0)
                {
                    Walk(property.GetValue(current), names, seen);
                }
            }
        }
    }

    // ── Executed against fixture data ────────────────────────────────────────
    //
    // Most of the chapter reads the machine it runs on — `ps`, `systemctl`,
    // `ls` — and cannot be asserted without asserting the runner's package
    // list. The two data-processing examples can, and they are the two the item
    // was filed about, so they run for real.
    //
    // The source comes from the specification rather than being copied here, so
    // editing the example edits the test. Only the file names are substituted;
    // every pipeline stage runs exactly as written.

    private static string SourceOf(string title)
    {
        var example = Harvest().SingleOrDefault(e => e.Title == title);
        Assert.False(example.Source is null, $"no worked example titled '{title}'");
        return example.Source;
    }

    private static async Task<IReadOnlyList<object?>> RunAsync(string source)
        => await new ToshEngine(ToshRuntime.CreateDefault().Language).ExecuteToListAsync(source);

    [Fact]
    public async Task The_CSV_example_filters_sorts_and_projects_its_fixture()
    {
        var dir = Directory.CreateTempSubdirectory("tosh-spec-csv");

        try
        {
            var input = Path.Combine(dir.FullName, "sales.csv");
            var output = Path.Combine(dir.FullName, "big-sales.csv");

            await File.WriteAllTextAsync(input,
                """
                Date,Customer,Amount,Region
                2026-01-03,Alice,150,North
                2026-01-04,Bob,80,South
                2026-01-05,Carol,220,North
                """);

            await RunAsync(SourceOf("CSV Processing")
                .Replace("cat sales.csv", $"cat {input}", StringComparison.Ordinal)
                .Replace("write-file big-sales.csv", $"write-file {output}", StringComparison.Ordinal));

            var written = (await File.ReadAllTextAsync(output)).Replace("\r", "").Trim();

            // Bob is filtered out at 80; Carol precedes Alice because the sort is
            // reversed; Region is dropped by the projection.
            Assert.Equal(
                """
                Date,Customer,Amount
                2026-01-05,Carol,220
                2026-01-03,Alice,150
                """.Replace("\r", ""),
                written);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task The_JSON_example_filters_projects_and_reserializes_its_fixture()
    {
        var dir = Directory.CreateTempSubdirectory("tosh-spec-json");

        try
        {
            var input = Path.Combine(dir.FullName, "api-response.json");

            await File.WriteAllTextAsync(input,
                """
                {"items":[
                  {"name":"beta","status":"active","created_at":"2026-02-01"},
                  {"name":"alpha","status":"inactive","created_at":"2026-01-01"},
                  {"name":"gamma","status":"active","created_at":"2026-01-15"}
                ]}
                """);

            var results = await RunAsync(SourceOf("JSON API Processing")
                .Replace("read-file api-response.json", $"read-file {input}", StringComparison.Ordinal));

            // alpha is dropped as inactive; gamma precedes beta by created date;
            // `created_at` is projected to `created`.
            Assert.Equal(
                """[{"name":"gamma","created":"2026-01-15"},{"name":"beta","created":"2026-02-01"}]""",
                results[^1]?.ToString());
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
