# Event System

[Back to Index](INDEX.md)

ToSh includes a fully featured event system for reacting to shell lifecycle events, command execution, and user-defined events.

## Overview

The event system consists of:
- **Event definitions** — Declare the shape of an event (name and fields)
- **Event handlers** — Functions that run when an event is raised
- **The event bus** — Routes events to registered handlers with priority ordering
- **Built-in events** — Pre-defined events for shell lifecycle

## Built-In Events

| Event | Description | Fields |
|-------|-------------|--------|
| `DirectoryChanged` | Fired when `cd` changes the working directory | `OldDirectory`, `NewDirectory` |
| `CommandStarting` | Fired before a command executes | `CommandName`, `Arguments`, `Pipeline` |
| `CommandCompleted` | Fired after a command finishes | `CommandName`, `ExitCode`, `Duration`, `Result` |
| `SessionStarted` | Fired when ToSh starts (after startup files) | `StartTime`, `ConfigDirectory` |
| `SessionEnding` | Fired when ToSh is about to exit | `ExitCode` |
| `VariableChanged` | Fired when a variable is modified | `VariableName`, `OldValue`, `NewValue` |
| `JobStarted` | Fired when a background job starts | `JobId`, `CommandName` |
| `JobCompleted` | Fired when a background job completes | `JobId`, `CommandName`, `ExitCode` |

All built-in events also carry base fields: `Name`, `Sender`, `Timestamp`, `Cancelled`.

## Handling Events

Functions become event handlers by adding a `handles` clause:

```tosh
func onDirChange(event) handles DirectoryChanged {
    writeline $"Moved to {$event.NewDirectory}"
}
```

The function receives the event object as its first parameter with access to all event fields.

### Handler Priority

Handlers execute in priority order (lower number = higher priority). Handlers without a priority run after prioritized ones, in registration order.

```tosh
func earlyHandler(event) handles CommandStarting priority 1 {
    writeline "I run first"
}

func laterHandler(event) handles CommandStarting priority 100 {
    writeline "I run second"
}
```

### One-Shot Handlers

A handler marked `once` is automatically unregistered after its first invocation:

```tosh
func welcomeOnce(event) handles SessionStarted once {
    writeline "Welcome to ToSh! (This message only appears once.)"
}
```

### When Guards

A `when` clause adds a condition — the handler only fires when the guard evaluates to true:

```tosh
func onSlowCommand(event) handles CommandCompleted when { $event.Duration > (timespan 1s) } {
    writeline $"Slow command: {$event.CommandName} took {$event.Duration}"
}

func onCdToHome(event) handles DirectoryChanged when { $event.NewDirectory.Name == "home" } {
    writeline "Welcome home!"
}
```

### Cancellation

Handlers can cancel events by calling `Cancel()` on the event object. For `CommandStarting`, this prevents the command from executing:

```tosh
func blockDangerousCommands(event) handles CommandStarting priority 0 when { $event.CommandName == "rm" } {
    writeline "rm is blocked in this session"
    $event.Cancel()
}
```

Once an event is cancelled, no further handlers are invoked.

## User-Defined Events

### Declaring Events

```tosh
event BuildCompleted {
    Project = ""
    Duration = (timespan 0s)
    Success = true
}
```

Each field has a name and a default value. The defaults are used when the event is raised without overrides.

### Modifiers

```tosh
# Required — raises an error if no handlers are registered when raised
required event CriticalError {
    Message = ""
    Code = 0
}

# Local — handlers are automatically removed when the declaring scope exits
local event ScopedNotification {
    Text = ""
}
```

### Raising Events

```tosh
raise $BuildCompleted                            # With default field values
raise $BuildCompleted { Project = "myapp", Success = false }  # With overrides
```

The `{ field = value }` syntax creates a record literal that overrides specific fields while keeping defaults for unspecified fields.

Events can also be raised via piping:

```tosh
$BuildCompleted | raise
```

### Handling User Events

```tosh
event DeployRequest {
    Target = ""
    Version = ""
}

func onDeploy(event) handles DeployRequest {
    writeline $"Deploying v{$event.Version} to {$event.Target}"
}

raise $DeployRequest { Target = "production", Version = "2.1.0" }
```

## Managing Event Handlers

The `events` command provides introspection and management:

```tosh
events                               # List all registered handlers
events names                         # List event names that have handlers
events handlers DirectoryChanged     # Show handlers for a specific event
events remove DirectoryChanged onDirChange   # Remove a specific handler
events clear CommandCompleted        # Remove all handlers for an event
```

### Handler Display

When listing handlers, the display shows:

| Column | Description |
|--------|-------------|
| Event | Event name the handler is bound to |
| Handler | Function name |
| Priority | Priority value (or `—` for default) |
| Once | Whether it's a one-shot handler |

## Event Lifecycle

### Command Events

For each command executed (except `raise` and `events` themselves, to prevent recursion):

1. `CommandStarting` is raised with command name, arguments, and full pipeline text
2. If `CommandStarting` is cancelled, the command does not execute
3. The command executes
4. `CommandCompleted` is raised with command name, exit code, duration, and result

### Session Events

1. ToSh starts, loads startup files
2. History and directory stack are initialized
3. `SessionStarted` is raised
4. REPL runs (or single command executes)
5. `SessionEnding` is raised
6. ToSh exits

Session events are wrapped in try/catch — handler failures do not prevent startup or shutdown.

### Directory Events

When `cd`, `back`, or `forward` changes the working directory:

1. `DirectoryChanged` is raised with old and new directory entries

## Local Event Scoping

Events declared with `local` have their handlers automatically cleaned up when the declaring scope exits:

```tosh
func runWithEvents() {
    local event StepCompleted { Step = "" }

    func onStep(e) handles StepCompleted {
        writeline $"Completed: {$e.Step}"
    }

    raise $StepCompleted { Step = "init" }
    raise $StepCompleted { Step = "process" }
}
# After runWithEvents returns, the StepCompleted handler is automatically removed
```

This prevents handler leaks from functions or scripts that define events for internal use.

## Practical Examples

### Command Timing Logger

```tosh
func logTiming(event) handles CommandCompleted when { $event.Duration > (timespan 100ms) } {
    append-file ~/.config/tosh/slow_commands.log $"[{(date now)}] {$event.CommandName}: {$event.Duration}\n"
}
```

### Directory Change Hook

```tosh
func showGitStatus(event) handles DirectoryChanged {
    if (is-dir ".git") {
        writeline ""
        git status --short
    }
}
```

### Auto-Save History on Exit

```tosh
func saveHistoryOnExit(event) handles SessionEnding {
    history save
}
```

## See Also

- [Language Reference](LANGUAGE.md) — Function definitions with `handles` clause
- [Commands Reference](COMMANDS.md) — `raise` and `events` commands
