# AGENTS.md

Quick-reference for AI agents and coding assistants working with TōSh (ToastedShell).

## Build & Test

```bash
dotnet build Tosh.slnx                    # build all projects
dotnet test  Tosh.slnx                    # run all tests
dotnet run --project src/Tosh.Cli         # run the shell interactively
dotnet run --project src/Tosh.Cli -- -c "echo hello"  # run a one-liner
```

## Project Structure

| Project | Purpose |
|---------|---------|
| `src/Tosh.Cli` | CLI entry point, REPL, startup loader |
| `src/Tosh.Core` | Built-in commands, type system, runtime |
| `src/Tosh.Language` | Lexer, parser, evaluator (ToshEngine) |
| `src/Tosh.LanguageServices` | LSP/MCP language features |
| `src/Tosh.Lsp` | Language Server Protocol server |
| `src/Tosh.Mcp` | Model Context Protocol server |
| `src/Tosh.Tui` | Terminal UI widgets |
| `tests/Tosh.Tests` | Unit and integration tests |
| `tests/Tosh.LspFixture` | LSP test fixtures |

## Language Syntax Quick Reference

### Variables

```tosh
var x = 42                         # declare a local variable
var name = "world"                 # string
var list = [1, 2, 3]               # list
var map = { name: "Alice", age: 30 } # record/dict

# After declaration, use $ prefix to reference or modify:
$x = 100                           # modify existing variable
echo $x                            # use variable
```

### Environment Variables

```tosh
# READ — use $env namespace (case-insensitive):
echo $env.HOME                    # /home/user
echo $env.path                    # works (case-insensitive)

# WRITE — use the `export` command with = syntax:
export MY_VAR = "hello"            # sets env var for this process + children
export PATH = "/usr/local/bin:$env.PATH"

# ⚠️ $env.X = "value" does NOT work — $env is read-only for assignment.
# Always use `export NAME = "value"`.
```

### Strings

```tosh
'single quotes are literal'
"double quotes allow \n escapes"
$"interpolated: ${expr} or $variable or $env.HOME"
```

### Functions

```tosh
# One-liner
func greet => echo "hello"

# With body
func greet(name) {
    echo $"Hello, {$name}!"
}

# Tosh has NO 'alias' keyword. Use one-liner functions instead:
func ll => ls -la
func gs => git status
```

### Control Flow

```tosh
if $x > 10 {
    echo "big"
} else {
    echo "small"
}

for $item in $list {
    echo $item
}

try {
    risky-command
} catch $err {
    echo $"Error: {$err}"
} finally {
    cleanup
}
```

### Pipes and Redirects

```tosh
ls -la | where _.Type == file | sort-by Size | head 10
cat file.txt | grep "pattern" | wc -l
echo "hello" > output.txt
echo "more" >> output.txt
```

### Special Namespaces

```tosh
$env.HOME              # environment variables (read-only namespace, use `export` to write)
$tosh.Config.*         # shell configuration (TTY, prompt, keybindings, etc.)
$tosh.Config.Shell.Dirs  # directory aliases dict
```

## Common Gotchas

1. **`$env.X = "value"` does not work.** The `$env` namespace is read-only for assignment.
   Always use: `export NAME = "value"`

2. **No `alias` keyword.** Use `func name => command` for one-liner aliases.

3. **`export` uses `=` syntax**: `export NAME = "value"` — not `export NAME "value"` or `export NAME=value`.

4. **Variable declaration** uses `var`: `var x = 42` to declare, `$x = 100` to modify after.

5. **String interpolation** uses `$"..."` with `$variable`, `$env.VAR`, or `${expression}`.

6. **Single quotes are literal** — no variable expansion or escape sequences.

## Startup File Load Order

When tosh starts as a login shell (`-` prefix in argv[0] or `--login`):

1. `~/.config/tosh/config.tosh` — shell configuration (prompt, keybindings, TTY settings)
2. `~/.config/tosh/profile.tosh` — environment setup, exports, user functions
3. `~/.config/tosh/autoload/*.tosh` — alphabetically sorted, top-level `.tosh` files only

Errors in any startup file are logged to stderr but do not prevent the shell from starting.
Use `--safe` to skip all startup files, or `--no-profile` to skip profile + autoload.

## CLI Flags

```
tosh                              # interactive REPL
tosh -c "command"                 # execute command string
tosh script.tosh                  # execute script file
tosh --login                      # login shell mode
tosh --no-startup                 # skip config.tosh
tosh --no-profile                 # skip profile.tosh and autoload/
tosh --safe                       # skip all startup files
tosh --version                    # print version
tosh --help                       # print help
tosh --export-command-metadata    # dump all builtin command metadata as JSON
tosh --export-command-metadata --latex   # dump as LaTeX
tosh --export-command-metadata --vscode  # dump as VS Code format
tosh --dump-builtins              # alias for --export-command-metadata (JSON)
```

## Introspecting Commands at Runtime

```tosh
help ls                           # returns a HelpTopic object
help ls | to json                 # full structured metadata as JSON
help ls | get Usage               # just the usage string
apropos "file"                    # search commands by keyword
```

## Machine-Readable Command Metadata

```bash
# Dump all ~209 built-in commands with full signatures, args, options, examples:
tosh --dump-builtins
tosh --export-command-metadata

# The MCP server also exposes a `command_metadata` tool for AI agents.
```

## Built-in Command Categories

TōSh has ~209 built-in commands spanning these categories:

- **Filesystem**: ls, cd, pwd, mkdir, touch, rm, cp, mv, chmod, chown, find, glob, tree, stat, link, mktemp, readlink, realpath, dirname, basename
- **Text/IO**: cat, read, write, append, head, tail, wc, grep, cut, tr, uniq, lines, read-lines, read-bytes, write-bytes, open, close, tee
- **Data/Format**: from, to, parse, split, join, replace, match, template, hash, get, rename, inspect
- **Functional**: where, each, map, filter, reduce, scan, flatmap, zip, first, last, skip, sort, reverse, count, collect, flatten, distinct, group-by, chunk, window, partition, frequencies, transpose, interleave
- **Aggregation**: sum, average, min, max, summarize
- **System**: uname, hostname, whoami, id, ps, kill, signal, jobs, fg, bg, uptime, free, df, du, lsblk, lscpu, lsfd, lsipc, systemctl, journalctl, loginctl, hostnamectl, networkctl, findmnt
- **Environment**: env, vars, export, forget/unset, which
- **Networking**: ping, http, ip (13 structured subcommands)
- **Time**: date, time, timespan, sleep
- **Shell**: echo, clear, history, config, exit, exec, source, assert
- **Object/CLR**: typeof, describe-type, members, methods, constructors, types, load-assembly, new-object, call, cast, get-props, set-prop, has-prop, has-method, get-methods, call-method
- **Prompt**: prompt-time, prompt-dir, prompt-git, prompt-userhost, prompt-history, prompt-jobs, prompt-duration, prompt-exitcode, prompt-text, prompt-newline
- **Interop**: alloc, native-free, native-read, native-write, native-sizeof, native-offsetof
- **Path Predicates**: exists, is-file, is-dir, is-link

## Writing Config Files

Commands in config/profile/autoload files use the same syntax as the interactive shell.
There is no separate scripting syntax.

```tosh
# ~/.config/tosh/profile.tosh — typical setup
export EDITOR = "nvim"
export PATH = "$env.HOME/.local/bin:$env.PATH"

$tosh.Config.Shell.Dirs["projects"] = "$env.HOME/projects"

func ll => ls -la
func gs => git status
func mkcd(dir) {
    mkdir $dir
    cd $dir
}
```
