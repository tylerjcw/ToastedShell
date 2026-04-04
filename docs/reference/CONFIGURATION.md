# Configuration Reference

[Back to Index](INDEX.md)

For the fuller practical guide and larger examples, see [../CONFIGURATION.md](../CONFIGURATION.md).

## Startup Root

ToSh resolves its startup root in this order:

1. `TOSH_CONFIG_HOME`
2. `XDG_CONFIG_HOME/tosh`
3. `~/.config/tosh`

Startup files run in this order:

1. `config.tosh`
2. `profile.tosh`
3. `autoload/*.tosh`

Use `tosh --no-startup` to skip all startup loading.

## The `config` Command

```tosh
config
config browse
config browse prompt
config get prompt.name-text
config set prompt.name-text toast
config reset prompt
config reload
config init
```

`config browse` is the interactive editor. It discovers the config tree automatically, supports staged edits, previews prompt/theme changes live, and saves through the managed config block in `config.tosh`.

## Live Config Object

The `config` command and `$tosh.Config` operate on the same live runtime object.

```tosh
$tosh.Config.Display.TimeSpan.ScalarMode = "Long"
$tosh.Config.Prompt.HeaderRightLayout = "Time, UserHost, Duration"
$tosh.Config.Shell.Pipefail = true
```

## Main Sections

Current top-level sections are:

- `Theme`
- `Display`
- `Repl`
- `Prompt`
- `Shell`
- `History`
- `Startup`

## Prompt Configuration

The prompt is modular and layout-driven.

Important layout properties:

- `Prompt.HeaderLeftLayout`
- `Prompt.HeaderRightLayout`
- `Prompt.PromptLeftLayout`

Common built-in modules:

- `Time`
- `Directory`
- `Git`
- `UserHost`
- `HistoryId`
- `Jobs`
- `Duration`
- `ExitCode`
- `Name`
- `Indicator`

Example:

```tosh
$tosh.Config.Prompt.HeaderLeftLayout = "Time, Directory, Git"
$tosh.Config.Prompt.HeaderRightLayout = "UserHost, Jobs, Duration"
$tosh.Config.Prompt.PromptLeftLayout = "HistoryId, ExitCode, Name, Indicator"
```

## Display Configuration

Common display areas:

- `Display.Style`
- `Display.DateTime`
- `Display.DateTimeOffset`
- `Display.DateOnly`
- `Display.TimeOnly`
- `Display.TimeSpan`
- `Display.StorageSize`
- `Display.Permissions`
- `Display.FileAttributes`
- `Display.Paging`
- `Display.Profiles`

Examples:

```tosh
$tosh.Config.Display.Style = "Compact"
$tosh.Config.Display.DateOnly.ScalarMode = "Relative"
$tosh.Config.Display.TimeOnly.TableMode = "TwentyFourHour"
$tosh.Config.Display.TimeSpan.ScalarMode = "Long"
```

## Theme Configuration

The theme is grouped into:

- `Theme.Prompt`
- `Theme.Syntax`
- `Theme.Completion`
- `Theme.Diagnostics`
- `Theme.Tables`
- `Theme.Tui`

Matrix and nested-table styling is part of `Theme.Tables`, including:

- `MatrixDepth0`
- `MatrixDepth1`
- `MatrixDepth2`
- `MatrixDepth3`
- `MatrixDepth4`

## History And Startup

Useful history settings:

- `History.Persistent`
- `History.FilePath`
- `History.MaxEntries`
- `History.Deduplication`
- `History.IgnoreLeadingSpace`

Useful startup settings live under `Startup`, especially when you want to redirect config roots or manage startup files programmatically.

## `view`

`view` is the quick interactive surface for display preferences:

```tosh
view compact
view detail
view datetimeoffset relative
view dateonly scalar relative
view timeonly table 24h
view timespan scalar long
view columns FileSystemEntry Name Size Modified
```
