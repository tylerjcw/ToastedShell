using Tosh.Runtime;

namespace Tosh.Cli;

/// <summary>
/// The shell's own help page, described as a <see cref="HelpTopic"/> and drawn by the
/// same renderer <c>help &lt;name&gt;</c> uses.
/// </summary>
/// <remarks>
/// <para>
/// It used to be fifty <c>Console.Out.WriteLineAsync</c> calls of hand-aligned columns.
/// The shell that renders every other command's help as a panel printed its own as flat
/// text, which is the one place the inconsistency is most visible.
/// </para>
/// <para>
/// The compilation and metadata-export flags stay in the options table rather than a
/// footer. <c>TOAST-0003</c> is on record that they "existed and were undiscoverable",
/// and moving them out of the table someone scans would undo that.
/// </para>
/// </remarks>
internal static class ToshHelp
{
    public static HelpTopic Topic(string version) => new(
        Name: "tosh",
        Kind: HelpSubjectKind.External,
        Category: "Shell",
        Description: $"TōSh {version} — a shell whose pipelines carry .NET objects rather than text.",
        Usage: """
            tosh
            tosh -c 'echo hello | type-of'
            tosh script.tosh [args...]
            tosh ./script-with-shebang [args...]
            tosh -- <command-or-script-starting-with-dash> [args...]
            """,
        Aliases: Array.Empty<string>(),
        Related: ["help", "config", "crumb"],
        Examples: Array.Empty<string>(),
        Path: Environment.ProcessPath,
        Notes: "Startup reads config.tosh, then profile.tosh, then autoload. "
             + "--no-profile skips the second; --no-startup skips all three; "
             + "--safe skips them and guarantees a recoverable prompt.",
        Arguments: Arguments,
        Options: Options,
        PipelineInput: null,
        Output: "Whatever the command produced — objects to a pipe, a rendered view to a terminal.",
        ExampleItems: Examples,
        Streaming: null,
        Subcommands: null);

    private static readonly IReadOnlyList<HelpArgumentInfo> Arguments =
    [
        new("script", "A .tosh script, or any file with a shebang. Arguments after it are the script's.", Required: false),
    ];

    private static readonly IReadOnlyList<HelpOptionInfo> Options =
    [
        new("-h, --help", "Show this help."),
        new("-V, --version", "Print the version and exit."),
        new("-c, --command <text>", "Run one command string and exit."),
        new("-l, --login", "Start as a login shell."),
        new("--no-startup", "Skip config.tosh, profile.tosh and autoload."),
        new("--no-profile", "Skip profile.tosh only; config.tosh and autoload still load."),
        new("--safe", "Safe mode: skip all startup, with a guaranteed recovery prompt."),
        new("--profile-startup", "Show a startup phase timing breakdown."),
        new("--diagnostics=<mode>", "Diagnostic output mode: text, plain or json.", "text"),
        new("--", "Stop flag parsing, so the next argument is a name and not an option."),

        new("--compile <file...>", "Compile scripts to an assembly. The output path comes from the first input unless -o is given."),
        new("-o, --output <path>", "Write the compiled assembly here."),
        new("--no-apphost", "Emit only the assembly, without a native launcher."),
        new("--publish-single-file", "Emit a self-contained single-file executable."),
        new("--emit-refasm", "Emit a reference assembly beside the output."),
        new("--compile-allow-dynamic", "Permit dynamic fallbacks the compiler would otherwise refuse."),

        new("--export-command-metadata", "Write the command metadata and exit."),
        new("--json | --latex | --vscode", "Format for that export.", "json"),
        new("--surface", "Export the language surface registry."),
        new("--dump-builtins", "List the built-in commands and exit."),
    ];

    private static readonly IReadOnlyList<HelpExample> Examples =
    [
        new("tosh", "Start an interactive shell"),
        new("tosh -c 'echo hello | type-of'", "Run one command and exit"),
        new("tosh ./examples/library_demo.tosh", "Run a script"),
        new("tosh 'help search json'", "Search the help"),
        new("tosh 'config set prompt.name-text toast'", "Change a setting"),
        new("tosh 'ls -la | where _.Type == file'", "A pipeline of objects, not lines"),
        new("tosh 'echo String.Join(\" \", [\"Hello\", \"World\"])'", "Call into .NET directly"),
        new("tosh 'mkdir -p scratch | get FullName'", "Commands return objects with members"),
        new("tosh 'func ll => ls -la'", "Define a function"),
        new("tosh 'func recent(days: TimeSpan) { ls -la | where _.Modified > ((date now) - $days) }'",
            "A typed parameter, used in a filter"),
        new("tosh --compile ./build.tosh -o ./build.dll", "Compile a script to an assembly"),
        new("tosh --export-command-metadata --latex", "The metadata the specification is generated from"),
    ];
}
