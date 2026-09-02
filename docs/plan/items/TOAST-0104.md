---
id: TOAST-0104
title: "A refinement type derived from a sibling in the same module silently fails to register"
status: proposed
area: toast
priority: 2
opened: 2026-08-30
---

## Problem

Inside a module, a declared type whose base is another declared type in that same module is
accepted at parse time, loads without a complaint, and then does not exist:

```tosh
partial module M {
    partial module T {
        export type Base        = string where _.Length > 0
        export type Unqualified = Base where _.Length < 10        # silently lost
        export type Qualified   = M.T.Base where _.Length < 10    # fine
    }
}
```

```
❯ var b: M.T.Qualified = "hi"     # ok
❯ var c: M.T.Unqualified = "hi"
✖  tosh.runtime.annotation_unknown_type
│  'c' uses unknown type annotation 'M.T.Unqualified'.
╰─ help: define the type first, or use a known CLR/shell type name.
```

At the top level, outside a module, the same derivation works — so it is the combination of
module nesting and an unqualified base name.

## Why it matters

There is no diagnostic at the declaration. `require` reports success, the module reports no
error, and the type is simply absent. The failure surfaces later and somewhere else, as
"unknown type" at a use site, which points at the consumer rather than the declaration.

Found in `~/.config/tosh/lib/Types/StringTypes.tosh`, where a chain of derivations —
`SingleLine → TrimmedString → {EmailLike, HttpUrl, SemVer, SafePath, AbsPath, Slug}` — meant
**seven of ten declared types did not exist**. The file had been in the profile for a month.
Qualifying each base to `ToastLib.Types.X` fixes all seven, and the refinements then both accept
and reject correctly, so nothing but the name resolution was wrong.

## Relationship to the other resolution items

This is the third place where a name resolves differently depending on whether it is qualified:

- `TOAST-0102` — an unqualified capitalised callee across files changes how the call *parses*
- Plot's load-order rule — an unqualified call binds when its file loads, a qualified one late
- this — an unqualified base type in a module declaration resolves to nothing at all

They may share a cause. This one is the worst of the three because it is silent.

## Acceptance

- [ ] `export type Derived = Base where …` inside a module resolves Base to the sibling
- [ ] A base that genuinely cannot be resolved is a diagnostic at the declaration, not silence
- [ ] The diagnostic names the declaration and the unresolved base
- [ ] Chained derivation of three or more levels works inside a module
- [ ] Corpus covers both `= Base where …` and the `= Base { where … coerce … }` block form
