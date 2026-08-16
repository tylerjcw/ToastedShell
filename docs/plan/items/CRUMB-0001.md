---
id: CRUMB-0001
title: "Crumb polish: config file, conflict-resolution UX, and four optional features"
status: partial
area: crumb
priority: 3
opened: 2026-05-13
---

## Problem

The polish batch identified after the TSSP work and the upgrade/install/removal UX
pass. None of it blocks daily use; it is ordered roughly by leverage.

Most of the batch has landed — `-Sw` stub handling, phase-splitting `UpdateAsync` and
`InstallAsync`, `crumb logs`, `--limit N`, centralised colour detection, startup cache
validation, and focused test coverage. What is below is the remainder.

## Acceptance

- [x] Honest stub handling — `-Sw`, `-Suw`/`-Syuw`, `-U <file...>`
- [x] `UpdateAsync` and `InstallAsync` split into phase methods
- [x] `crumb logs` with `--pkg`, `--tail`, `--clean`, `--limit`, `--dry-run`
- [x] `--limit N` in search and news
- [ ] A config file at `~/.config/crumb/crumb.toml` with env-var overrides — persisting build flags, default `--quiet`/`--verbose`, an exclude list for `-Syu`, pager and truecolor preference
- [ ] Conflict-resolution UX beyond binary proceed/abort: name the installed package the conflict is with, and offer remove-or-skip per conflict
- [ ] Pacnew/pacsave detection after install
- [ ] Downgrade support via the Arch archive
- [ ] `--aur-base-url` wired to the CLI — `AurClient` already takes the constructor argument
- [ ] Implicit behaviour documented: pager precedence (`pagerOverride` > `CRUMB_PAGER` > `PAGER` > `less`), pacman-flag expansion, format-flag last-wins
- [ ] `crumb --help` lists no command that throws "not implemented"

## Notes

Explicit non-goals, so they are not re-proposed: no mirror ranker (pacman owns
mirrors), no alpm FFI binding (the on-disk DB parser is sufficient), no repo management
— Crumb is a client, not an admin tool.

The config file is the highest-leverage remaining box. Everything is environment
variables today (`CRUMB_SUDO`, `CRUMB_PAGER`, `CRUMB_REVIEW`, `CRUMB_NO_TRUECOLOR`,
`CRUMB_NO_COLOR`, `TOSH_TTY`), which is fine for one machine and poor for persisting
preferences.
