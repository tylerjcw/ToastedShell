# tosh

`tosh` (ToastedSHell) is an early shell / REPL / language scaffold that borrows NuShell-style pipeline syntax but keeps `.NET` objects alive all the way through execution, closer to the spirit of PowerShell.

The current codebase is intentionally small, but it already has the right seams:

- a lexer and parser for a tiny Unix-like pipeline language
- a streaming execution model based on `IAsyncEnumerable<object?>`
- a command registry and session runtime
- reflection-backed `.NET` interop for object construction, method calls, and member access
- a configurable renderer so the REPL can display arbitrary CLR objects consistently
- an adaptive batch display layer that can render homogeneous object sequences as text tables

## Current Commands

- `help`
- `man`
- `apropos`
- `exit`
- `clear`
- `history`
- `view`
- `echo`
- `pwd`
- `cd`
- `ls`
- `cat`
- `mkdir`
- `touch`
- `rm`
- `cp`
- `mv`
- `get`
- `inspect`
- `where`
- `type-of`
- `new`
- `call`

## Quick Start

```bash
dotnet run --project src/Tosh.Cli
```

```bash
dotnet run --project src/Tosh.Cli -- 'help'
dotnet run --project src/Tosh.Cli -- 'help search json'
dotnet run --project src/Tosh.Cli -- 'man where'
dotnet run --project src/Tosh.Cli -- 'apropos loop'
dotnet run --project src/Tosh.Cli -- 'view detail'
dotnet run --project src/Tosh.Cli -- 'echo 42 true hello | type-of'
dotnet run --project src/Tosh.Cli -- 'ls -la'
dotnet run --project src/Tosh.Cli -- 'ls | where Extension == .csproj | get Name'
dotnet run --project src/Tosh.Cli -- 'mkdir -p scratch'
dotnet run --project src/Tosh.Cli -- 'touch scratch/demo.txt'
dotnet run --project src/Tosh.Cli -- 'new System.Text.StringBuilder hello | call Append world | call ToString'
dotnet run --project src/Tosh.Cli -- 'new System.Text.StringBuilder hello | inspect'
dotnet run --project src/Tosh.Cli -- 'call System.DateTime Parse 2026-03-22T00:00:00Z | type-of'
```

Inside the REPL there are no side-channel meta commands. Session control is part of the shell itself:

- `help`
- `help search <query>`
- `man <topic>`
- `apropos <query>`
- `view compact`
- `view detail`
- `history`
- `clear`
- `exit`

The prompt itself now supports in-line editing with:

- left/right arrows
- up/down history recall
- `Home` / `End`
- `Delete` / `Backspace`

## Project Layout

- `src/Tosh.Core`: runtime primitives, command model, reflection helpers, built-in commands
- `src/Tosh.Language`: lexer, parser, AST, and execution engine
- `src/Tosh.Cli`: interactive REPL host
- `tests/Tosh.Tests`: parser and execution tests

## Supported Syntax Right Now

- bareword arguments: `echo hello`
- quoted strings: `echo "hello world"`
- numbers, booleans, and `null`
- pipelines with `|`
- comments beginning with `#`
- Unix-style short and long flags inside commands, such as `ls -la` and `mkdir -p`
- shell/REPL control as normal commands instead of special REPL directives
- adaptive table rendering for batches of similarly shaped objects such as `help`, `history`, and `ls`
- nullable member-path syntax with `?`, such as `ls -la | where Size? > 1000 | get Name`
- newline-separated top-level scripts and block statements, so `.tosh` files do not need `;` between every statement
- `return` statements for early exits from functions and scripts
- `break` / `continue` for `for`, `while`, and `each`
- `using` for CLR namespace/type imports, aliases, and `.tosh` module files

## Scripts

`tosh` now treats ordinary newlines as statement separators in script files, sourced files, and blocks:

```tosh
alias ll = ls -la

def recent(days: TimeSpan) {
    ls -la | where Modified > ((date now) - $days)
}

ll | first 5 | get { Name, Owner, Group }

def names() {
    return get Name
}

using System.IO = IO

def nonHiddenNames() {
    ll | each {
        if ($it.IsHidden) { continue }
        echo $it.Name
    }
}

using "./common.tosh"
```

Startup files follow the same model:

- `~/.config/tosh/profile.tosh`
- `~/.config/tosh/autoload/*.tosh`

## Good Next Steps

- see [docs/ROADMAP.md](docs/ROADMAP.md) for the longer-term plan
- variables and assignment
- blocks / closures for commands like `where`
- richer expression syntax for direct member access and method calls
- external process execution with object-aware adapters
- command discovery from loaded `.NET` assemblies
- script files, modules, and startup profiles
- readline-style input editing, completion, and richer object-aware REPL UX
