# Configuration

TōSh configuration is runtime-backed and scriptable.

That means:

- there is a live `$tosh.Config` object in every session
- the `config` command can inspect and mutate the same object
- persistent startup customization lives in `config.tosh`, which is just normal TōSh code

## Startup Order

TōSh resolves its config home in this order:

1. `TOSH_CONFIG_HOME`
2. `XDG_CONFIG_HOME/tosh`
3. `~/.config/tosh`

Within that directory, startup files run in this order:

1. `config.tosh`
2. `profile.tosh`
3. `autoload/*.tosh` sorted lexically

`config.tosh` runs first so it can redirect profile and autoload locations for the rest of startup.

## Bootstrap

Scaffold a config directory with:

```tosh
config init
config init ./scratch/tosh-config
config reload
```

That creates missing startup files without overwriting existing ones:

- `config.tosh`
- `profile.tosh`
- `autoload/`

## Runtime Use

Inspect the whole config object:

```tosh
config
```

Get one value:

```tosh
config get prompt.name-text
config get repl.continuation-prompt
```

Set one value:

```tosh
config set prompt.name-text toast
config set repl.continuation-prompt "..> "
config set display.style detail
```

You can also pipe a value into `config set`:

```tosh
echo toast | config set prompt.name-text
```

Direct object access works too:

```tosh
$tosh.Config.Prompt.NameText = "toast"
$tosh.Config.Prompt.IndicatorText = " >> "
$tosh.Config.Repl.GhostTextEnabled = false
$tosh.Config.Display.TimeSpan.ScalarMode = "Long"
$tosh.Config.Display.StorageSize.Mode = "Bytes"
```

Reset a section or the whole config:

```tosh
config reset prompt
config reset repl
config reset
```

Replay startup files in the current session after editing them on disk:

```tosh
config reload
```

`config reload` resets the live config object back to defaults, then reruns startup in normal order:

1. `config.tosh`
2. `profile.tosh`
3. `autoload/*.tosh`

It also respects any startup-path redirects made by `config.tosh`.

## Config Surface

Current runtime-backed sections:

- `Theme`
- `Display`
- `Repl`
- `Prompt`
- `Shell`
- `History`
- `Startup`

Examples:

```tosh
$tosh.Config.Theme.Syntax.Keyword.Foreground = "bright-magenta"
$tosh.Config.Theme.Syntax.ValidCommand.Foreground = "bright-green"
$tosh.Config.Theme.Completion.GhostText.Foreground = "gray"
$tosh.Config.Theme.Diagnostics.Title.Foreground = "bright-red"
$tosh.Config.Theme.Tables.BoxStyle = "Double"
$tosh.Config.Theme.Tables.Header.Foreground = "bright-yellow"
$tosh.Config.Theme.Tables.RecordKey.Bold = true
$tosh.Config.Theme.Tables.Selection.Bold = true
$tosh.Config.Theme.Tables.MatrixDepth0.Foreground = "bright-cyan"
$tosh.Config.Theme.Tables.MatrixDepth1.Foreground = "cyan"
$tosh.Config.Theme.Tables.MatrixDepth2.Foreground = "bright-blue"
$tosh.Config.Theme.Tui.BoxStyle = "Double"
$tosh.Config.Theme.Tui.TreeStyle = "Clean"
$tosh.Config.Theme.Tui.Border.Foreground = "gray"
$tosh.Config.Theme.Tui.Title.Foreground = "bright-cyan"
$tosh.Config.Theme.Tui.SelectedGutter.Foreground = "bright-cyan"
$tosh.Config.Theme.Tui.Namespace.Foreground = "bright-cyan"
$tosh.Config.Theme.Tui.Type.Foreground = "green"
$tosh.Config.Theme.Tui.Method.Foreground = "magenta"
$tosh.Config.Theme.Tui.Property.Foreground = "yellow"
$tosh.Config.Theme.Tui.Constructor.Foreground = "bright-green"
$tosh.Config.Theme.Tui.SectionHeading.Bold = true

$tosh.Config.Display.Style = "Detail"
$tosh.Config.Display.DateTime.TableMode = "Raw"
$tosh.Config.Display.DateTimeOffset.ScalarMode = "Relative"
$tosh.Config.Display.DateOnly.ScalarMode = "Relative"
$tosh.Config.Display.TimeOnly.TableMode = "TwentyFourHour"
$tosh.Config.Display.TimeSpan.ScalarMode = "Long"
$tosh.Config.Display.TimeSpan.TableMode = "Short"
$tosh.Config.Display.StorageSize.Mode = "Bytes"
$tosh.Config.Display.Permissions.Mode = "Both"
$tosh.Config.Display.FileAttributes.Mode = "Hex"
$tosh.Config.Display.Paging.Enabled = true
$tosh.Config.Display.Paging.ReservedLines = 1

$tosh.Config.Repl.ContinuationPrompt = "..> "
$tosh.Config.Repl.SyntaxHighlightingEnabled = true
$tosh.Config.Repl.GhostTextEnabled = true
$tosh.Config.Repl.CompletionMaxVisible = 10

$tosh.Config.Prompt.HeaderLeftLayout = "Time, Directory, Git"
$tosh.Config.Prompt.HeaderRightLayout = "UserHost, Jobs, Duration"
$tosh.Config.Prompt.PromptLeftLayout = "HistoryId, ExitCode, Name, Indicator"
$tosh.Config.Prompt.NameText = "toast"
$tosh.Config.Prompt.NameColor = "yellow"
$tosh.Config.Prompt.TimeEnabled = true
$tosh.Config.Prompt.TimeFormat = "HH:mm"
$tosh.Config.Prompt.DirectoryColor = "blue"
$tosh.Config.Prompt.GitEnabled = true
$tosh.Config.Prompt.UserHostEnabled = true
$tosh.Config.Prompt.HistoryIdEnabled = true
$tosh.Config.Prompt.JobsEnabled = true
$tosh.Config.Prompt.DurationEnabled = true
$tosh.Config.Prompt.DurationThresholdMilliseconds = 500
$tosh.Config.Prompt.ExitCodeEnabled = true
$tosh.Config.Prompt.IndicatorText = " ❯ "

$tosh.Config.Shell.Pipefail = true
$tosh.Config.Shell.AutoCd = false
$tosh.Config.Shell.Trace = false
$tosh.Config.Shell.MaxRecursionDepth = 128

$tosh.Config.History.Persistent = true
$tosh.Config.History.FilePath = "history.jsonl"
$tosh.Config.History.MaxEntries = 5000
$tosh.Config.History.Deduplication = "Consecutive"
$tosh.Config.History.IgnoreLeadingSpace = false
```

`Shell.MaxRecursionDepth` limits the number of active ToastScript
execution frames in one asynchronous flow. The default and safe maximum
are both `128`; a session may choose a stricter value from `1` through
`128`. The limit covers functions, methods, lambdas, constructors, and
nested `eval`/`source` execution in both interpreted and compiled code.
Exceeding it raises the structured
`tosh.runtime.recursion_limit_exceeded` diagnostic without terminating
the shell process.

## Example `config.tosh`

```tosh
# Runs before profile.tosh and autoload.

$tosh.Config.Prompt.HeaderLeftLayout = "Time, Directory"
$tosh.Config.Prompt.HeaderRightLayout = "UserHost, Duration"
$tosh.Config.Prompt.PromptLeftLayout = "HistoryId, ExitCode, Name, Indicator"
$tosh.Config.Prompt.NameText = "toast"
$tosh.Config.Prompt.NameColor = "bright-yellow"
$tosh.Config.Prompt.TimeEnabled = true
$tosh.Config.Prompt.TimeFormat = "HH:mm"
$tosh.Config.Prompt.DurationThresholdMilliseconds = 250
$tosh.Config.Prompt.IndicatorText = " >> "

$tosh.Config.Theme.Syntax.Keyword.Foreground = "bright-magenta"
$tosh.Config.Theme.Syntax.Path.Foreground = "#7ee787"
$tosh.Config.Theme.Completion.SelectedLabel.Foreground = "bright-cyan"
$tosh.Config.Theme.Diagnostics.Help.Foreground = "bright-yellow"
$tosh.Config.Theme.Tables.BoxStyle = "Double"
$tosh.Config.Theme.Tables.Border.Foreground = "gray"
$tosh.Config.Theme.Tables.Header.Bold = true
$tosh.Config.Theme.Tables.Selection.Bold = true
$tosh.Config.Theme.Tables.MatrixDepth0.Bold = true
$tosh.Config.Theme.Tables.MatrixDepth3.Foreground = "green"
$tosh.Config.Theme.Tables.MatrixDepth4.Foreground = "bright-yellow"
$tosh.Config.Theme.Tui.BoxStyle = "Double"
$tosh.Config.Theme.Tui.TreeStyle = "Clean"
$tosh.Config.Theme.Tui.Title.Foreground = "bright-cyan"
$tosh.Config.Theme.Tui.SelectedGutter.Foreground = "bright-yellow"
$tosh.Config.Theme.Tui.Namespace.Foreground = "bright-cyan"
$tosh.Config.Theme.Tui.Type.Foreground = "green"
$tosh.Config.Theme.Tui.Method.Foreground = "magenta"
$tosh.Config.Theme.Tui.Property.Foreground = "yellow"
$tosh.Config.Theme.Tui.Constructor.Foreground = "bright-green"
$tosh.Config.Theme.Tui.Footer.Foreground = "gray"

$tosh.Config.Repl.ContinuationPrompt = "..> "
$tosh.Config.Repl.CompletionMaxVisible = 10

$tosh.Config.Display.Style = "Compact"
$tosh.Config.Display.DateTime.TableMode = "Relative"
$tosh.Config.Display.TimeSpan.ScalarMode = "Long"
$tosh.Config.Display.StorageSize.Mode = "Human"
$tosh.Config.Display.Permissions.Mode = "Symbolic"
$tosh.Config.Display.FileAttributes.Mode = "Names"
$tosh.Config.Display.Paging.Enabled = true
$tosh.Config.Display.Paging.ReservedLines = 1

# Per-type table-column overrides can live in config/profile scripts too.
view columns table Kind Name

$tosh.Config.History.Persistent = true
$tosh.Config.History.MaxEntries = 5000
$tosh.Config.History.Deduplication = "Consecutive"
```

## Example `profile.tosh`

```tosh
func ll => ls -la
func gs => git status --short
```

## Notes

- `config` and `$tosh.Config` operate on the same live runtime object.
- Relative startup paths inside `$tosh.Config.Startup` resolve from the active config root.
- The default history file lives under the XDG state directory: `$XDG_STATE_HOME/tosh/history.jsonl` or `~/.local/state/tosh/history.jsonl`.
- Relative history file paths inside `$tosh.Config.History.FilePath` resolve from TōSh's state root, not the startup config directory.
- `config init` only creates missing files; it does not overwrite existing user config.
- `config init` does not create the history file; TōSh creates it on demand when interactive history is persisted.
- `config reload` is the easiest way to apply edits from `config.tosh`, `profile.tosh`, or autoload files without restarting TōSh.
- `config browse` is the best interactive editor for the live config tree. It auto-discovers config values, stages edits safely, previews prompt/theme changes live, and saves through the managed block in `config.tosh`.
- Use `history path`, `history save`, `history reload`, and `history clear` to inspect or manage the persisted history file.
- `$tosh.Config.History.Deduplication` accepts `None`, `Consecutive`, or `All`.
- `$tosh.Config.History.IgnoreLeadingSpace = true` gives you zsh-style "don't save commands that start with a space" behavior.
- Long interactive output pages automatically by default. Configure that with `$tosh.Config.Display.Paging`.
- The interactive pager supports `Space` / `PageDown` for the next page, `b` / `PageUp` for the previous page, `Enter` / `Up` / `Down` for line-wise movement, `g` / `G` for home/end, and `q` / `Esc` to quit.
- Use `view permissions <symbolic|octal|both>` and `view attributes <names|hex|both>` for quick display changes in the current session.
- Use `view columns <type> <column ...>` for per-type table-column overrides, and `view columns <type> default` to remove an override.
- Active per-type column overrides are visible through `$tosh.Config.Display.Profiles`.
