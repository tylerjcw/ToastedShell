using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// Where a Tōast program's output goes — `TOAST-0015`, Phase A.
///
/// Redirection worked by swapping the *shell session's* `TextWriter`: save `Runtime.Output`,
/// put a composite writer in its place, restore in a `finally`. It works, and it means a
/// program that writes `run-report out> out.txt` needed a shell's stdout to exist before it
/// could redirect away from it.
///
/// The destination is now a value the **language** owns. The session's writer is one
/// destination among files, pipes and buffers rather than the thing being mutated — which is
/// what the first test here proves, by redirecting with no shell in the picture at all.
/// </summary>
public sealed class ToastStreamTests
{
    /// <summary>
    /// The test the item exists to pass: a host constructs a `ToastRuntime`, points its
    /// output at a file, and a program writes to it. No `ToshRuntime`, no session, no
    /// terminal, no `TextWriter`.
    /// </summary>
    [Fact]
    public void A_language_runtime_writes_to_a_file_with_no_shell()
    {
        var path = Path.Combine(Path.GetTempPath(), $"toast-stream-{Guid.NewGuid():N}.txt");

        try
        {
            var language = new ToastRuntime();
            using (var handle = ManagedFileHandle.OpenTextWrite(path, append: false))
            {
                language.Output = handle;
                language.Output.WriteTextLine("written with no session");
                language.Output.Flush();
            }

            Assert.Equal("written with no session", File.ReadAllText(path).TrimEnd());
        }
        finally
        {
            if (File.Exists(path)) { File.Delete(path); }
        }
    }

    /// <summary>
    /// A host that supplies no destination still runs a program that writes. "Nowhere to
    /// write" is a legitimate configuration for a `no_clr` program, not an error, so the
    /// default discards rather than throwing or being null.
    /// </summary>
    [Fact]
    public void A_language_runtime_has_somewhere_to_write_by_default()
    {
        var language = new ToastRuntime();

        Assert.NotNull(language.Output);
        Assert.NotNull(language.Error);

        language.Output.WriteTextLine("discarded");
        language.Error.WriteTextLine("discarded");
    }

    /// <summary>
    /// `ManagedFileHandle` *is* a destination rather than being wrapped in one. The item had
    /// assumed the concept needed inventing; it existed, and what was missing was that
    /// redirection and the file commands targeted different things.
    /// </summary>
    [Fact]
    public void A_file_handle_is_a_stream()
    {
        var path = Path.Combine(Path.GetTempPath(), $"toast-stream-{Guid.NewGuid():N}.txt");

        try
        {
            using (var handle = ManagedFileHandle.OpenTextWrite(path, append: false))
            {
                Assert.IsAssignableFrom<IToastStream>(handle);
                Assert.True(handle.CanWrite);
            }
        }
        finally
        {
            if (File.Exists(path)) { File.Delete(path); }
        }
    }

    /// <summary>
    /// The adapter that makes a session's writer one destination among many. This is the
    /// load-bearing piece: without it the shell's writer cannot *be* a destination, and
    /// redirecting means replacing it.
    /// </summary>
    [Fact]
    public void A_text_writer_can_be_a_destination()
    {
        var writer = new StringWriter();
        var stream = ToastStreams.FromWriter(writer);

        stream.WriteText("a");
        stream.WriteTextLine("b");
        stream.Flush();

        Assert.Equal("ab" + Environment.NewLine, writer.ToString());
    }

    /// <summary>
    /// `cmd out&gt; a out&gt; b` — one destination standing for several.
    /// </summary>
    [Fact]
    public void A_composite_writes_to_every_destination()
    {
        var first = new StringWriter();
        var second = new StringWriter();

        var composite = ToastStreams.Composite(
            [ToastStreams.FromWriter(first), ToastStreams.FromWriter(second)]);

        composite.WriteTextLine("both");
        composite.Flush();

        Assert.Equal("both" + Environment.NewLine, first.ToString());
        Assert.Equal("both" + Environment.NewLine, second.ToString());
    }

    /// <summary>
    /// A composite of one is that one. Worth pinning because the alternative — always
    /// wrapping — costs an indirection on the overwhelmingly common single-target case.
    /// </summary>
    [Fact]
    public void A_composite_of_one_is_that_one()
    {
        var only = ToastStreams.FromWriter(new StringWriter());

        Assert.Same(only, ToastStreams.Composite([only]));
    }

    /// <summary>
    /// The shell's writer and the language's destination are one thing seen twice, not two
    /// things kept in step by hand. Assigning the session's writer must be visible to the
    /// language, or a host that sets `Runtime.Output` would silently keep writing elsewhere.
    /// </summary>
    [Fact]
    public void Assigning_the_session_writer_is_visible_to_the_language()
    {
        var runtime = ToshRuntime.CreateDefault();
        var writer = new StringWriter();

        runtime.Output = writer;
        runtime.Language.Output.WriteTextLine("through the language");
        runtime.Language.Output.Flush();

        Assert.Equal("through the language" + Environment.NewLine, writer.ToString());
    }

    /// <summary>
    /// And end to end: redirection writes through the language's destination, and restores
    /// it afterwards.
    /// </summary>
    [Fact]
    public async Task Redirection_writes_through_the_language_destination_and_restores_it()
    {
        var path = Path.Combine(Path.GetTempPath(), $"toast-stream-{Guid.NewGuid():N}.txt");

        try
        {
            var runtime = ToshRuntime.CreateDefault();
            var engine = new ToshEngine(runtime);
            var before = runtime.Language.Output;

            await engine.ExecuteToListAsync($"echo \"redirected\" out> \"{path}\"");

            Assert.Equal("redirected", File.ReadAllText(path).TrimEnd());
            Assert.Same(before, runtime.Language.Output);
        }
        finally
        {
            if (File.Exists(path)) { File.Delete(path); }
        }
    }

    /// <summary>
    /// The evaluator no longer reaches through its compatibility <c>Runtime</c> property
    /// while redirecting. This would throw before the session-redirection port existed.
    /// </summary>
    [Fact]
    public async Task An_unhosted_engine_redirects_without_a_shell_runtime()
    {
        var path = Path.Combine(Path.GetTempPath(), $"toast-stream-{Guid.NewGuid():N}.txt");

        try
        {
            var language = new ToastRuntime();
            language.Commands.RegisterOrReplace(new LanguageEmitCommand());
            var engine = new ToshEngine(language);
            var before = language.Output;

            await engine.ExecuteToListAsync($"language-emit out> \"{path}\"");

            Assert.Equal("standalone", File.ReadAllText(path).TrimEnd());
            Assert.Same(before, language.Output);
        }
        finally
        {
            if (File.Exists(path)) { File.Delete(path); }
        }
    }

    /// <summary>
    /// Shell-provided commands still follow redirection, and disposal restores the exact
    /// session writer that was present before the pipeline.
    /// </summary>
    [Fact]
    public async Task Shell_session_redirection_is_scoped_and_restored()
    {
        var outputPath = Path.Combine(Path.GetTempPath(), $"toast-stream-{Guid.NewGuid():N}.txt");
        var errorPath = Path.Combine(Path.GetTempPath(), $"toast-stream-{Guid.NewGuid():N}.err");
        var sessionOutput = new StringWriter();
        var sessionError = new StringWriter();
        var runtime = new ToshRuntime(sessionOutput, sessionError);
        runtime.Commands.RegisterOrReplace(new SessionWriteCommand());
        var engine = new ToshEngine(runtime);

        try
        {
            await engine.ExecuteToListAsync(
                $"session-write out> \"{outputPath}\" err> \"{errorPath}\"");

            Assert.Equal("shell output", File.ReadAllText(outputPath).TrimEnd());
            Assert.Equal("shell error", File.ReadAllText(errorPath).TrimEnd());
            Assert.Equal(string.Empty, sessionOutput.ToString());
            Assert.Equal(string.Empty, sessionError.ToString());
            Assert.Same(sessionOutput, runtime.Output);
            Assert.Same(sessionError, runtime.Error);
        }
        finally
        {
            if (File.Exists(outputPath)) { File.Delete(outputPath); }
            if (File.Exists(errorPath)) { File.Delete(errorPath); }
        }
    }

    private sealed class LanguageEmitCommand : IShellCommand
    {
        public string Name => "language-emit";

        public string Description => "Emits a value without requiring a shell session.";

        public string Usage => "language-emit";

        public async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
        {
            await Task.CompletedTask;
            yield return new ShellTextLine("standalone");
        }
    }

    private sealed class SessionWriteCommand : IShellCommand
    {
        public string Name => "session-write";

        public string Description => "Writes through the shell session for a boundary test.";

        public string Usage => "session-write";

        public async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
        {
            await context.Runtime.Output.WriteLineAsync("shell output");
            await context.Runtime.Error.WriteLineAsync("shell error");
            yield break;
        }
    }
}
