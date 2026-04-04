using Tosh.Core;

namespace Tosh.Cli;

internal static class CliInvocationResolver
{
    public static CliInvocationPlan Resolve(string[] args, string currentDirectory)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentDirectory);

        var skipStartup = false;
        var skipProfile = false;
        var isLoginShell = false;
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
                case "--login" or "-l":
                    isLoginShell = true;
                    continue;
            }

            effectiveArgs.Add(argument);
        }

        if (effectiveArgs.Count == 0)
        {
            return CliInvocationPlan.Repl(skipStartup, skipProfile, isLoginShell);
        }

        if (IsHelpSwitch(effectiveArgs[0]))
        {
            return CliInvocationPlan.Help(skipStartup);
        }

        if (effectiveArgs[0] is "-c" or "--command")
        {
            if (effectiveArgs.Count < 2)
            {
                throw new InvalidOperationException("The '-c'/'--command' flag requires a command string.");
            }

            return CliInvocationPlan.Command(effectiveArgs[1], effectiveArgs.Skip(2).ToArray(), skipStartup, skipProfile, isLoginShell);
        }

        var stopFlagParsing = effectiveArgs[0] == "--";
        var invocationArgs = stopFlagParsing
            ? effectiveArgs.Skip(1).ToArray()
            : effectiveArgs.ToArray();

        if (invocationArgs.Length == 0)
        {
            throw new InvalidOperationException("Expected a command or script path after '--'.");
        }

        if (TryResolveFileInvocation(invocationArgs, currentDirectory, out var plan))
        {
            return plan with { LoadStartup = !skipStartup, SkipProfile = skipProfile, IsLoginShell = isLoginShell };
        }

        if (!stopFlagParsing && invocationArgs[0].StartsWith("-", StringComparison.Ordinal) && invocationArgs[0] != "-")
        {
            throw new InvalidOperationException($"Unknown option '{invocationArgs[0]}'. Use '--' before a command or script path that begins with '-'.");
        }

        return CliInvocationPlan.Command(BuildScript(invocationArgs), Array.Empty<string>(), skipStartup, skipProfile, isLoginShell);
    }

    internal static bool TryResolveFileInvocation(string[] arguments, string currentDirectory, out CliInvocationPlan plan)
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
            plan = CliInvocationPlan.ToshScript(candidate, arguments.Skip(1).ToArray());
            return true;
        }

        if (shebang is { } shebangInfo)
        {
            var invocation = shebangInfo.CommandTokens
                .Concat([candidate])
                .Concat(arguments.Skip(1))
                .ToArray();
            plan = CliInvocationPlan.ExternalScript(candidate, invocation);
            return true;
        }

        if (ScriptFileDetection.IsToshScript(candidate))
        {
            plan = CliInvocationPlan.ToshScript(candidate, arguments.Skip(1).ToArray());
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
}

internal readonly record struct CliInvocationPlan(
    CliInvocationKind Kind,
    string? ScriptOrCommand,
    string[] Arguments,
    bool LoadStartup = true,
    bool SkipProfile = false,
    bool IsLoginShell = false)
{
    public static CliInvocationPlan Repl(bool skipStartup = false, bool skipProfile = false, bool isLoginShell = false) =>
        new(CliInvocationKind.Repl, null, Array.Empty<string>(), LoadStartup: !skipStartup, SkipProfile: skipProfile, IsLoginShell: isLoginShell);

    public static CliInvocationPlan Help(bool skipStartup = false) => new(CliInvocationKind.Help, null, Array.Empty<string>(), LoadStartup: !skipStartup);

    public static CliInvocationPlan Command(string command, string[] arguments, bool skipStartup = false, bool skipProfile = false, bool isLoginShell = false) =>
        new(CliInvocationKind.Command, command, arguments, LoadStartup: !skipStartup, SkipProfile: skipProfile, IsLoginShell: isLoginShell);

    public static CliInvocationPlan ToshScript(string path, string[] arguments, bool skipStartup = false) => new(CliInvocationKind.ToshScript, path, arguments, LoadStartup: !skipStartup);

    public static CliInvocationPlan ExternalScript(string path, string[] invocation, bool skipStartup = false) => new(CliInvocationKind.ExternalScript, path, invocation, LoadStartup: !skipStartup);
}

internal enum CliInvocationKind
{
    Repl,
    Help,
    Command,
    ToshScript,
    ExternalScript,
}
