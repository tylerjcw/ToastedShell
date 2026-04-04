# tosh

ToSh (ToastedShell) is a Unix-style shell, REPL, and scripting language built on .NET. It keeps real objects alive through the pipeline, gives them rich terminal rendering, and still aims to feel terse and comfortable for interactive shell work.

## What Ships Today

- Object-first pipelines over real CLR values
- ToastScript language features: functions, modules, classes, records, enums, pattern matching, exceptions, and typed parameters
- Rich display profiles and configurable rendering through `view` and `$tosh.Config.Display`
- Full-screen TUI apps: `help browse` and `config browse`
- Managed stream and file-handle commands for text and binary I/O
- Unix-style built-ins with typed output: `ls`, `ps`, `df`, `du`, `stat`, `find`, `grep`, `cat`, `wc`, `mv`, `touch`, `env`, and more
- Structured Linux adapters over machine-readable command output: `ip`, `lsblk`, `findmnt`, `lscpu`, `lsfd`, `lsipc`
- CLR interop (`new`, `call`, `cast`, `members`, `constructors`, `describe-type`)
- Native interop (`require native`, `bind`, buffers, `out` / `ref`, `read-buffer`, `write-buffer`)
- Modular prompt system with live prompt previews in `config browse`

## Quick Start

```bash
dotnet run --project src/Tosh.Cli
```

```bash
dotnet run --project src/Tosh.Cli -- -c 'help browse'
dotnet run --project src/Tosh.Cli -- -c 'config browse'
dotnet run --project src/Tosh.Cli -- -c 'ls -la | where _.Type == file | sort Size | reverse | first 10'
dotnet run --project src/Tosh.Cli -- -c 'ip addr | where { _.State == up }'
dotnet run --project src/Tosh.Cli -- -c 'lsblk -l | summarize --sum Size'
dotnet run --project src/Tosh.Cli -- -c 'date -dt now'
dotnet run --project src/Tosh.Cli -- -c 'guid new v7'
```

## A Quick Taste

```tosh
# Typed object pipelines
ls -la | where _.Type == file | sort Size | reverse | first 5 | get { Name, Size, Modified }

# CLR interop
new System.Net.IPEndPoint 127.0.0.1 8080
call System.String Join ", " ["objects", "pipelines", "types"]

# Managed file I/O
write-file scratch/notes.txt "hello"
var reader = (open-file scratch/notes.txt)
read-line-from $reader
close $reader

# Structured summaries
df | summarize _.Used
seq 5 | summarize --sum --avg --min --max --count

# Full-screen tooling
help browse regex
config browse prompt
```

## Scripts

ToSh supports normal `.tosh` scripts and shebang-driven scripts without requiring a file extension.

```tosh
#!/usr/bin/env tosh
echo $"hello from {$env.USER}"
```

```bash
chmod +x ./hello
./hello
tosh --no-startup ./hello
```

ToSh distinguishes between:

- `(...)`: capture exactly one object value
- `$(...)`: capture text output
- `source ./file.tosh`: run a file in the current scope
- `./file` or `tosh ./file`: execute a script as a script

## Documentation

- [Docs Index](docs/INDEX.md)
- [Getting Started](docs/reference/GETTING_STARTED.md)
- [Language Reference](docs/reference/LANGUAGE.md)
- [Command Map](docs/reference/COMMANDS.md)
- [Pipeline Model](docs/reference/PIPELINES.md)
- [Type System](docs/reference/TYPES.md)
- [Configuration Guide](docs/CONFIGURATION.md)
- [Project Status](docs/STATUS.md)
- [Architecture](docs/ARCHITECTURE.md)

The live in-shell help system is also a primary source of truth:

- `help <topic>`
- `help search <text>`
- `help browse`

## Current Status

ToSh is already strong as:

- an exploratory REPL
- a scripting shell
- a daily side shell for real work

It is not yet fully hardened as a default login shell. The main remaining work is startup/login-shell hardening, common-command parity polish, and more real-world shell edge-case coverage.
