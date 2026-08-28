---
id: TOAST-0002
title: "Statement dispatch is decided by scattered lookahead predicates that must agree by hand"
status: partial
area: toast
priority: 2
opened: 2026-08-16
---

## Problem

`TS-P2-11` proposed a Pratt rewrite of the expression layers, measured it, and
recommended against it — the expression cascade is already an explicit nine-level
precedence chain with correct associativity. But it found the scatter it was aimed at
is real and sits somewhere else, and said so: **statement dispatch**, which a Pratt
expression parser does not touch. This is that remainder, filed where it lives.

Counted in `src/Tosh.Language/Parsing/ToshParser.cs` (13,931 lines):

| | Count |
|---|---:|
| `LooksLike*` predicates | 59 |
| `Is*Token` predicates | 27 |
| `Peek(` lookahead sites | 229 |

The cost is not the count, it is that these must **agree with one another by hand**,
and the board records what happens when they do not:

- `TS-P2-105` — `as` was added to the precedence chain, but not to every "does this
  look like an expression?" scan. A bare `$x as int` stopped parsing as an
  expression while every case with a second operator still worked, so the corpus
  missed it.
- `TS-P2-116` — the unary operators could not open a statement. The binary operators
  are found by scans that look for an operator *after* the leading token, and a
  unary operator **is** the leading token, so no scan could ever have seen it.
  `not true` was read as a command name; `var x = not true` bound nothing, printed
  nothing and exited 0.
- Adding the six bitwise word operators (`TS-P3-14`) meant editing seven separate
  hand-maintained scan sites, and `export flags enum` still failed to parse because
  one modifier list was missed. The bare form worked, which is why the corpus did
  not catch it.

Three defects of the same shape, each found by hand, each after the fact.

## Acceptance

- [x] The invariant established and stated behaviourally: **every operator in `OperatorSurface` must parse in every syntactic position**, rather than "every site lists every predicate" — the sites legitimately differ, and comparing their lists would fail on correct code
- [x] A tripwire exists: `OperatorStatementCorpusTests`, 276 assertions driven from the registry, failing in both directions if an operator gains or loses a probe
- [x] It found a live defect immediately — `f($a ** $b, 1)` did not parse, exponentiation missing from the comma scan. Fixed, with the corpus as its regression test
- [x] **Coverage measured properly, and "3 of 7" was measuring the wrong thing** — see below. `HasTopLevelOperatorBeforeCloseParen` is the most load-bearing site in the parser, not an uncovered one
- [x] Determine which of the four are *redundant* rather than uncovered — two show no observable effect when their answer is forced, one is heavily exercised in both directions, and the fourth shares a method with the second
- [ ] Adding a new operator requires editing one place, demonstrated by adding one — **not attempted**; the corpus makes an omission *visible*, it does not make it *impossible*
- [ ] `TS-P2-116`'s shape (unary at statement start) covered — currently blocked behind `TS-P2-117`, which the corpus found and which exempts `not`
- [x] No accepted syntax changes; the full suite passes at 5,604

## Re-measured 2026-08-27, and the earlier number was measuring the wrong thing

Deleting `IsCastOperatorToken` from a site asks "is *this operator's* contribution to this
scan tested?". It does not ask whether the **site** is tested — a scan handling nine operator
families keeps working when one is removed from it. So "3 of 7" understated the coverage and,
worse, pointed at the wrong sites.

Forcing each site to a constant asks the stronger question. Full suite, 6,651 tests:

| Site | forced `false` | forced `true` |
|---|---:|---:|
| `HasTopLevelOperatorBeforeCloseParen` | **116 failures** | **415 failures** |
| `HasTopLevelOperatorBeforeComprehensionKeywordOrClose` | 10 failures | **0** |
| `IsAnyOperatorToken` | 10 failures | **0** |

**`HasTopLevelOperatorBeforeCloseParen` is the most load-bearing scan in the parser**, not a
redundant one. The earlier table recorded it as "not caught", which was true of the cast
operator and false of the site.

### The two that answer `true` for free

Both remaining sites break when forced `false` and change nothing when forced `true`, so only
their positive answers are constrained. Their negative branches were then probed by hand
rather than left at "the suite does not cover it":

- **Comprehension `where` / `let`** chooses `ParseOperatorExpression` over `ParseArgument`.
  Thirteen shapes — bare variable, literal, string, parenthesised, negation, array literal,
  record literal, string with spaces, nested comprehension, and the operator cases — parse
  **identically** either way. `ParseOperatorExpression` appears to subsume `ParseArgument` at
  that position.
- **`IsAnyOperatorToken`**, whose only caller is `NextTokenStartsCommandArgument`'s bareword
  case. Six shapes, including `Math.PI + 1` — the example its own doc comment cites — parse
  identically.

So the honest reading is **unexercised, and probably redundant**, on negative evidence: no
constructed shape distinguishes them, and neither does the suite. That is worth acting on —
a branch nothing depends on can drift forever — but removing one is a behaviour change
resting on the absence of a counterexample, which is the user's call rather than a cleanup.

### An accidental guard worth knowing about

Forcing a scan site wrong failed the **build**, not just the tests: `Tosh.Cli.csproj` runs the
freshly built binary over `scripts/sync_vscode_extension.tosh` after compiling, so a parser
that cannot parse a real script stops the build. It cost a confusing "BUILD FAILED" here, and
it is a real check on the parser that no test file contains.

## What the guard actually covers, measured

Deleting `IsCastOperatorToken` from each of the seven scan sites in turn:

| Site | Method | Caught |
|---|---|---|
| 611 | `HasTopLevelOperatorBefore` | yes |
| 667, 684 | `HasTopLevelOperatorBeforeComprehensionKeywordOrClose` | no |
| 1342 | `HasTopLevelOperatorBeforeCommaOrCloseParen` | yes |
| 1446 | `HasTopLevelOperatorBeforeStageBoundary` | yes |
| 1524 | `HasTopLevelOperatorBeforeCloseParen` | no |
| 1543 | `IsAnyOperatorToken` | no |

**3 of 7.** A single bare probe caught none of six, which is why the position matrix
exists at all. The four misses are the open question: site 1524 governs the close-paren
position and the corpus *does* probe `($a as int)`, yet deleting it changes nothing — so
that site is either redundant or the decision is reached another way first. Redundant
scan sites would be worth knowing about on their own, since a site nothing depends on is
a site that can drift unnoticed forever.

## Two errors made while building it

Recorded because both are about the *oracle*, and a guard with a bad oracle is worse
than no guard.

The corpus first reported 24 failures. **Twenty-three were the probe's fault**: the
call-argument position used `func add(x, y) => ($x + $y)`, which fails at *runtime* when
handed a boolean, and "any exception" could not tell a parse failure from a runtime one.
The probe function is now deliberately type-agnostic.

Separately, `($a as int)` briefly appeared to be broken in the shell — the source had
been restored after a control experiment without rebuilding, so the binary still had the
predicate removed. The same "measured the fix against itself" trap this programme has
hit before.

## Notes

**Not a rewrite.** `TS-P2-11` already established that rewriting this parser for
style carries real regression risk across 13,000 lines and buys nothing user-visible.
The value here is a guard that makes disagreement impossible, which is a much smaller
change than restructuring the dispatch.

Sequence this against the file split in `TOAST_SEPARATION_PLAN.md` A2 — the parser is
one of the two monoliths being divided into partial classes. A guard is easier to add
once the scan sites are grouped by concern, and the split itself will make any
remaining disagreement more visible. Doing this first would mean touching the same
code twice.

The parity-tripwire pattern already used by `EditorSurfaceParityTests`,
`LanguageSurfaceParityTests` and `SyncAsyncTwinInventoryTests` is the model. Those
encode "these two surfaces must list the same things" and have caught real drift.
