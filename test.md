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

## Good Next Steps

- variables and assignment
- blocks / closures for commands like `where`
- richer expression syntax for direct member access and method calls
- external process execution with object-aware adapters
- command discovery from loaded `.NET` assemblies
- script files, modules, and startup profiles
- readline-style input editing, completion, and richer object-aware REPL UX
