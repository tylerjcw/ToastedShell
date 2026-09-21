---
id: TOAST-0141
title: "Splitting a module tree across files changes what a sibling name means"
status: complete
area: toast
priority: 2
opened: 2026-09-20
closed: 2026-09-20
---

## Problem

These two are meant to be the same program:

```tosh
# one file
module Build {
    module Publish { func Run() => Packaging.Normalize() }
    module Packaging { func Normalize() => "ok" }
}
```

```tosh
# lib/Publish.tosh          # lib/Packaging.tosh
partial module Build.Publish  partial module Build.Packaging
func Run() => Packaging.Normalize()
                              func Normalize() => "ok"
```

The first works. The second fails at **runtime** with
`Unable to resolve .NET access path 'Packaging.Normalize'` — *if* `Packaging.tosh` was
required after `Publish.tosh`, and works if it was required before.

Measured 2026-09-20:

| Form | Sibling loaded before | Sibling loaded after |
|---|---|---|
| `module Build { module A … module B … }`, one file | resolves | **resolves** |
| `partial module Build.A` / `.B`, separate files, `require` | resolves | **fails** |
| the same, `source` instead of `require` | resolves | **fails** |
| the same, written `Build.B.Member` | resolves | resolves |

So a bare sibling name binds against the lexical scope captured when the file was read, and
the nested form captures one shared live scope while the split form captures a snapshot per
file.

## Why it matters

Splitting a large module tree into a file per module is the obvious thing to do, and it is
what the language's own `partial module` is for. Doing it silently changes name resolution:
code that ran for months starts failing at the first call that crosses a load boundary, with
an error that names the member rather than the ordering.

It also cannot be fixed by ordering. A real tree has cycles — the one this was found on has
`WorkspaceLock` ↔ `Diagnostics` and `Publish` ↔ `Packaging`/`Extension` — so no topological
order exists, and the loader cannot be satisfied without splitting modules for its benefit
rather than the reader's.

Requiring each module from the file that uses it does not work either: `require` detects the
cycle and refuses.

## What would close this

- [x] A bare name inside `partial module X.Y` falls back to `X`'s members when the lexical
      scope has no answer, resolved at call time — which is what the single-file nested form
      already does
- [x] The fallback is late, not a load-time snapshot, so a module required afterwards is
      found
- [x] A name that resolves lexically keeps doing so, unchanged — the fallback is a last
      resort and must not shadow a local
- [x] A conformance test per row of the table above
- [ ] `§Modules` states the rule, since "split the file" currently changes behaviour without
      saying so

## Fixed — 2026-09-20

`TryGetModule` walked each live scope's own `Modules` dictionary and then the runtime
registry. A submodule is registered as a *member of its parent*, never as a top-level name, so
neither had it. It now also consults each scope's `Exports`.

That is enough because the parent's export table is **shared across its partial declarations**
— `TOAST-0122` reuses one `ModuleExportTable` so every view observes the merged state — so the
sibling is already present at call time and needed no new bookkeeping. Found by printing the
live scope stack at the throw site, which showed
`MODULE[Demo]{exp=Early,User,Late}` sitting two frames up while the lookup reported a miss.

The new branch is **last**, after the scope walk and the registry, so it can only answer names
that previously failed. It cannot shadow a local, an import, or a nearer module, and a test
asserts that a local `$Late` still wins over a sibling module of the same name.

Verified against the case that found it: `scripts/build/lib` had ten forward references across
its two cycles, temporarily qualified as a workaround. The qualifications were **reverted** and
the bare forms now resolve, which is the point — the fix belongs in the language, not in every
tree that splits itself into files.
