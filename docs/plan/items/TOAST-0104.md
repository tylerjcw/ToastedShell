---
id: TOAST-0104
title: "A refinement type derived from a sibling in the same module silently fails to register"
status: complete
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

- [x] `export type Derived = Base where …` inside a module resolves Base to the sibling
- [~] A base that genuinely cannot be resolved is a diagnostic — **at the use site, not the
      declaration.** See below: bases resolve lazily and forward references are legal, so an
      eager check would refuse working code.
- [x] The diagnostic names the declaration and the unresolved base
- [x] Chained derivation of three or more levels works inside a module
- [x] Corpus covers both `= Base where …` and the `= Base { where … coerce … }` block form

## Fix — 2026-09-04

An alias now carries the module it was declared in, and its base is resolved there. This is the
device `ToshClassDefinition` already used around member invocation, for exactly the same reason:
the base is resolved where the alias is *used*, and by then the declaring scope has left the
stack. Installing it around the three places that walk to a base — the effective-type walk, the
conversion, and the known-type check — is the whole change.

Verified against the file that found it. `Types/StringTypes.tosh` had been worked around by
qualifying every base; with the qualifiers stripped back out, all of `TrimmedString`, `Slug`,
`AbsPath` and `SemVer` resolve. The workaround is no longer needed, and the file as it stands
still loads.

### The declaration-time check the item asked for would have been a regression

Forward references are legal — `type Derived = Base` resolves when `Base` is declared *after* it,
because bases resolve lazily. Checking eagerly at the declaration would refuse that, and a base
living in a module that loads later would fail too. Measured before writing anything, which is the
only reason the check was not written.

So the diagnostic moved rather than being dropped. It fires at the use site, as before, but says
what is actually wrong:

```
Type 'Broken' is declared over 'NoSuchBase', which does not name a type.
  'M.Broken' cannot be resolved because 'NoSuchBase' does not exist
  help: 'NoSuchBase' is the base of 'Broken'. Declare it, correct the spelling, or
        qualify it if it lives in another module.
```

rather than `'x' uses unknown type annotation 'M.Broken'`, which named the consumer. The chain is
walked to the deepest break, so a broken link three aliases down is the one reported.

### A second defect, found and not fixed here

`"hi" is M.T.Base` — a *qualified* refinement type in a type test — fails with
`Member 'Base' was not found on type 'ToshModuleObject'`. The right operand is evaluated as module
member access before `is` sees it, and a refinement type is not in a module's `Types` table.
`TOAST-0111` fixed the unqualified spelling; this is the qualified one, and it is filed separately
rather than folded in here.
