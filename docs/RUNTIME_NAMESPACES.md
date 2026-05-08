# Runtime Namespaces

TōSh exposes shell and runtime state through two reserved root namespaces:

- `$tosh` — runtime, session, script, and host state
- `$env` — process environment variables

These are always available and cannot be shadowed by user declarations.

## `$tosh`

### Top-level properties

| Property        | Type | Description                                                            |
|-----------------|------|------------------------------------------------------------------------|
| `IsLoginShell`  | bool | `true` when started as a login shell (`-` argv[0] prefix or `--login`) |

### `$tosh.Last`

The result of the most recently executed statement.

| Property    | Type   | Description                                   |
|-------------|--------|-----------------------------------------------|
| `Result`    | any    | The last pipeline result value                |
| `ExitCode`  | int    | Exit code of the last external command (or 0) |

```tosh
ls | count
echo $tosh.Last.Result      # e.g. 42
echo $tosh.Last.ExitCode    # 0
```

### `$tosh.Script`

Execution context for the current script or module.

| Property    | Type   | Description                                       |
|-------------|--------|---------------------------------------------------|
| `Path`      | string | Absolute path of the running script file          |
| `Directory` | string | Directory containing the script                   |
| `Name`      | string | Script filename without directory                 |
| `Args`      | array  | Arguments passed to the script                    |

```tosh
# In a script file:
echo $tosh.Script.Path        # /home/user/scripts/deploy.tosh
echo $tosh.Script.Directory   # /home/user/scripts
echo $tosh.Script.Name        # deploy.tosh
echo $tosh.Script.Args        # arguments passed on the command line
```

When running a `-c` snippet, `Path` is `<repl>` or `<mcp-snippet>`.

### `$tosh.Function`

Context for the currently executing function.

| Property  | Type   | Description                              |
|-----------|--------|------------------------------------------|
| `Name`    | string | Name of the current function (or `""`)   |
| `Args`    | array  | Positional arguments the function received |
| `Input`   | any    | Pipeline input value (or `null`)         |

```tosh
func describe() {
    echo $"I am: {$tosh.Function.Name}"
    echo $"Args: {$tosh.Function.Args}"
}
describe "hello" "world"
```

### `$tosh.Session`

Live session state. Read-only; use commands like `cd` and `jobs` to mutate.

| Property          | Type   | Description                           |
|-------------------|--------|---------------------------------------|
| `CurrentDirectory`| string | Working directory (same as `pwd`)     |
| `HistoryCount`    | int    | Number of history entries             |
| `NextHistoryId`   | int    | ID that will be assigned to the next entry |
| `HistoryFilePath` | string | Path to the history file              |
| `JobCount`        | int    | Number of active background jobs      |
| `OpenHandleCount` | int    | Number of open stream handles         |
| `StartupProfile`  | string | Active startup profile (or `null`)    |

```tosh
echo $tosh.Session.CurrentDirectory   # /home/user/projects
echo $tosh.Session.HistoryCount       # 1042
echo $tosh.Session.JobCount           # 0
```

### `$tosh.Host`

Information about the TōSh process itself. Read-only.

| Property        | Type   | Description                                      |
|-----------------|--------|--------------------------------------------------|
| `Version`       | string | TōSh version string (e.g. `"26.4.80.10"`)       |
| `RuntimeId`     | string | .NET RID (e.g. `"linux-x64"`)                   |
| `Framework`     | string | .NET framework version (e.g. `".NET 10.0.3"`)   |
| `OSDescription` | string | OS name/version                                  |
| `ProcessId`     | int    | Current process ID                               |
| `ExecutablePath`| string | Absolute path to the tosh binary                 |
| `IsInteractive` | bool   | `true` when running an interactive REPL session  |

```tosh
echo $tosh.Host.Version        # 26.4.80.10
echo $tosh.Host.RuntimeId      # linux-x64
echo $tosh.Host.IsInteractive  # true (in REPL), false (in scripts)
if (not $tosh.Host.IsInteractive) {
    # running as a script — suppress interactive prompts
}
```

### `$tosh.Config`

Live shell configuration. Mutable. Same object used by the `config` command.

```tosh
$tosh.Config.Display.Style = "Detail"
$tosh.Config.Theme.Tables.BoxStyle = "Double"
```

See [CONFIGURATION.md](CONFIGURATION.md) for the full config schema.

---

## `$env`

Read access to process environment variables. **Read-only as a namespace**
— use the `export` command to set or update environment variables.

```tosh
echo $env.HOME          # /home/user
echo $env.PATH

export MY_VAR = "hello"     # set it
echo $env.MY_VAR            # hello

# This does NOT work — raises tosh.runtime.member_assignment_failed:
# $env.MY_VAR = "hello"
```

---

## Contextual Forms (Not Namespaced)

The following are language constructs, not namespace members:

- `_` — the current pipeline item inside `where`, `map`, `sort`, etc.
- `$value` — the incoming value inside a property setter
- `$this` — the current instance inside a class method or property
- `$super` — the parent-class constructor reference inside a constructor
