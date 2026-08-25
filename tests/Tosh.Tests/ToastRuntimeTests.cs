using Tosh.Language;
using Tosh.Runtime;
using Tosh.Stdlib.Clr;
using Tosh.Stdlib.Time;

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
        Assert.NotNull(language.SessionRedirection);
        Assert.Null(language.BackgroundJobs);
        Assert.Null(language.RuntimeNamespaceFactory);
        Assert.Null(language.EnvironmentExporter);
        Assert.Null(language.AutoCdCommandFactory);
        Assert.Null(language.ExternalCommands);
        Assert.Empty(language.InvocationArguments);
        Assert.Null(language.BlockExecutor);
        Assert.Null(language.Evaluator);
        Assert.False(string.IsNullOrEmpty(language.CurrentDirectory));

        language.HostSignals.RequestExit();
        language.HostSignals.SyncExportedEnvironmentVariable("probe", 1);
        language.HostSignals.RemoveExportedEnvironmentVariable("probe");
        Assert.False(language.HostSignals.ExitRequested);
        Assert.False(language.HostSignals.IsExported("probe"));
        Assert.True(language.Options.MaxRecursionDepth > 0);
    }

    [Fact]
    public async Task A_language_engine_runs_without_a_shell_runtime()
    {
        var language = new ToastRuntime();
        var engine = new ToshEngine(language);

        var results = await engine.ExecuteToListAsync("var x = 2\n$x + 3");

        Assert.Same(language, engine.LanguageRuntime);
        Assert.Null(engine.ShellRuntime);
        Assert.Equal(5, Assert.Single(results));
    }

    [Fact]
    public async Task A_language_engine_invokes_a_declared_function_without_a_shell_runtime()
    {
        var engine = new ToshEngine(new ToastRuntime());

        var results = await engine.ExecuteToListAsync(
            "func add(a: int, b: int) -> int { return ($a + $b) }\nadd 2 3");

        Assert.Equal(5, Assert.Single(results));
    }

    [Fact]
    public async Task A_language_engine_delegates_background_jobs_without_a_shell_runtime()
    {
        var jobs = new RecordingBackgroundJobHost();
        var observer = new RecordingExecutionObserver();
        var language = new ToastRuntime
        {
            BackgroundJobs = jobs,
            ExecutionObserver = observer,
        };
        language.Commands.RegisterOrReplace(new ExternalProcessProbeCommand());
        var engine = new ToshEngine(language);

        var results = await engine.ExecuteToListAsync("external-probe alpha &");

        Assert.Empty(results);
        Assert.Null(engine.ShellRuntime);
        var request = Assert.IsType<ToastBackgroundPipelineRequest>(jobs.Request);
        var stage = Assert.Single(request.Stages);
        Assert.Equal("/virtual/external-probe", stage.ResolvedPath);
        Assert.Equal("alpha", Assert.Single(stage.Arguments));
        Assert.Same(jobs.Result, observer.LastResult);
        Assert.Equal(0, observer.LastExitCode);
    }

    [Fact]
    public async Task An_unhosted_engine_reports_when_background_jobs_are_unavailable()
    {
        var engine = new ToshEngine(new ToastRuntime());

        var failure = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => engine.ExecuteToListAsync("external-probe &"));

        Assert.Equal("tosh.runtime.background_jobs_not_supported", failure.Diagnostics[0].Code);
    }

    [Fact]
    public async Task A_language_engine_accepts_a_host_runtime_namespace_without_a_shell_runtime()
    {
        var factory = new RecordingRuntimeNamespaceFactory();
        var language = new ToastRuntime
        {
            RuntimeNamespaceFactory = factory,
        };
        var engine = new ToshEngine(language);

        var results = await engine.ExecuteToListAsync("$tosh.Marker");

        Assert.Equal(42, Assert.Single(results));
        Assert.Null(engine.ShellRuntime);
        Assert.NotNull(factory.Script);
        Assert.NotNull(factory.Function);
    }

    [Fact]
    public async Task An_unhosted_engine_assigns_process_environment_without_a_shell_runtime()
    {
        var variableName = "TOAST_UNHOSTED_ENV_" + Guid.NewGuid().ToString("N");
        var engine = new ToshEngine(new ToastRuntime());

        try
        {
            await engine.ExecuteToListAsync($"$env.{variableName} = \"standalone\"");

            Assert.Equal("standalone", Environment.GetEnvironmentVariable(variableName));
            Assert.Null(engine.ShellRuntime);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, null);
        }
    }

    [Fact]
    public async Task Language_level_clr_commands_run_without_a_shell_runtime()
    {
        var language = new ToastRuntime();
        language.Commands.RegisterOrReplace(new NewObjectCommand());
        language.Commands.RegisterOrReplace(new CallCommand());
        var engine = new ToshEngine(language);

        var results = await engine.ExecuteToListAsync(
            "new System.Text.StringBuilder hello | call Append \" world\" | call ToString");

        Assert.Equal("hello world", Assert.Single(results));
        Assert.Null(engine.ShellRuntime);
    }

    [Fact]
    public async Task The_time_command_runs_a_language_block_without_a_shell_runtime()
    {
        var language = new ToastRuntime();
        language.Commands.RegisterOrReplace(new TimeCommand());
        var engine = new ToshEngine(language);

        var results = await engine.ExecuteToListAsync("time { 1 + 2 }");

        Assert.Equal(3, results[0]);
        Assert.IsType<CommandTimingInfo>(results[1]);
        Assert.Null(engine.ShellRuntime);
    }

    [Fact]
    public async Task The_shell_tracks_an_environment_assignment_as_an_export()
    {
        var variableName = "TOAST_HOSTED_ENV_" + Guid.NewGuid().ToString("N");
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);

        try
        {
            await engine.ExecuteToListAsync($"$env.{variableName} = \"hosted\"");

            Assert.Contains(variableName, runtime.ExportedEnvironmentVariables);
            Assert.Equal("hosted", runtime.Variables[variableName]);
            Assert.Equal("hosted", Environment.GetEnvironmentVariable(variableName));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, null);
        }
    }

    [Fact]
    public async Task A_language_engine_delegates_auto_cd_without_a_shell_runtime()
    {
        var root = Path.Combine(Path.GetTempPath(), $"toast-autocd-{Guid.NewGuid():N}");
        var target = Directory.CreateDirectory(Path.Combine(root, "target")).FullName;

        try
        {
            var factory = new RecordingAutoCdCommandFactory();
            var language = new ToastRuntime
            {
                AutoCdCommandFactory = factory,
                CurrentDirectory = root,
            };
            language.Options.AutoCd = true;
            var engine = new ToshEngine(language);

            var results = await engine.ExecuteToListAsync("target");

            Assert.Equal(target, Assert.Single(results));
            Assert.Equal(target, factory.ResolvedPath);
            Assert.Null(engine.ShellRuntime);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task An_unhosted_engine_reports_when_auto_cd_is_unavailable()
    {
        var root = Path.Combine(Path.GetTempPath(), $"toast-autocd-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "target"));

        try
        {
            var language = new ToastRuntime
            {
                CurrentDirectory = root,
            };
            language.Options.AutoCd = true;
            var engine = new ToshEngine(language);

            var failure = await Assert.ThrowsAsync<ToshDiagnosticException>(
                () => engine.ExecuteToListAsync("target"));

            Assert.Equal("tosh.runtime.auto_cd_not_supported", failure.Diagnostics[0].Code);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
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
        Assert.Same(runtime, runtime.Language.SessionRedirection);
        Assert.Same(runtime, runtime.Language.BackgroundJobs);
        Assert.Same(runtime, runtime.Language.RuntimeNamespaceFactory);
        Assert.Same(runtime, runtime.Language.EnvironmentExporter);
        Assert.Same(runtime, runtime.Language.AutoCdCommandFactory);
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

    [Fact]
    public void The_shell_runtime_namespace_is_published_from_the_runtime_assembly()
    {
        var runtime = ToshRuntime.CreateDefault();
        _ = new ToshEngine(runtime);

        Assert.NotNull(runtime.RuntimeNamespace);
        Assert.Equal(typeof(ToshRuntime).Assembly, runtime.RuntimeNamespace.GetType().Assembly);
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

    private sealed class RecordingBackgroundJobHost : IToastBackgroundJobHost
    {
        public object Result { get; } = new();

        public ToastBackgroundPipelineRequest? Request { get; private set; }

        public object StartExternalPipeline(ToastBackgroundPipelineRequest request)
        {
            Request = request;
            return Result;
        }
    }

    private sealed class RecordingExecutionObserver : IToastExecutionObserver
    {
        public object? LastResult { get; private set; }

        public int? LastExitCode { get; private set; }

        public void SetLastResult(object? value) => LastResult = value;

        public void SetLastExitCode(int exitCode) => LastExitCode = exitCode;
    }

    private sealed class ExternalProcessProbeCommand : IExternalProcessCommand
    {
        public string Name => "external-probe";

        public string Description => "Represents a host process without launching one.";

        public string Usage => "external-probe [args...]";

        public string ResolvedPath => "/virtual/external-probe";

        public async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class RecordingRuntimeNamespaceFactory : IToastRuntimeNamespaceFactory
    {
        public IToastScriptNamespace? Script { get; private set; }

        public IToastFunctionNamespace? Function { get; private set; }

        public IShellRecordObject CreateRuntimeNamespace(
            IToastScriptNamespace script,
            IToastFunctionNamespace function)
        {
            Script = script;
            Function = function;
            return MarkerRuntimeNamespace.Instance;
        }
    }

    private sealed class MarkerRuntimeNamespace : IShellRecordObject
    {
        public static MarkerRuntimeNamespace Instance { get; } = new();

        public string ShellTypeName => "TestRuntime";

        public bool TryGetMember(string name, out object? value, bool includeHidden = false)
        {
            if (name == "Marker")
            {
                value = 42;
                return true;
            }

            value = null;
            return false;
        }

        public bool TrySetMember(string name, object? value) => false;

        public IReadOnlyList<KeyValuePair<string, object?>> GetMembers(bool includeHidden = false)
            => [new("Marker", 42)];
    }

    private sealed class RecordingAutoCdCommandFactory : IToastAutoCdCommandFactory
    {
        public string? ResolvedPath { get; private set; }

        public IShellCommand CreateAutoCdCommand(string resolvedPath)
        {
            ResolvedPath = resolvedPath;
            return new AutoCdProbeCommand(resolvedPath);
        }
    }

    private sealed class AutoCdProbeCommand(string resolvedPath) : IShellCommand
    {
        public string Name => "cd";

        public string Description => "Records embedded-host AutoCd navigation.";

        public string Usage => "cd <path>";

        public async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
        {
            await Task.CompletedTask;
            yield return resolvedPath;
        }
    }
}
