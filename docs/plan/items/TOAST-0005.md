---
id: TOAST-0005
title: "Split ToshEngine.cs and ToshParser.cs into partial classes by concern"
status: open
area: toast
priority: 2
opened: 2026-08-16
---

## Problem

Phase A2 of [the separation plan](../../TOAST_SEPARATION_PLAN.md).

| File | Lines |
|---|---:|
| `ToshEngine.cs` | 19,318 |
| `ToshParser.cs` | 13,931 |

Two files are two-thirds of the language project. Splitting them is not cosmetic: it
is how the language/shell boundary gets decided, because deciding which partial each
method belongs to *is* deciding which side it is on.

## Acceptance

- [ ] Both files split into partial classes by concern — statements, expressions, declarations, classes, native interop, diagnostics
- [ ] Every moved method moves **verbatim**; the diff is a move, not an edit
- [ ] The suite passes unchanged, before and after, with no test edited to accommodate the split
- [ ] Anything noticed while moving is **filed, not fixed** — list the filed items here
- [ ] `BuiltInDisplayProfiles.cs` (6,501), `DisplayEngine.cs` (4,308), `ToshClassDefinition.cs` (3,769), `ToshLanguageFeatures.cs` (3,363) and `ToshHost.cs` (3,042) assessed, and split or explicitly left alone with a reason

## The measured shape

Surveyed 2026-08-16 before touching anything, because the two files are not the same
problem.

**`ToshEngine.cs`** is already `public sealed partial class`, one type, no regions.
Splitting it needs no structural change — only moving members. Methods by domain:

| Concern | Members |
|---|---:|
| type system and refinements | 77 |
| argument binding | 33 |
| statements | 30 |
| classes, members, enums | 35 |
| commands and pipelines | 32 |
| diagnostics | 16 |
| modules and imports | 13 |
| native interop | 13 |
| scopes, operators, assignment, match | 41 |

Native interop is the smallest self-contained concern and the natural first slice —
but it is **not contiguous**: it sits at 2995–3192, 9998–10009 and 17608–17965. So
this is a redistribution method by method, not a set of cuts, and that is true of
every concern here.

**`ToshParser.cs`** is different and more involved. It is a `static class` with only
five top-level members; the actual parser is a **nested `private sealed class
InternalParser`** with 379 members. Splitting it means making *both* `partial` — a
nested partial needs its containing type declared partial in every file. Members:

| Concern | Members |
|---|---:|
| `Parse*` (all layers) | 163 |
| statements | 63 |
| **`LooksLike*` lookahead predicates** | **59** |
| arguments | 52 |
| expressions | 40 |
| operators | 35 |
| types | 24 |
| commands, declarations, members, literals, blocks | 76 |

**The 59 `LooksLike*` predicates are the reason to do the parser at all.** They are
the scatter `TOAST-0002` describes — the hand-maintained agreement that let `as`
(`TS-P2-105`), the unary operators (`TS-P2-116`) and `export flags enum` each break in
a way no single reviewer would catch. Collecting them into one file does not fix that,
but it is what makes a guard writable, and it makes the disagreement visible for the
first time. Sequence `TOAST-0002` immediately after this.

## Notes

The discipline is refusing to fix anything while moving it. A behavioural change
hidden inside a 19,000-line mechanical diff is the hardest thing to review and the
easiest place for a regression to survive.

Do it one concern at a time, building and running the suite between each, so every
commit is a reviewable move rather than one 33,000-line diff. The engine first, since
it needs no structural change; the parser second, because making a nested class
partial is a change to the file's shape as well as its contents.

`git mv` where whole files move, so blame follows.

`TOAST-0002` — the scattered statement-dispatch lookahead — is easier to guard once
the parser's scan sites are grouped, so sequence it after this rather than before.
