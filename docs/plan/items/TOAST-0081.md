---
id: TOAST-0081
title: "`const` freezes a name but not its object graph, so immutable data is still mutable through an alias"
status: proposed
area: toast
priority: 2
opened: 2026-08-28
---

## Problem

The specification deliberately makes `const` an immutable **binding**, not an immutable value:

```tosh
const Config = {| retries: 3, hosts: ["a"] |}
$Config.retries = 5                   # currently allowed
$Config.hosts[0] = "other"           # currently allowed through the nested list
```

That rule is useful—`const StartedAt = (date)` is evaluated once at runtime—but it cannot
represent configuration snapshots, compiler trees or values safe to share with another task.
`strict class` and `fixed prop` make properties read-only after initialization; they do not
recursively close aliases to mutable children.

## Candidate surface

```tosh
const base = freeze({| retries: 3, hosts: ["a"] |})
$base.hosts[0] = "other"              # error: frozen value

const tuned = $base with {| retries: 5 |} # persistent update; base is unchanged
var editable = thaw($tuned)               # explicit independent mutable copy
```

`freeze` is deep for Tōast data. It must not merely wrap a mutable list while another alias can
still change it. Cycles and shared subgraphs need a stated snapshot rule. Identity-bearing CLR
objects, live resources and arbitrary foreign values must be rejected or handled by an explicit
type adapter; pretending they became immutable would be worse than having no feature.

## Persistent values

A frozen collection should support efficient copy-on-write updates rather than requiring a full
deep clone for every edit. Records need `with` update—the missing shape already identified by
`TOAST-0048`—and lists, dictionaries and sets need corresponding persistent operations. Equality,
hashing, iteration order and rendering remain the ordinary Tōast contracts.

## Acceptance

- [ ] `freeze` produces a transitively immutable Tōast value; all mutation paths diagnose
- [ ] Existing aliases cannot mutate a supposedly frozen child behind the frozen root
- [ ] Cycles and shared subgraphs have specified, tested snapshot and identity behavior
- [ ] `thaw` creates an independent mutable graph and states how cycles/aliases are preserved
- [ ] Record `with` updates and persistent collection updates leave the original unchanged
- [ ] Persistent updates share unchanged storage where safe, with an allocation benchmark
- [ ] Foreign objects and affine resources are rejected unless their type supplies an explicit
      freeze adapter with honest semantics
- [ ] `const` keeps its current runtime immutable-binding meaning
- [ ] A `prefer-const`/`prefer-frozen` analysis can identify bindings and values never mutated,
      without changing program behavior
- [ ] Frozen values satisfy `TOAST-0086`'s automatic sendability rules
- [ ] Interpreter, compiler, formatter, LSP and specification agree on the new forms

## Dependencies

Record-update syntax is already called out by `TOAST-0048`. Compile-time values are deliberately
separate in `TOAST-0082`; a value can be deeply immutable without having been computed at build
time, and can be computed at build time without introducing a runtime binding at all.
