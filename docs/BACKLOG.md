# ToSh Backlog

Open work items by area, roughly ordered by priority within each section.

Last updated: April 15, 2026.

## Language Surface

### `match` as pattern-matching syntax

`match` is currently a text-matching command. If pattern-matching syntax is added later, consider renaming the command to `match-text`.

## Type Surface

### Tuple and set literals

No first-class tuple or set literal surfaces exist. Acceptable for now but should be acknowledged.

## Unix Command Parity

### Adapters

| Command | Remaining gaps |
|---------|----------------|
| `ping` | — |
| `ip` | deeper subcommand coverage beyond addr/link/route/neigh/rule |
| `cp` | — |
| `lsblk` | — |

## Daily-Driver Hardening

- Performance under volume (large listings, sustained REPL use, startup cost)
- Native/object/text boundary polish

## TUI Platform

- Keep extracting reusable widgets from app-specific screens
- Build future full-screen tools on the shared runtime
- Form editors and structured input widgets
