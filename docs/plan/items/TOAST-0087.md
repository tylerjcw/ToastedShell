---
id: TOAST-0087
title: "Side-effect metadata is descriptive only, so code cannot prove purity or enforce a capability boundary"
status: proposed
area: toast
priority: 2
opened: 2026-08-28
---

## Problem

TōSh command metadata can describe file reads/writes, network access, process spawning and free-form
permissions. The evaluator and type checker do not consume that metadata: a function cannot promise
it is pure, a caller cannot see transitive effects, and a sandbox cannot refuse an operation before
the command performs it.

The self-hosting RFC already depends on capability/effect checking for `core`, `os`, `native`,
`unsafe` and `clr`. That target-portability vocabulary needs to meet the shell's operational effects
in one semantic model rather than growing a second annotation system.

## Candidate surface

```tosh
pure func normalize(name: string) -> string {
    return $name.Trim().ToLowerInvariant()
}

func load(path: string) -> string effects [fs.read] {
    return (read-file $path)
}

capabilities [fs.read, network] {
    var config = load "app.json"
    http get $config.endpoint
}
```

`effects` is a statically checked declaration; `capabilities` is an authority boundary at execution.
A pure function has the empty effect set. Calls contribute effects transitively, while unresolved
dynamic calls conservatively require a declared dynamic/ambient capability rather than being assumed
safe.

## One source of truth

Builtin command annotations, Tōast function metadata, target profiles, LSP hover, MCP metadata and
runtime enforcement must derive from one registry. Initial operational effects should distinguish at
least `fs.read`, `fs.write`, `env.read`, `env.write`, `network`, `process`, `terminal`, `native`,
`unsafe` and `clr`; aliases/aggregation may present these more simply to users.

A capability token limits what code may do; it is not a claim that an operation is harmless. Native
or CLR calls still cross the unsafe/foreign boundary even when authorized.

## Acceptance

- [ ] The specification defines effect sets, capability grants and their relationship
- [ ] `pure func` is rejected when its body or transitive callees may perform an effect
- [ ] Public functions can declare an effect upper bound; implementations and overrides cannot exceed it
- [ ] Effects are inferred within a source/module and exported in typed metadata across module boundaries
- [ ] Builtin `CommandSideEffects`/permission metadata maps into the same registry
- [x] A coverage audit of the existing builtin metadata (step 1, below)
- [ ] Unannotated commands read as "unknown", never as "no effects"
- [ ] A guard test requires an effect declaration on every `ShellCommand`, so coverage cannot regress
- [ ] Dynamic dispatch has a conservative, explicit rule and cannot silently launder a capability
- [ ] A capability scope or invocation policy denies unauthorized operations before their side effect
- [ ] Denials identify the missing capability and the static/dynamic call path that required it
- [ ] RFC target capabilities (`core`, `os`, `native`, `unsafe`, `clr`) compose with operational effects
      rather than being checked by a separate source scan
- [ ] `TOAST-0059`'s unsafe blocks and native operations require the corresponding capability
- [ ] `TOAST-0082` compile-time calls accept only a proven empty effect set
- [ ] Interpreter, compiler, LSP/MCP metadata and sandbox integration tests agree

## Ordering

First normalize and audit builtin metadata; then add effect inference/checking; then enforce runtime
capability tokens. Enforcement before coverage would create a sandbox whose omissions look like safety.

## Step 1 — audit complete (2026-08-28)

`docs/plan/TOAST-0087_effect_audit.md`, repeatable via `scripts/effect-audit.sh`. Of 252
`ShellCommand` classes:

- 15 (6%) declare `[CommandSideEffects]`; **8 (3%)** declare something consistent with their own
  source. Seven of the fifteen under-declare — `http` declares `network` while also writing files
  and reading the environment.
- 52 perform observable effects and declare nothing, including every core file command
  (`read-file`, `write-file`, `head`, `tail`, `stat`, `tee`, …) and 14 commands that spawn
  processes. `kill` is the only command in the shell declaring `SpawnsProcess`.
- `native`, `env.read`, `env.write`, `terminal` and `clr` **cannot be declared at all** — the
  metadata is four booleans. `load-assembly`, which loads arbitrary CLR assemblies, therefore has
  empty metadata.
- `[CommandPermission]` is applied by **zero** commands, though `CommandMetadata` carries the list
  and LSP hover renders it.
- `ShellCommand.Describe` leaves `SideEffects` null when unannotated, so absence reads as purity
  for 94% of the shell.

Counts are a floor: commands delegate through helpers (`read-file` reads via `FileIoUtilities`),
which a source-local scan cannot see.

The audit revises this item's shape. Normalization is not an annotation pass over a
mostly-correct corpus but a from-scratch classification against a vocabulary that does not exist
yet, and it needs two things the original acceptance list does not name: an explicit "unknown"
state so absence cannot read as pure, and a guard test asserting every `ShellCommand` carries a
declaration, without which the corpus decays back to 6%. Both are added below.

**Decided 2026-08-29: the vocabulary is a closed enum**, with grouping and aliases for display
only. The checker matches it exhaustively, `pure` is the empty set, and the classification of
252 commands is mechanical. See `DECISIONS.md`.
