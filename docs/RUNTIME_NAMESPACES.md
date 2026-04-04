# Runtime Namespaces

## Goal

ToSh should stop growing a pile of flat magic globals like `$config`, `$result`, and `$ThisScript`.

The shell should expose runtime state through one live root object:

```tosh
$tosh
```

That root object should be:

- always available
- live, not snapshotted
- mostly read-only
- clearly separated from normal user variables

This keeps shell state discoverable, avoids name collisions, and gives us one stable place to extend later.

Environment-variable values are the one intentional exception:

```tosh
$env.HOME
$env.PATH
```

`$env` is a value-oriented namespace for direct environment-variable lookup, while runtime/session/config state lives under `$tosh`.

## Design Rules

- Runtime/session state lives under `$tosh`.
- Configuration lives under `$tosh.Config`.
- Live state and config must stay separate.
- `_` and `$value` remain contextual language forms.
- Flat runtime aliases like `$config` and `$result` should not remain as compatibility globals.
- If a user wants a shorter name, they can create one themselves.

## Root Shape

The first public shape should be:

```tosh
$tosh.Config
$tosh.Last
$tosh.Script
$tosh.Function
$tosh.Session
$tosh.Host
```

## Namespace Sections

### `$tosh.Config`

Live shell configuration.

This is the existing config object moved under the root namespace:

```tosh
$tosh.Config.Display.Style = "Detail"
$tosh.Config.Theme.Tables.BoxStyle = "Double"
```

Notes:

- mutable
- same object used by `config`
- intended for settings, not session state

### `$tosh.Last`

Information about the most recent execution result.

Initial properties:

- `Result`
- `ExitCode`

Examples:

```tosh
$tosh.Last.Result
$tosh.Last.ExitCode
```

Future candidates:

- `Error`
- `StatementText`
- `ExternalCommand`

We intentionally avoid a vague single `LastCommand` property for now.

### `$tosh.Script`

Current script/module execution context.

Initial properties:

- `Path`
- `Directory`
- `Name`
- `Args`

Examples:

```tosh
$tosh.Script.Path
$tosh.Script.Directory
$tosh.Script.Name
$tosh.Script.Args
```

This section should be live and reflect the currently executing script.

When ToSh is running a `-c` command with extra arguments, `Args` should reflect that top-level invocation as well.

### `$tosh.Function`

Current callable context.

Initial properties:

- `Name`
- `Args`
- `Input`

Examples:

```tosh
$tosh.Function.Name
$tosh.Function.Args
$tosh.Function.Input
```

This lets us expose function-call state without relying on flat globals.

### `$tosh.Session`

Current shell session state.

Initial properties:

- `CurrentDirectory`
- `HistoryCount`
- `HistoryFilePath`
- `JobCount`
- `OpenHandleCount`
- `OpenHandles`

Examples:

```tosh
$tosh.Session.CurrentDirectory
$tosh.Session.HistoryCount
$tosh.Session.HistoryFilePath
$tosh.Session.JobCount
$tosh.Session.OpenHandleCount
$tosh.Session.OpenHandles
```

This section should be read-only for now.

Use commands like `cd` and `jobs` for mutation and workflow.

### `$tosh.Host`

Information about the running ToSh host/runtime process.

Initial properties:

- `Version`
- `RuntimeId`
- `Framework`
- `OSDescription`
- `ProcessId`
- `ExecutablePath`
- `IsInteractive`

Examples:

```tosh
$tosh.Host.RuntimeId
$tosh.Host.ProcessId
$tosh.Host.Version
```

## What Stays Outside `$tosh`

The following remain language/context constructs for now:

- `_`
- `$value`

Reason:

- they are contextual lexical forms, not session-wide runtime globals
- they are tightly tied to pipelines, functions, and accessors
- `_` is a true pipeline-item language form
- `$value` is setter/accessor-local state

Function and script argument state should live under:

- `$tosh.Script.Args`
- `$tosh.Function.Args`
- `$tosh.Function.Input`

## Alias Policy

We should not keep the old flat globals as official compatibility aliases:

- no `$config`
- no `$result`
- no `$ThisScript`
- no `$ThisFunc`
- no `$LastExitCode`

ToSh is not released yet, so this is the right time to normalize the surface.

If users want convenience names, they can create them themselves.

## Reserved Name Policy

`$tosh` and `$env` should be reserved.

User declarations should not be allowed to bind:

- `tosh`

That prevents accidental shadowing of the root namespace.

## Future Namespaces

The likely next root-level namespaces are:

- `$env`
- maybe `$profile`

Those should stay separate from `$tosh`.

`$tosh` is for shell/runtime state.
`$env` is for process environment variables.

## Rollout

### Phase 1

- add `$tosh`
- move config to `$tosh.Config`
- expose live `Last`, `Script`, `Function`, `Session`, and `Host`
- remove flat runtime globals
- update docs, tests, examples, REPL help, and editor metadata

### Phase 2

- expand namespace-aware completions and hover
- add more host/session metadata as needed
