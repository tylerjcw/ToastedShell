# Getting Started with ToSh

[Back to Index](INDEX.md)

## Running ToSh

```bash
# Interactive REPL
tosh

# Run one command and exit
tosh -c 'ls -la | where _.Type == file | count'

# Run a script
tosh ./script.tosh

# Run a shebang-driven script with no required extension
./script

# Skip startup files
tosh --no-startup
```

### Command-Line Flags

| Flag | Description |
|------|-------------|
| `-h`, `--help` | Show usage information |
| `-c`, `--command` | Run one ToSh command string and exit |
| `--no-startup` | Skip `config.tosh`, `profile.tosh`, and `autoload/` |
| `--` | Stop option parsing for the next argument |

## First Things To Try

```tosh
help --cli
help browse
config browse
ls -la | where _.Type == file | sort Size | reverse | first 10
ip addr | where { _.State == up }
lsblk -l | summarize --sum Size
date -dt now
guid new v7
```

In the interactive REPL:

- `F1` opens the inline help browser, seeded from the token under the cursor
- `Alt+H` opens the same inline help browser when function keys are not exposed cleanly by the terminal
- `F2` tries to inline-inspect the reference under the cursor
- `Alt+I` opens the same inline inspector when function keys are not exposed cleanly by the terminal
- `i` inside inline help/inspect inserts into the active command line at the cursor

## Core Ideas

### Everything Is an Object

ToSh pipelines carry CLR objects, not text. For example:

```tosh
ls | first | type-of
ls | first | members
```

### The Pipeline Is Still Shell-Friendly

```tosh
ls -la | where _.Type == file | sort Size | reverse | first 5
```

`_` is the current pipeline item in predicate and block contexts:

```tosh
ls | where _.Size > 1mb
ls | each { echo $"File: {_.Name}" }
```

### Variables And Capture

```tosh
var files = (ls -la)     # capture one pipeline result as an object/list value
var name = $(whoami)     # capture text
echo $files
echo $name
```

Use:

- `(...)` when you want exactly one object value
- `$(...)` when you want text substitution

### CLR Interop Is Built In

```tosh
new System.Net.IPEndPoint 127.0.0.1 8080
call System.String Join ", " ["a", "b", "c"]
cast dateonly (date now)
```

### Configuration Is Live

```tosh
config browse
config get prompt.name-text
config set prompt.name-text toast
$tosh.Config.Display.TimeSpan.ScalarMode = "Long"
```

## Your First Script

```tosh
#!/usr/bin/env tosh

var user = (whoami)
var host = (hostname)

echo $"Hello, {$user}@{$host}"
ls -la | where _.Type == file | sort Size | reverse | first 5 | get { Name, Size }
```

Run it:

```bash
chmod +x ./hello
./hello
```

## Startup Files

ToSh resolves its startup root in this order:

1. `TOSH_CONFIG_HOME`
2. `XDG_CONFIG_HOME/tosh`
3. `~/.config/tosh`

Within that root, startup runs in this order:

1. `config.tosh`
2. `profile.tosh`
3. `autoload/*.tosh`

Useful commands:

```tosh
config init
config reload
config browse
```

## Where To Go Next

- [Language Reference](LANGUAGE.md)
- [Command Map](COMMANDS.md)
- [Pipeline Model](PIPELINES.md)
- [Type System](TYPES.md)
- [Configuration Reference](CONFIGURATION.md)
