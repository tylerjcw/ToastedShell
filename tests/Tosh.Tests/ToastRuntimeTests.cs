using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// The language's runtime, and the shell's view of it.
///
/// `TOAST-0006`, stage 2d. `ToshRuntime` carries 38 public members; the language touches
/// 23. `ToastRuntime` holds the language's half and `ToshRuntime` **composes** one — it
/// does not inherit from it and does not copy it.
///
/// Composition was chosen because with a base class every member added later needs a
/// judgement about which class it lands in, and that judgement is what produced
/// `Config.Shell.MaxRecursionDepth` — a limit on the evaluator filed under "Shell".
/// Composition asks "does the language need this?", which is a question with a checkable
/// answer.
/// </summary>
public sealed class ToastRuntimeTests
{
    /// <summary>
    /// The test the whole item exists to pass: a host constructs a `ToastRuntime` and
    /// nothing else. No shell, no config file, no display stack, no terminal.
    /// </summary>
    [Fact]
    public void A_language_runtime_stands_alone()
    {
        var language = new ToastRuntime();

        Assert.NotNull(language.Invoker);
        Assert.NotNull(language.ObjectAccessor);
        Assert.NotNull(language.TypeResolver);
        Assert.NotNull(language.Options);
        Assert.NotNull(language.Variables);
        Assert.NotNull(language.Classes);
        Assert.NotNull(language.Modules);
        Assert.NotNull(language.LoadedModules);
        Assert.NotNull(language.NativeTypes);
        Assert.NotNull(language.Events);
        Assert.NotNull(language.Commands);
        Assert.NotNull(language.HostSignals);
        Assert.NotNull(language.Diagnostics);
        Assert.NotNull(language.ExecutionObserver);
        Assert.Null(language.ExternalCommands);
        Assert.Empty(language.InvocationArguments);
        Assert.Null(language.BlockExecutor);
        Assert.Null(language.Evaluator);
        Assert.False(string.IsNullOrEmpty(language.CurrentDirectory));

        language.HostSignals.RequestExit();
        Assert.False(language.HostSignals.ExitRequested);
        Assert.False(language.HostSignals.IsExported("probe"));
        Assert.True(language.Options.MaxRecursionDepth > 0);
    }

    /// <summary>
    /// The language runtime names contracts, not the .NET reflection implementations.
    /// A host may replace all three services while constructing the runtime; the default
    /// remains the current CLR implementation.
    /// </summary>
    [Fact]
    public void Object_services_are_host_substitution_points()
    {
        var invoker = new ReflectionInvoker();
        var accessor = new ReflectionObjectAccessor();
        var resolver = new DotNetTypeResolver();

        var language = new ToastRuntime
        {
            Invoker = invoker,
            ObjectAccessor = accessor,
            TypeResolver = resolver,
        };

        Assert.Equal(typeof(IObjectInvoker),
            typeof(ToastRuntime).GetProperty(nameof(ToastRuntime.Invoker))!.PropertyType);
        Assert.Same(invoker, language.Invoker);
        Assert.Same(accessor, language.ObjectAccessor);
        Assert.Same(resolver, language.TypeResolver);
    }

    /// <summary>
    /// The shell's members are the *same objects*, not copies of them.
    /// </summary>
    /// <remarks>
    /// This is the assertion that matters. Delegation and duplication both compile, both
    /// read back correctly in a casual test, and differ only when something writes
    /// through one route and reads through the other — which is exactly what
    /// `$tosh.Vars` does. Reference equality is the only check that tells them apart.
    /// </remarks>
    [Fact]
    public void The_shell_view_is_the_same_state_not_a_copy()
    {
        var runtime = ToshRuntime.CreateDefault();

        Assert.Same(runtime.Language.Variables, runtime.Variables);
        Assert.Same(runtime.Language.Classes, runtime.Classes);
        Assert.Same(runtime.Language.Modules, runtime.Modules);
        Assert.Same(runtime.Language.LoadedModules, runtime.LoadedModules);
        Assert.Same(runtime.Language.NativeTypes, runtime.NativeTypes);
        Assert.Same(runtime.Language.Options, runtime.Options);
        Assert.Same(runtime.Language.Invoker, runtime.Invoker);
        Assert.Same(runtime.Language.ObjectAccessor, runtime.ObjectAccessor);
        Assert.Same(runtime.Language.TypeResolver, runtime.TypeResolver);
        Assert.Same(runtime.Language.Events, runtime.Events);
        Assert.Same(runtime, runtime.Language.HostSignals);
        Assert.Same(runtime, runtime.Language.Diagnostics);
        Assert.Same(runtime, runtime.Language.ExecutionObserver);
        Assert.Same(runtime.ExternalCommands, runtime.Language.ExternalCommands);
        Assert.Same(runtime.InvocationArguments, runtime.Language.InvocationArguments);
        Assert.Same(runtime.BlockExecutor, runtime.Language.BlockExecutor);
        Assert.Same(runtime.Evaluator, runtime.Language.Evaluator);

        // One table, two views — the shell keeps the registry's extra members while the
        // language sees only ICommandTable, but it must be the same instance or
        // `export func` and `which` would disagree.
        Assert.Same(runtime.Language.Commands, runtime.Commands);

        runtime.CurrentDirectory = "/tmp";
        Assert.Equal("/tmp", runtime.Language.CurrentDirectory);
    }

    /// <summary>
    /// And behaviourally: a variable declared through the engine is visible on the
    /// language runtime, because there is only one dictionary.
    /// </summary>
    [Fact]
    public async Task A_global_declared_from_script_lands_in_the_language_runtime()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);

        Assert.Same(runtime.Language, engine.LanguageRuntime);

        await engine.ExecuteToListAsync("global var probe = 41");

        Assert.True(runtime.Language.Variables.ContainsKey("probe"));
    }

    /// <summary>
    /// Composition, not inheritance — asserted because the alternative would satisfy
    /// every other test here while reintroducing the "which half does this belong to?"
    /// question that the choice exists to avoid.
    /// </summary>
    [Fact]
    public void The_shell_runtime_composes_the_language_runtime_rather_than_deriving_from_it()
    {
        Assert.False(typeof(ToastRuntime).IsAssignableFrom(typeof(ToshRuntime)));
        Assert.NotNull(typeof(ToshRuntime).GetProperty(nameof(ToshRuntime.Language)));
    }
}
