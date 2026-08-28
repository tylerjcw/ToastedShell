---
id: TOAST-0007
title: "Split Tosh.Stdlib into language-level and shell-level commands"
status: partial
area: toast
priority: 2
opened: 2026-08-16
---

## Problem

Phase A4 of [the separation plan](../../TOAST_SEPARATION_PLAN.md). Already grouped by
category, which makes it unusually tractable:

| Language-level (moves to Tōast) | Shell-level (stays TōSh) |
|---|---|
| Pipeline (4,327), Clr (2,213), Text (1,748) | Filesystem (6,279), Sys (4,203) |
| Concurrency (1,045), Functional (648) | Shell (3,754), Net (1,931) |
| Time (615), Data (507), Maths | Processes (1,636), Display, Tssp |

`map`, `where`, `count` and `sort` are as much part of Tōast as `for` is. `ls`, `ps`
and `systemctl` are not.


## Registrar division — 2026-08-28

`RegisterDefaults` is now the composition of `RegisterLanguageDefaults` and
`RegisterShellDefaults`, so an existing host sees the table it always did while the two halves
become separately addressable. **178 language registrations, 97 shell**, all 275 preserved.

### The line was drawn from evidence, not from where a file sits

A command can only obtain a `ToshRuntime` through `context.Shell()` or
`RequireCommandHost<ToshRuntime>()`. Scanning for those two gives a *lower bound* on what must
be shell-side, and it confirmed every reclassification this item had argued by hand:

| category | reaches the host | of |
|---|---:|---:|
| Shell | 27 | 34 |
| Filesystem | 19 | 45 |
| Sys | 11 | 19 |
| Processes | 8 | 8 |
| Scripting | 3 | 4 |
| Net | 2 | 4 |
| Concurrency | 2 | 12 |
| Pipeline | 1 | 58 |
| Text | 1 | 17 |
| Clr, Data, Time, Maths, Functional | 0 | 52 |

Pipeline's one is `InspectCommand`. Text's one is `WordCountCommand`. Concurrency's two are
`SpawnCommand` and `ScopeCommand`. Those are the three reclassifications the 2026-08-25 note
named, arrived at independently.

### The judgements above the lower bound

**`Sys` stays shell wholesale — the question this item asked, answered.** Every command in it
names a shell verb: `uname`, `hostname`, `whoami`, `id`, `free`, `uptime`. A language reaches
that information through the CLR bridge, which is already language-level, so moving them would
duplicate what `new` and `call` already reach. `seq` and `guid` go with them for the same
reason — `1..10` and `System.Guid.NewGuid()` are the language spellings.

**`Net` stays shell.** `ping` needs no shell mechanically, but ICMP and HTTP are library
territory, not language.

**`Filesystem` splits, as the 2026-08-17 correction said it must.** Language-level: the `Stream`
surface (`open`, `close`, `read-from`, `write-to`, `flush`, `seek`, `position`, `length`,
`copy-to`), the `File` surface (`read-file`, `read-lines`, `write-file`, `append-file`,
`read-bytes`, `write-bytes`), the `Path` surface (`dirname`, `basename`, `realpath`,
`readlink`), `mkdir`, the temp-file pair, and the `exists` / `is-file` / `is-dir` / `is-link`
predicates. Shell-level: `ls`, `df`, `du`, `stat`, `tree`, `lsblk`, `findmnt`, `find`, `glob`,
`cat`, `pwd`, `cd`, `chmod`, `chown`, and — for now — `cp`, `mv`, `rm`, `touch`, `link`, which
resolve against the shell's working directory rather than being shell verbs in themselves.

**`Processes` stays shell wholesale**, which is the other question the correction asked. All
eight reach the host, and the category is job control and process listing. A language that
needs to *spawn* a process reaches external execution, which is a different mechanism.

### One command was language-level in name only

`read-file` and `write-file` failed in a bare host despite their own sources naming no shell.
The dependency reached them through `FileIoUtilities.ResolveRequiredPath`, which asked
`context.Shell()` for the directory to resolve a relative path against.

That did not need the shell. TōSh keeps a working directory per session — what `cd` moves — and
a host with no session has the process's directory, which is the only answer there is. The
helper now falls back, and the item's central reclassification is true rather than aspirational:
a self-hosting Tōast can read its own source.

The first version of the source scan passed while this was still broken, because it read each
command's own file. It follows shared helpers one level now, and the negative control that
restores the old resolver fails both the scan and the behavioural test.

## What is left

The two sets are separately addressable but still one assembly. Moving them is now mechanical —
a relocation rather than a behavioural change, which is what this slice was for.

## Acceptance

- [x] Categories divided as above, with every reclassification argued above — and each one
      cross-checked against which commands actually reach the shell host
- [x] Language-level commands work in a host with no shell present — a scan covering all of
      them, and behavioural tests for a pipeline and for file IO
- [x] Command help, metadata and completion still resolve across both halves — the composed
      registrar builds one table, and the nine help/metadata/completion test files pass
- [x] The suite passes unchanged — 6,751, no test altered
- [ ] The two sets live in separate projects, with the language half free of any shell
      reference — the physical move this item's title names, now a relocation

## Correction 2026-08-17: `Filesystem` splits *within itself*

The table above files `Filesystem` (6,279) shell-side as a block, on the rule that "`ls`,
`ps` and `systemctl` are not [language]". That rule is right about `ls`; it is wrong
about the category.

`read-file`, `write-file`, `open` and `close` are **language-level**. A self-hosting
Tōast has to read its own source, and any program in a systems language opens files —
that is `FILE*` in C and `Stream` in C#, not a shell verb. `ls`, `df`, `du`, `chmod` and
`chown` are shell verbs.

So `Filesystem` is not assignable as a block: it holds both, and the split runs through
it. The same question should be asked of `Sys` (4,203) and `Processes` (1,636) before
either is moved wholesale — a self-hosting language needs to spawn a process, even if it
does not need `systemctl`.

## Notes

The borderline cases are worth deciding explicitly rather than by where a file
currently sits: `Time` and `Data` read as language, `Net` reads as shell, and `Clr` is
language only because the CLR bridge is how Tōast reaches types at all.

## State after 2026-08-25

Boundary preparation in `TOAST-0006` proved the first language-side groups in a host with no
`ToshRuntime`: CLR, Data/hash, Time, Pipeline (except `inspect`), Text (except `wc`'s display
override), Functional runtime helpers, and pure Concurrency (`async`, `race`, `settle`,
`timeout`). `spawn` and `scope` now explicitly recover TōSh's opaque command host because they
manipulate its concrete job table.

Two reclassifications are explicit:

- `inspect` is shell-side even though its directory says Pipeline. Its defining behavior is an
  interactive inline tree browser and its fallback is a shell object inspector.
- `wc`'s counting core is language-side, but `--show`/`--hide` currently attach preferences to
  TōSh's display engine. Split that presentation decoration from the counter instead of moving
  a display-selection type into the language contract.

The next implementation slice is to divide `BuiltInCommands.RegisterDefaults` into explicit
language and shell registrars while it is still one assembly. The full registrar remains their
composition for compatibility. Once callers and tests choose a registrar deliberately, the two
sets can move to separate projects without a behavioral diff; this is also the prerequisite for
removing `ToshEngine(ToshRuntime)` and completing `TOAST-0006`'s physical assembly acceptance.
