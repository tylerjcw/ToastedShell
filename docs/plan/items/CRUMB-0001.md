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
- [x] A config file — **at `~/.config/crumb/crumb.tosh`, and it is ToastScript**, not the TOML this row proposed; see below
- [ ] Conflict-resolution UX beyond binary proceed/abort: name the installed package the conflict is with, and offer remove-or-skip per conflict
- [ ] Pacnew/pacsave detection after install
- [ ] Downgrade support via the Arch archive
- [x] `--aur-base-url` wired to the CLI — `AurClient` already takes the constructor argument
- [ ] Implicit behaviour documented: pager precedence (`pagerOverride` > `CRUMB_PAGER` > `PAGER` > `less`), pacman-flag expansion, format-flag last-wins
- [x] `crumb --help` lists no command that throws "not implemented" — it never did, and
      `-Qd` is now implemented rather than throwing, so one fewer exists to hide

## Audit — 2026-09-12

A read of the whole 4,900 lines, driven by differential-testing against `pacman` rather
than by reading alone.

### One real bug: `-Qt` over-reported orphans

**80 against `pacman -Qdt`'s 43** on the author's machine — 37 packages it would have
told someone they could safely delete. The `needed` set was the literal `Depends` of
every installed package, which misses two things:

- **A dependency may be satisfied under another name.** `lutris` depends on `p7zip`;
  `7zip` *provides* it, so `7zip` looked unwanted.
- **An optional dependency still counts**, and `pacman` writes them `name: description`,
  so the description has to come off before the name matches anything.

Now 42, with **zero false positives** — the only remaining difference from pacman is one
package Crumb keeps *off* the list. That is the safe direction, and deliberate: provides
are matched on the bare name, so two providers of the same soname at different versions
both stay. This list is what someone reads before deciding what to delete, so erring
towards "still wanted" costs a missed cleanup while erring the other way costs a broken
package.

Reporting only — `remove --recursive` builds a `-Rs` and hands it to pacman, so the
destructive path never used this.

### What the audit found to be sound

- **No shell injection surface.** Zero uses of `ProcessStartInfo.Arguments`, 25 of
  `ArgumentList`, no `/bin/sh`, no `UseShellExecute = true`. For a tool that takes
  package names off the internet and runs builds, that is the property that matters.
- **Package sets match pacman exactly** — all (2460), foreign (139), explicit (486) —
  as do `-Qi` fields on every sample checked.
- **`Vercmp` delegates to pacman's own binary** rather than hand-rolling alpm's rules.

### Three smaller fixes

- **`--review` was nearly a no-op.** Review is off unless `--review` or `CRUMB_REVIEW=1`
  asks for it — deliberate — but the `:: Review N PKGBUILDs?` prompt then defaulted to
  *no*, so anyone answering with Enter reviewed nothing. Having explicitly asked, the
  second gate now defaults to yes. A PKGBUILD is arbitrary code about to run as you, and
  reading it is the only control there is.
- **`Vercmp` spawned a process per comparison** — 139 foreign packages per `-Syu`. It
  now short-circuits on equal strings, which is most of them, and gained a five-second
  timeout and a non-deadlocking read of both pipes.
- **`-Qd` threw "not implemented"** while `-Qt` computed `InstallReason == "depend"` one
  line away. Implemented; matches `pacman -Qd` exactly at 1974.

## The config file is ToastScript — 2026-09-12

This row proposed TOML. It was built that way, including a hand-written parser for a
TOML subset, and then **changed on the author's question: why not use ToastScript?**

The right answer, and the measurements say so:

| | |
|---|---|
| `crumb -Qd` today | ~334 ms (it parses 2,460 local packages) |
| booting the whole `tosh` CLI | ~194 ms |
| engine assemblies added | ~3.7 MB against an 80 MB self-contained binary |

The start-up objection does not survive the numbers, and the dependency is 4%. Crumb's
own description is "a TōSh-native pacman + AUR wrapper" — configuring it in a format
invented for the purpose was the odd thing, not the native one. A second, worse
configuration language with a parser of its own is also exactly the kind of thing the
rest of this session kept finding bugs in.

The file's value is a record, read with the language engine:

```tosh
# ~/.config/crumb/crumb.tosh
var host = "valinor"

{|
    quiet        = false
    pager        = "bat"
    review       = true
    exclude      = ["linux", $"{$host}-kernel"]
    makepkgFlags = ["--skippgpcheck"]
|}
```

That last line is the part TOML could not do: a setting can be *computed*.

**A bare `ToastRuntime`, not the shell.** The built-in command set is deliberately left
out, so a config file cannot shell out during start-up and the engine Crumb carries is
the language only.

**Nothing is read unless the file exists**, so `crumb -Q` pays nothing for a feature
nobody configured. A file that fails to evaluate is a warning and the defaults — a
package manager that refuses to run over a typo in a preference file is worse than one
that says so and carries on.

Precedence is flag > environment variable > file > built-in default, so the file never
takes an option away from the command line. Wired to `quiet`, `verbose`, `review`,
`pager`, `truecolor`, the `-Syu` exclude list and `makepkg` flags.

**Verified:** excluding an AUR package drops it from the upgrade list (9 rebuilds → 8,
`zen-browser-bin` held back). The repo half hands its names to pacman's own `--ignore`
rather than filtering them here, so pacman's dependency resolution sees the hold — that
half is unobserved, because this machine had zero repo upgrades pending when it was
tested.

## Notes

Explicit non-goals, so they are not re-proposed: no mirror ranker (pacman owns
mirrors), no alpm FFI binding (the on-disk DB parser is sufficient), no repo management
— Crumb is a client, not an admin tool.

The config file is the highest-leverage remaining box. Everything is environment
variables today (`CRUMB_SUDO`, `CRUMB_PAGER`, `CRUMB_REVIEW`, `CRUMB_NO_TRUECOLOR`,
`CRUMB_NO_COLOR`, `TOSH_TTY`), which is fine for one machine and poor for persisting
preferences.

## `--aur-base-url` — 2026-09-20

`AurClient` took a `baseUrl` from its first day and nothing ever passed one, so a mirror or
a test double had no way in. Wired the way the pager already is, because it is the same
shape of question:

```
--aur-base-url  >  CRUMB_AUR_BASE_URL  >  the config file's aurBaseUrl  >  the real AUR
```

One shorter than the pager's chain, which has a system-wide `PAGER` to sit below the file;
there is no system-wide AUR endpoint to defer to.

`AurClient` defaults through the resolver rather than straight to `DefaultBaseUrl`, and the
flag is published as `CrumbConfig.AurBaseUrlOverride`, set once in `Program` before any work
starts. That is a process-wide value rather than a threaded parameter deliberately: of the
five places an `AurClient` is built, **one** has the parsed options in scope, and giving the
other four a string they have no other reason to know about would be a worse change than the
static. `CrumbConfig.Current` is already read the same way.

### A correction to this item

The remaining documentation bullet states the pager precedence as
`pagerOverride > CRUMB_PAGER > PAGER > less`. The code is
`explicit > CRUMB_PAGER > the config file > PAGER > less` — the file is missing from the
bullet. Worth having the right chain before it is written down, since writing down the wrong
one is how the bullet's own problem started.
