# ToSh Documentation

This directory is split into three layers:

- **Reference**: the user-facing language, command, pipeline, type, and configuration docs
- **Current state**: what is implemented today and what should happen next
- **Design notes**: focused architecture docs for subsystems that are still evolving

The in-shell help system is also a live source of truth:

- `help <topic>`
- `help search <text>`
- `help --cli`
- `help browse`

## Start Here

- [Getting Started](reference/GETTING_STARTED.md)
- [Language Reference](reference/LANGUAGE.md)
- [Command Map](reference/COMMANDS.md)
- [Pipeline Model](reference/PIPELINES.md)
- [Type System](reference/TYPES.md)
- [CLR Interop](reference/CLR_INTEROP.md)
- [Configuration Reference](reference/CONFIGURATION.md)

## Current State

- [Status](STATUS.md)
- [Roadmap](ROADMAP.md)

## Practical Guides

- [Configuration Guide](CONFIGURATION.md)
- [Editor Support](EDITOR_SUPPORT.md)
- [Runtime Namespaces](RUNTIME_NAMESPACES.md)

## Design Notes

- [Architecture](ARCHITECTURE.md)
- [TUI Architecture](TUI_ARCHITECTURE.md)
- [Config Browser](CONFIG_BROWSER.md)
- [Functional Language Design](FUNCTIONAL_LANGUAGE_DESIGN.md)
- [Systemd Family Design](SYSTEMD_FAMILY_DESIGN.md)
- [Stream Management](STREAM_MANAGEMENT.md)
- [Tabular Summary](TABULAR_SUMMARY.md)
- [Unix Command Audit](UNIX_COMMAND_AUDIT.md)
- [CLR Display Backlog](CLR_DISPLAY_BACKLOG.md)

## Historical / Audit Notes

These are useful engineering notes, but they are not the best first stop for day-to-day usage docs:

- [Builtin Pipeline Audit](BUILTIN_PIPELINE_AUDIT.md)
- [Language Surface Audit](LANGUAGE_SURFACE_AUDIT.md)
- [Type Surface Audit](TYPE_SURFACE_AUDIT.md)
