using Tosh.Runtime;

namespace Tosh.Cli;

internal static class CliInvocationResolver
{
    public static CliInvocationPlan Resolve(string[] args, string currentDirectory)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentDirectory);

        var skipStartup = false;
        var skipProfile = false;
        var safeMode = false;
        var profileStartup = false;
        var isLoginShell = DetectLoginShellFromArgv0();
        var effectiveArgs = new List<string>(args.Length);

        foreach (var argument in args)
        {
            switch (argument)
            {
                case "--no-startup":
                    skipStartup = true;
                    continue;
                case "--no-profile":
                    skipProfile = true;
                    continue;
                case "--safe":
                    safeMode = true;
                    continue;
                case "--login" or "-l":
                    isLoginShell = true;
                    continue;
                case "--profile-startup":
                    profileStartup = true;
                    continue;
            }

            effectiveArgs.Add(argument);
        }

        if (safeMode)
        {
            skipStartup = true;
        }

        if (effectiveArgs.Count == 0)
        {
            return CliInvocationPlan.Repl(skipStartup, skipProfile, isLoginShell, safeMode, profileStartup);
        }

        if (IsHelpSwitch(effectiveArgs[0]))
        {
            return CliInvocationPlan.Help(skipStartup, profileStartup);
        }

        if (effectiveArgs[0] is "--version" or "-V")
        {
            return CliInvocationPlan.Version(profileStartup);
        }

        if (effectiveArgs[0] is "--compile" or "-C")
        {
            if (effectiveArgs.Count < 2)
            {
                throw new InvalidOperationException("The '--compile'/'-C' flag requires at least one script path.");
            }

            var inputPaths = new List<string>();
            string? outputPath = null;
            string? profile = null;
            var allowDynamic = false;
            var emitRefasm = false;
            var emitAppHost = true;
            var publishSingleFile = false;

            for (var i = 1; i < effectiveArgs.Count; i++)
            {
                switch (effectiveArgs[i])
                {
                    case "-o" or "--output" when i + 1 < effectiveArgs.Count:
                        outputPath = PathUtilities.ResolvePath(currentDirectory, effectiveArgs[++i]);
                        break;
                    case "--profile" when i + 1 < effectiveArgs.Count:
                        profile = effectiveArgs[++i];
                        break;
                    case "--compile-allow-dynamic" or "--allow-dynamic":
                        allowDynamic = true;
                        break;
                    case "--emit-refasm":
                        emitRefasm = true;
                        break;
                    case "--no-apphost":
                        emitAppHost = false;
                        break;
                    case "--publish-single-file":
                        publishSingleFile = true;
                        break;
                    default:
                        if (effectiveArgs[i].StartsWith("--profile="))
                        {
                            profile = effectiveArgs[i]["--profile=".Length..];
                            break;
                        }
                        if (effectiveArgs[i].StartsWith('-'))
                        {
                            throw new InvalidOperationException($"Unknown option '{effectiveArgs[i]}' for --compile.");
                        }
                        inputPaths.Add(PathUtilities.ResolvePath(currentDirectory, effectiveArgs[i]));
                        break;
                }
            }

            if (inputPaths.Count == 0)
            {
                throw new InvalidOperationException("The '--compile'/'-C' flag requires at least one script path.");
            }

            return CliInvocationPlan.Compile(inputPaths.ToArray(), outputPath, profile, allowDynamic, emitRefasm, emitAppHost, publishSingleFile);
        }

        if (effectiveArgs[0] is "--export-command-metadata" or "--dump-builtins")
        {
            var format = "json";
            string? outputPath = null;

            for (var i = 1; i < effectiveArgs.Count; i++)
            {
                switch (effectiveArgs[i])
                {
                    case "--latex":
                        format = "latex";
                        break;
                    case "--json":
                        format = "json";
                        break;
                    case "--vscode":
                        format = "vscode";
                        break;
                    case "--surface":
                        format = "surface";
                        break;
                    case "-o" or "--output" when i + 1 < effectiveArgs.Count:
                        outputPath = effectiveArgs[++i];
                        break;
                    default:
                        throw new InvalidOperationException($"Unknown option '{effectiveArgs[i]}' for --export-command-metadata.");
                }
            }

            return CliInvocationPlan.ExportMetadata(format, outputPath, profileStartup);
        }

        if (effectiveArgs[0] is "-c" or "--command")
        {
            if (effectiveArgs.Count < 2)
            {
                throw new InvalidOperationException("The '-c'/'--command' flag requires a command string.");
            }

            return CliInvocationPlan.Command(effectiveArgs[1], effectiveArgs.Skip(2).ToArray(), skipStartup, skipProfile, isLoginShell, profileStartup);
        }

        var stopFlagParsing = effectiveArgs[0] == "--";
        var invocationArgs = stopFlagParsing
            ? effectiveArgs.Skip(1).ToArray()
            : effectiveArgs.ToArray();

        if (invocationArgs.Length == 0)
        {
            throw new InvalidOperationException("Expected a command or script path after '--'.");
        }

        if (TryResolveFileInvocation(invocationArgs, currentDirectory, out var plan, skipStartup, profileStartup))
        {
            return plan with { LoadStartup = !skipStartup, SkipProfile = skipProfile, IsLoginShell = isLoginShell };
        }

        if (!stopFlagParsing && invocationArgs[0].StartsWith("-", StringComparison.Ordinal) && invocationArgs[0] != "-")
        {
            throw new InvalidOperationException($"Unknown option '{invocationArgs[0]}'. Use '--' before a command or script path that begins with '-'.");
        }

        return CliInvocationPlan.Command(BuildScript(invocationArgs), Array.Empty<string>(), skipStartup, skipProfile, isLoginShell, profileStartup);
    }

    internal static bool TryResolveFileInvocation(string[] arguments, string currentDirectory, out CliInvocationPlan plan, bool skipStartup = false, bool profileStartup = false)
    {
        plan = default;

        if (arguments.Length == 0)
        {
            return false;
        }

        var candidate = PathUtilities.ResolvePath(currentDirectory, arguments[0]);

        if (!File.Exists(candidate))
        {
            return false;
        }

        var shebang = ScriptFileDetection.ReadShebang(candidate);

        if (shebang?.IsTosh == true)
        {
            plan = CliInvocationPlan.ToshScript(candidate, arguments.Skip(1).ToArray(), skipStartup, profileStartup);
            return true;
        }

        if (shebang is { } shebangInfo)
        {
            var invocation = shebangInfo.CommandTokens
                .Concat([candidate])
                .Concat(arguments.Skip(1))
                .ToArray();
            plan = CliInvocationPlan.ExternalScript(candidate, invocation, skipStartup, profileStartup);
            return true;
        }

        if (ScriptFileDetection.IsToshScript(candidate))
        {
            plan = CliInvocationPlan.ToshScript(candidate, arguments.Skip(1).ToArray(), skipStartup, profileStartup);
            return true;
        }

        return false;
    }

    private static bool IsHelpSwitch(string argument) => argument is "--help" or "-h";

    private static string BuildScript(string[] arguments)
    {
        if (arguments.Length == 1)
        {
            return arguments[0];
        }

        return string.Join(" ", arguments.Select(QuoteArgument));
    }

    private static string QuoteArgument(string argument)
    {
        if (argument.Length == 0 || argument.Any(character => char.IsWhiteSpace(character) || character is '"' or '|' or '#'))
        {
            return $"\"{argument.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
        }

        return argument;
    }

    /// <summary>
    /// Detects login shell invocation via the Unix argv[0] convention.
    /// When login/sshd spawns a login shell, argv[0] is set to "-shellname" (e.g. "-tosh").
    /// </summary>
    private static bool DetectLoginShellFromArgv0()
    {
        try
        {
            var commandLineArgs = Environment.GetCommandLineArgs();

            if (commandLineArgs.Length > 0)
            {
                var argv0 = Path.GetFileName(commandLineArgs[0]);
                return argv0.StartsWith('-');
            }
        }
        catch
        {
            // Defensive — don't let argv inspection break startup.
        }

        return false;
    }
}

internal readonly record struct CliInvocationPlan(
    CliInvocationKind Kind,
    string? ScriptOrCommand,
    string[] Arguments,
    bool LoadStartup = true,
    bool SkipProfile = false,
    bool IsLoginShell = false,
    bool SafeMode = false,
    bool ProfileStartup = false,
    string? CompileProfileName = null,
    bool CompileAllowDynamic = false,
    bool EmitRefasm = false,
    bool EmitAppHost = true,
    bool PublishSingleFile = false)
{
    public static CliInvocationPlan Repl(bool skipStartup = false, bool skipProfile = false, bool isLoginShell = false, bool safeMode = false, bool profileStartup = false) =>
        new(CliInvocationKind.Repl, null, Array.Empty<string>(), LoadStartup: !skipStartup, SkipProfile: skipProfile, IsLoginShell: isLoginShell, SafeMode: safeMode, ProfileStartup: profileStartup);

    public static CliInvocationPlan Help(bool skipStartup = false, bool profileStartup = false) => new(CliInvocationKind.Help, null, Array.Empty<string>(), LoadStartup: !skipStartup, ProfileStartup: profileStartup);

    public static CliInvocationPlan Command(string command, string[] arguments, bool skipStartup = false, bool skipProfile = false, bool isLoginShell = false, bool profileStartup = false) =>
        new(CliInvocationKind.Command, command, arguments, LoadStartup: !skipStartup, SkipProfile: skipProfile, IsLoginShell: isLoginShell, ProfileStartup: profileStartup);

    public static CliInvocationPlan ToshScript(string path, string[] arguments, bool skipStartup = false, bool profileStartup = false) => new(CliInvocationKind.ToshScript, path, arguments, LoadStartup: !skipStartup, ProfileStartup: profileStartup);

    public static CliInvocationPlan ExternalScript(string path, string[] invocation, bool skipStartup = false, bool profileStartup = false) => new(CliInvocationKind.ExternalScript, path, invocation, LoadStartup: !skipStartup, ProfileStartup: profileStartup);

    public static CliInvocationPlan ExportMetadata(string format, string? outputPath, bool profileStartup = false) => new(CliInvocationKind.ExportMetadata, format, outputPath is not null ? [outputPath] : Array.Empty<string>(), LoadStartup: false, ProfileStartup: profileStartup);

    public static CliInvocationPlan Compile(string[] inputPaths, string? outputPath, string? compileProfileName = null, bool compileAllowDynamic = false, bool emitRefasm = false, bool emitAppHost = true, bool publishSingleFile = false) =>
        new(CliInvocationKind.Compile, outputPath, inputPaths, LoadStartup: false, CompileProfileName: compileProfileName, CompileAllowDynamic: compileAllowDynamic, EmitRefasm: emitRefasm, EmitAppHost: emitAppHost, PublishSingleFile: publishSingleFile);

    public static CliInvocationPlan Version(bool profileStartup = false) => new(CliInvocationKind.Version, null, Array.Empty<string>(), LoadStartup: false, ProfileStartup: profileStartup);
}

internal enum CliInvocationKind
{
    Repl,
    Help,
    Version,
    Command,
    ToshScript,
    ExternalScript,
    ExportMetadata,
    Compile,
}
