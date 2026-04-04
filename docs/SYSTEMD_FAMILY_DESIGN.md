# Systemd Family Design

## Goal

Bring the systemd-oriented commands into ToSh in a way that feels:

- familiar to users coming from Linux
- object-first inside the pipeline
- consistent across related commands
- realistic about the fact that the underlying tools do **not** all expose data the same way

The command family in scope is:

- `systemctl`
- `journalctl`
- `loginctl`
- `hostnamectl`
- `networkctl`
- `busctl`

## High-Level Recommendation

Keep the familiar command names.

Do **not** invent a brand-new top-level `systemd` command in place of them.

Instead:

1. preserve the standard command names
2. make the read/query paths return typed objects
3. keep action/mutation paths recognizable
4. share object models, display rules, and selection behavior across the family

That gives users normal muscle memory:

```tosh
systemctl list-units
journalctl -u sshd.service
loginctl list-sessions
hostnamectl status
networkctl list
busctl list
```

but with ToSh semantics:

- object pipelines instead of text scraping
- display-only `--show`, `--hide`, `--show-all`
- strong detail views for standalone objects
- consistent rendering and filtering

## Capability Findings On This Machine

Local systemd version:

- `systemd 260`

Observed machine-readable surfaces:

- `systemctl list-units --output=json` works
- `systemctl show <unit>` returns stable key/value property output
- `journalctl -o json` works well
- `loginctl --json=short` works for list commands
- `hostnamectl --json=short` works
- `networkctl --json=short` exists
- `busctl list --json=short` works

Important nuance:

- `systemctl` is good for **list** and **show**
- `journalctl` is excellent for structured log events
- `loginctl`, `hostnamectl`, `networkctl`, and `busctl` are already JSON-friendly
- `busctl introspect` still wants special handling and should not be treated as “just another JSON list”

## Design Principles

### 1. Query Paths Return Objects

Read/query commands should return objects, not formatted text.

Examples:

- `systemctl list-units`
- `systemctl show sshd.service`
- `journalctl -n 100`
- `loginctl list-sessions`
- `hostnamectl status`
- `networkctl list`
- `busctl list`

### 2. Action Paths Stay Familiar

Mutation commands should keep the standard systemd verbs, but should return structured result objects where possible.

Examples:

- `systemctl start sshd.service`
- `systemctl stop sshd.service`
- `systemctl restart sshd.service`
- `loginctl terminate-session 2`
- `hostnamectl hostname valinor`

We should not try to hide those actions behind brand-new ToSh-only verbs.

### 3. Shared Output Selection

Where a command returns rows, it should support the same display-only selection layer used by other Unix-style ToSh commands:

- `--show`
- `--hide`
- `--show-all`

For commands that already have native field-selection concepts, we can mirror them onto the same layer where practical.

Examples:

```tosh
systemctl list-units --show Unit,ActiveState,SubState,Description
loginctl list-sessions --show Session,User,Seat,State
busctl list --show Name,Process,User,Unit
```

### 4. Capability-Probed Adapters

We should not hardcode one parsing strategy forever.

The adapter layer should probe for the best available structured surface at runtime:

1. JSON output when available
2. stable property output (`systemctl show`, `loginctl show-*`, etc.)
3. only fall back to plain-text parsing when absolutely necessary

That keeps ToSh compatible with a wider range of systemd versions without designing around the oldest possible environment.

### 5. Reuse Object Shapes Across Commands

Avoid one-off types when possible.

Good reusable domain types:

- `SystemdUnitInfo`
- `SystemdUnitPropertySet`
- `SystemdJournalEntry`
- `SystemdSessionInfo`
- `SystemdUserInfo`
- `SystemdSeatInfo`
- `SystemdHostInfo`
- `SystemdBusNameInfo`
- `SystemdBusObjectInfo`
- `SystemdBusMemberInfo`

If a shape is too dynamic or D-Bus-specific to justify a dedicated CLR type yet, prefer shell-native table/record values over inventing throwaway classes.

## Command-Specific Recommendations

## `systemctl`

### First Slice

Implement structured support for:

- `systemctl list-units`
- `systemctl list-unit-files`
- `systemctl list-jobs`
- `systemctl list-machines`
- `systemctl show <unit>`
- `systemctl status <unit>` as a richer single-object/detail path

### Data Source Strategy

- use `systemctl list-units --output=json` for row listings when available
- use `systemctl show <unit>` for detail/property views
- keep action verbs (`start`, `stop`, `restart`, `reload`, `enable`, `disable`, `mask`, `unmask`) recognizable

### Recommended Object Shape

For row listings:

- `Unit`
- `LoadState`
- `ActiveState`
- `SubState`
- `Description`

For richer detail views:

- the common row fields above
- `Names`
- `Documentation`
- `FragmentPath`
- `UnitFileState`
- `UnitFilePreset`
- `Requires`
- `Wants`
- `WantedBy`
- `Conflicts`
- `Before`
- `After`
- `CanStart`
- `CanStop`
- `CanReload`
- timestamps where meaningful

### Notes

`systemctl` should probably become the anchor of the family. `loginctl`, `hostnamectl`, and `networkctl` should feel like siblings that follow the same adapter rules.

## `journalctl`

### First Slice

Implement structured support for:

- `journalctl`
- `journalctl -n <count>`
- `journalctl -u <unit>`
- `journalctl --since ... --until ...`
- `journalctl -g <pattern>`
- `journalctl -f` later, after the non-follow query path is solid

### Data Source Strategy

- use `-o json`
- map the common journal fields into a stable typed view
- preserve the full raw field map somewhere on the object so advanced filtering is still possible

### Recommended Object Shape

- `Timestamp`
- `Message`
- `Priority`
- `Unit`
- `UserUnit`
- `SyslogIdentifier`
- `Pid`
- `Comm`
- `Exe`
- `Hostname`
- `BootId`
- `InvocationId`
- `Transport`
- `Cursor`
- `MessageId`
- `Fields` or `RawFields`

### Notes

`journalctl` should be treated as a log/event command, not just a text pager.

## `loginctl`

### First Slice

Implement structured support for:

- `loginctl list-sessions`
- `loginctl list-users`
- `loginctl list-seats`
- `loginctl show-session`
- `loginctl show-user`
- `loginctl show-seat`

### Data Source Strategy

- use `--json=short` for list commands
- use `show-*` plus selected properties for detail views

### Recommended Reusable Types

- `SystemdSessionInfo`
- `SystemdUserInfo`
- `SystemdSeatInfo`

## `hostnamectl`

### First Slice

Implement structured support for:

- `hostnamectl status`
- `hostnamectl hostname`
- `hostnamectl icon-name`
- `hostnamectl chassis`
- `hostnamectl deployment`
- `hostnamectl location`

### Data Source Strategy

- use `--json=short`

### Recommended Object Shape

- `Hostname`
- `StaticHostname`
- `PrettyHostname`
- `DefaultHostname`
- `HostnameSource`
- `IconName`
- `Chassis`
- `Deployment`
- `Location`
- `KernelName`
- `KernelRelease`
- `KernelVersion`
- `OperatingSystemPrettyName`
- `OperatingSystemHomeUrl`
- `HardwareVendor`
- `HardwareModel`
- `FirmwareVendor`
- `FirmwareVersion`
- `FirmwareDate`
- `MachineId`
- `BootId`

## `networkctl`

### First Slice

Implement structured support for:

- `networkctl list`
- `networkctl status <link>`
- `networkctl lldp`

### Data Source Strategy

- use `--json=short` when available
- fall back carefully if the environment has no `systemd-networkd` data

### Notes

This command should integrate conceptually with the existing `ip` adapter, not fight it. `ip` remains the kernel/network stack view; `networkctl` is the systemd-networkd/service-layer view.

## `busctl`

### First Slice

Implement structured support for:

- `busctl list`
- `busctl tree`
- `busctl introspect`
- `busctl get-property`

### Data Source Strategy

- use `busctl list --json=short` for bus-name listings
- treat `tree`, `introspect`, and `get-property` as their own structured paths
- do not force everything through one flat JSON adapter

### Notes

`busctl` is slightly different from the other commands: it is more like a D-Bus exploration surface than a plain systemd admin command. It still belongs in the family, but it should feel like the “introspection/IPC” sibling.

## Shared UX Rules

### Display Profiles

All of these commands should follow the usual ToSh split:

- standalone object: rich record/detail view
- homogeneous row stream: table view
- nested values: concise summaries

### Filtering

Objects should be easy to filter with normal pipeline commands:

```tosh
systemctl list-units | where { _.ActiveState == active }
journalctl -u sshd.service | where { _.Priority <= 3 }
loginctl list-sessions | where { _.State == active }
```

### Native Fallback

Text-only or specialty modes that do not fit the object model yet should fall back to the real command instead of blocking progress.

That is especially reasonable for:

- `journalctl -f` at first
- `busctl monitor`
- `busctl capture`
- rare `systemctl` formatting switches

## Recommended Implementation Order

1. `systemctl`
   - `list-units`
   - `show`
   - a few basic action verbs

2. `journalctl`
   - structured entry objects
   - unit/time filtering

3. `loginctl`
   - sessions/users/seats

4. `hostnamectl`
   - status first

5. `busctl`
   - `list`
   - then introspection

6. `networkctl`
   - once we decide how much overlap we want with `ip`

## Recommendation

The best path is:

- familiar command names
- shared ToSh object/display behavior
- capability-probed structured adapters
- query paths first, actions second
- `systemctl` and `journalctl` as the first real implementation slice

That gives ToSh a unified systemd story without inventing a separate “parallel shell language” for Linux service management.
