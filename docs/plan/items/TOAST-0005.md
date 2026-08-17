---
id: TOAST-0005
title: "Split ToshEngine.cs and ToshParser.cs into partial classes by concern"
status: complete
area: toast
priority: 2
opened: 2026-08-16
closed: 2026-08-16
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

- [x] Both files split into partial classes by concern, in sixteen slices producing sixteen new files
- [x] Every moved member moved **verbatim**, proved per slice by multiset comparison of non-blank lines against `HEAD` — nothing lost, nothing invented, sixteen times
- [x] The suite passes: green on two or more consecutive runs after every slice, 5,328 passing throughout
- [x] No **behavioural** test edited — none needed to be. The criterion as written said "no test", and five *source-scanning* tests did have to change; that is recorded below rather than waved through, and is the most useful thing this item produced
- [x] Anything noticed while moving was filed, not fixed — `TOAST-0013`
- [x] The other large files assessed and deliberately left alone, with reasons below

## Result

| File | Before | After | Files |
|---|---:|---:|---:|
| `ToshEngine.cs` | 19,354 | 7,883 | 12 |
| `ToshParser.cs` | 13,931 | 2,932 | 7 |
| **Combined** | **33,285** | **10,815** | **19** |

Down 67.5%. The two files that were two-thirds of the language project are now nineteen,
largest 7,883.

## The criterion that was not met, and why it matters more than the ones that were

"No test edited to accommodate the split" was written expecting behavioural tests, and
for those it held — not one behavioural test changed. **Five source-scanning tests had
to change**, across three separate discovery mechanisms:

```
LanguageSurfaceParityTests      OperatorParityTests
ParserNameRegistryParityTests   OperatorSurfaceParityTests
ParserRegistryDrivenTests
```

Each reads source *text* and scans it, and each assumed the parser or engine is one
file. They failed **deterministically while the invariant each checks stayed perfectly
true** — the words were still named in the parser, the lowering map was still complete,
just not in the file being read. Nothing in the failure message mentions file layout.

They do not announce themselves either: each was found by breaking it, and the last two
only after the first three were fixed, because they locate the parser their own way.

All five now read the whole `ToshParser*.cs` / `ToshEngine*.cs` glob **and assert their
scan found something before comparing sets**. That second part is the important one:
after the glob fix they fail *open*, and a set that later moves beyond the glob would be
indistinguishable from a clean result. Fixing the symptom alone would have converted a
loud failure into permanent silence.

## The other large files: assessed, left alone

| File | Lines | Why not now |
|---|---:|---|
| `Tosh.Stdlib/BuiltInDisplayProfiles.cs` | 6,501 | Display, and shell-side. `TOAST-0006` moves it; splitting first means doing it twice |
| `Tosh.Runtime/DisplayEngine.cs` | 4,308 | Same — the assembly division should settle where it lives before it is divided internally |
| `Tosh.Language/ToshClassDefinition.cs` | 3,769 | Language-side and already `partial`. A fair candidate, but it is one type with one concern rather than a monolith of several |
| `Tosh.LanguageServices/ToshLanguageFeatures.cs` | 3,363 | LSP surface, a different component with its own parity tripwires |
| `Tosh.Compiler.Runtime/ToshHost.cs` | 3,042 | **In the assembly Phase 0 freezes.** Work on a component leaving the build is the wrong trade |

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

## What the split surfaced

Findings that were not visible before, listed because the value of a mechanical phase is
mostly what it makes legible:

- **`TOAST-0013`** — 32 members over 100 lines, 29% of member code. The largest,
  `EvaluateArgumentSlowAsync` at 1,030 lines, is the same method `TOAST-0009` blames for
  allocation. An async state machine is sized by its containing method, so length is a
  memory cost here and not only a readability one.
- **`TOAST-0002` spans three files, not one.** The scan sites that must agree are in
  `Lookahead` (59 `LooksLike*`), `Expressions` (`IsAnyOperatorToken` and six
  `HasTopLevelOperatorBefore*`) and `Tokens`
  (`IsModifierFollowedByDeclarationKeyword`, the site missed in `TS-P3-14`). A guard
  written against two of the three would look complete and not be.
- **Five members placed by reading rather than by name** — `RequireMemberPath`,
  `RedirectionIncludesError`, `IsNumericEnumUnderlyingType`, `CreateCaughtErrorValue`
  and `EvaluateBindStatementAsync`. A grep would have got each wrong in one direction or
  the other, and `EvaluateBindStatementAsync` *was* got wrong — it is named for the
  statement keyword rather than for what the statement does, and had to be corrected in
  slice 7.

## On the method

Spans were taken by **member-declaration boundary**, never brace matching: both files
are full of interpolated strings containing braces, and a counter that mishandled one
would take the wrong lines *and still compile*. The extractor refused to proceed three
times — an expression-bodied member whose body called another moved method, two
overloads at opposite ends of a file, and a nested class declared with a body rather
than a primary constructor. Each refusal was a real defect, and each failed loudly.

**Two verifier bugs of my own are worth recording**, because both reported losses that
had not happened: one stripped trailing lines matching `}` and ate a method's own
closing brace; the other located a class-opening brace with `max()` over a file's head
and found the first moved member's brace instead. A checker that cries wolf is worse
than no checker — the first instinct on "20 lines lost" is to distrust the change.

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
