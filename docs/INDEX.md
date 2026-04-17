# TōSh Documentation

## Language Specification

The authoritative language reference is the LaTeX spec:

- [ToastScript Specification](spec/ToastScript.pdf) — grammar, types, operators, pipelines, commands, CLR interop, modules, events

The command reference is auto-generated at build time into [command-reference.tex](spec/command-reference.tex).

## In-Shell Help

The shell itself is a live source of truth:

- `help <topic>`
- `help search <text>`
- `help --cli`
- `help browse`

## Project Direction

- [Roadmap](ROADMAP.md) — vision, completed phases, and current direction
- [Backlog](BACKLOG.md) — open work items by area

## Design & Architecture

- [Architecture](ARCHITECTURE.md) — core design philosophy and invariants
- [Configuration](CONFIGURATION.md) — startup order, config command, live settings
- [Editor Support](EDITOR_SUPPORT.md) — VS Code extension and LSP
- [Runtime Namespaces](RUNTIME_NAMESPACES.md) — `$tosh` namespace structure
- [TUI Architecture](TUI_ARCHITECTURE.md) — terminal UI platform design
