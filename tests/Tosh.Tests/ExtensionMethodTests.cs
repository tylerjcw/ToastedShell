using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// <c>extend</c> adds methods to a type the author does not own.
///
/// `TS-P3-27`. The user's own `Graphics.tosh` documented the absence: *"Helpers are
/// free functions rather than methods because ToastScript has no extension-method
/// dispatch: `$color.ToGl()` cannot work, `Gfx.ToGl($color)` can."* For a language
/// built on object pipelines, not being able to attach a verb to a type is the
/// sharpest structural limit there was.
///
/// The rule is that an extension can only ever *add* a name. Dispatch reaches it
/// last — after the receiver's own methods, after inherited ones, after a callable
/// held in a property (`TS-P2-93`) — so no existing call can change meaning, and
/// that is what makes the feature safe to add to a language with running code.
///
/// It hooks the invoker rather than the engine's several call sites, because the
/// invoker is the one place every instance call passes through. The engine supplies
/// the lookup, since extensions are lexically scoped and arrive with imported
/// modules — neither of which the invoker can see.
/// </summary>
public class ExtensionMethodTests
{
    private static async Task<string> RunAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var results = await engine.ExecuteToListAsync(source);
        return results.Count == 0 ? string.Empty : results[^1]?.ToString() ?? "null";
    }

    private const string ShoutString = """
        extend string {
            func Shout() -> string => ($this.ToUpper() + "!")
            func Repeat(n: int) -> string => (1..$n | map { $this } | join "")
        }

        """;

    /// <summary>The receiver is bound to `$this`, and the method is found on a CLR type.</summary>
    [Fact]
    public async Task An_extension_is_found_on_a_clr_type()
        => Assert.Equal("HI!", await RunAsync(ShoutString + "\"hi\".Shout()"));

    /// <summary>Extensions take arguments like any method.</summary>
    [Fact]
    public async Task An_extension_takes_arguments()
        => Assert.Equal("ababab", await RunAsync(ShoutString + "\"ab\".Repeat(3)"));

    /// <summary>And on a ToastScript class, which dispatches through its own path.</summary>
    [Fact]
    public async Task An_extension_is_found_on_a_tosh_class()
        => Assert.Equal("6", await RunAsync(
            """
            class P { prop X: int = 3 }
            extend P {
                func Doubled() -> int => ($this.X * 2)
            }
            (new P()).Doubled()
            """));

    /// <summary>
    /// The rule that makes this safe: a real member always wins. If an extension
    /// could shadow one, adding a library could silently change what existing code
    /// does.
    /// </summary>
    [Fact]
    public async Task A_real_member_wins_over_an_extension()
        => Assert.Equal("HI", await RunAsync(
            """
            extend string {
                func ToUpper() -> string => "extension ran"
            }
            "hi".ToUpper()
            """));

    /// <summary>
    /// The same rule for a class's own method, which reaches dispatch by a different
    /// route entirely.
    /// </summary>
    [Fact]
    public async Task A_class_method_wins_over_an_extension()
        => Assert.Equal("own", await RunAsync(
            """
            class P { func Which() -> string => "own" }
            extend P { func Which() -> string => "extension" }
            (new P()).Which()
            """));

    /// <summary>
    /// An extension declared inside a module and reached through `require` — the
    /// visibility rule, and the reason registration happens when the declaration
    /// executes rather than at parse time.
    /// </summary>
    [Fact]
    public async Task An_extension_arrives_with_an_imported_module()
    {
        var directory = Directory.CreateTempSubdirectory("tosh-extend-").FullName;

        try
        {
            var library = Path.Combine(directory, "kit.tosh");
            await File.WriteAllTextAsync(library,
                """
                export partial module StrKit {
                    extend string {
                        func Titlecase() -> string => ($this.Substring(0, 1).ToUpper() + $this.Substring(1))
                    }
                }
                """);

            Assert.Equal("Hello", await RunAsync(
                $"require StrKit from \"{library}\" as SK\n\"hello\".Titlecase()"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// An extension has nowhere to put state, so it can only add behaviour. Saying so
    /// at the declaration is better than accepting a property that could never be
    /// read.
    /// </summary>
    [Fact]
    public async Task An_extension_cannot_declare_state()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var exception = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => engine.ExecuteToListAsync("extend string { prop X: int = 1 }"));

        Assert.Equal(
            "tosh.runtime.extend_member_not_a_method",
            Assert.Single(exception.Diagnostics).Code);
    }

    /// <summary>
    /// A method that exists nowhere still fails, and says so — the fallback must not
    /// swallow a genuine mistake.
    /// </summary>
    [Fact]
    public async Task A_method_that_exists_nowhere_still_fails()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);

        await Assert.ThrowsAnyAsync<Exception>(
            () => engine.ExecuteToListAsync(ShoutString + "\"hi\".NoSuchMethod()"));
    }

    /// <summary>
    /// `extends` is the inheritance clause and `extend` the declaration; they are
    /// told apart by the whole word, so this still means what it always did.
    /// </summary>
    [Fact]
    public async Task The_extends_clause_is_unaffected()
        => Assert.Equal("5", await RunAsync(
            """
            class Base { prop V: int = 5 }
            class Leaf extends Base { }
            (new Leaf()).V
            """));
}
