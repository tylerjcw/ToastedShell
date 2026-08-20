---
id: TOAST-0003
title: "Documentation disagrees with the implementation in twelve recorded places"
status: complete
area: toast
priority: 2
opened: 2026-08-16
closed: 2026-08-19
---

## Problem

Twelve mismatches between what the documentation says and what the code does,
collected on the old stabilization board under "Documentation Drift to Resolve" and
carried unfiled ever since. They were listed as things to "repair alongside their
owning work item", which meant several had no owner at all.

They are gathered into one item because they are one kind of work — reconcile a
statement with the behaviour — and because a checklist is the honest shape for
something that is finished only when the last one is done.

## Acceptance

- [x] The specification says a failed `as` returns `null`; runtime help and the implementation throw — **fixed 2026-08-17.** Verified first: `"abc" as int` reports *Cannot convert 'String' to 'int'* and `"hello" \| cast int` reports *Could not cast 'hello' to System.Int32*. The table entry, the example and a new paragraph now say it raises, and say why — a silent `null` turns a wrong assumption into a wrong value carried onwards, reported somewhere else entirely
- [x] Storage-size suffixes are documented as literals but behave as strings when both operands are suffix forms — **stale, verified 2026-08-19.** They are a real `StorageSize` type: `1kb + 1kb` is `2 kB`, `(10kb).Bytes` is `10,000`, `(1kib).Bytes` is `1,024`. Every claim in the specification's notebox was run: SI versus binary factors, case-insensitive matching (`10KB`), `echo 10kb` staying a `String` in raw-argument position, and an overflowing suffix raising rather than falling back to a string. The prose is accurate and needed no change
- [x] Storage-size suffixes are worse than that in value position (`TS-P2-14`) — **stale, verified 2026-08-19.** `2mb > 1mb`, `1gb > 1mb` and `10kb > 5mb` all answer correctly, and `ls | where Size > 1b` filters (8 of 19 entries here) rather than silently matching everything
- [x] The operator-precedence table disagrees with the implementation four ways — **three did, one did not, fixed 2026-08-19.** Real: comparison, type testing and membership are **one** left-associative level, not three (`true == 1 is int` is `false`, which only fits `(true == 1) is int`); and `??` binds *tighter* than the ternary, which the table had backwards (`"x" ?? "y" ? "a" : "b"` answers `a`, not `x`). Not real: `**` against unary minus. `-2 ** 2` is `4` because **`-2` lexes as a negative literal**, not because unary minus binds tighter — `-$x ** 2` with `$x` of `2` is `-4`, exactly as the table always said. Range binding did not reproduce either: `1 + 1 .. 4` is `2,3,4` and `1 .. 3 == 3` is `false`, both as documented. The table is now 16 levels rather than 18
- [x] The equality cascade omits the `TypeConversion` coercion and the case-insensitive `ToString` fallback (`TS-P1-14`) — **fixed 2026-08-19, and the first attempt at it was wrong.** The cascade is now given as five ordered steps, each one run first: records and dictionaries by name and value, two non-string sequences element-wise, enum members against their own enum and backing value, conversion in *both* directions (`TS-P1-26`), then `Equals`. There is **no** textual fallback — `TS-P1-14` removed it — so the earlier repair's parenthetical "mixed types compare case-insensitively" was itself drift, generalising a bool-parsing quirk into a language rule. `true == "TRUE"` is true because parsing a bool ignores case; `E.Low == "LOW"` is **false** because converting to an enum member is by name and case-sensitive. Both are now in the specification as the pair that tells the two explanations apart
- [x] The comprehension chapter pipes `$myDict | entries` — **fixed 2026-08-19, and the first attempt at that was wrong too.** The replacement example was written down without being run, and it does not run: `$myDict.Keys` fails on a `{| ... |}` record, which is an `ExpandoObject`. A dictionary is `{% "a" => 1 %}`, and the example now declares one, shows its output, and says which literal is which
- [x] Operator help and MCP metadata misstate case sensitivity and which operators are supported — **fixed 2026-08-19.** Both said string equality is case-insensitive, and the MCP entry's example was `"Hello" == "hello"`, which is `false`. Both now say two strings compare exactly and a differing type is converted first. On "which operators are supported": all 53 entries in the MCP table were executed, and the only two that failed did so because the example names an enum the harness had not declared — with `flags enum InitFlag` present, `InitFlag.Video bor InitFlag.Audio` renders `Video, Audio` and `has` answers correctly, so the table is accurate
- [x] The LaTeX build depends on an absolute personal path for the cover image — **fixed 2026-08-19 by shipping the asset**, since the `\IfFileExists` guard tried earlier cannot work (it does not search `\graphicspath`). `docs/spec/assets/Colby-Crest.png` is now in the repository and `\graphicspath` is relative. The rebuilt PDF is 3,021,140 bytes — the crest is embedded; the build without it was 1.46 MB. **This adds a 1.49 MB binary**, the first under `docs/spec/`, and the original is shipped unmodified rather than downscaled
- [x] CLI help omits the compilation and metadata-export modes — **fixed 2026-08-19.** `--help` gained a *Compilation* section (`--compile`, `-o`, `--no-apphost`, `--publish-single-file`, `--emit-refasm`, `--compile-allow-dynamic`) and a *Metadata export* one (`--export-command-metadata`, `--json`/`--latex`/`--vscode`, `--surface`, `--dump-builtins`). Each was run before being written down. The build itself uses `--export-command-metadata --latex` to generate part of this specification, and nothing in `--help` said so
- [x] Compile output is documented as requiring `-o` though it can be derived — **fixed 2026-08-19.** `tosh --compile greet.tosh` writes `greet.dll` beside the source. The flag row said "Required", and its second claim was misleading too: the extension does not merely "not have to be `.dll`", it is *replaced* with `.dll` — `-o foo.exe` emits `foo.dll` plus an apphost named `foo`
- [x] Startup documentation disagrees with itself about whether `--no-profile` skips autoload — **fixed 2026-08-19.** The CLI help was right and `AGENTS.md` was wrong. Measured with `--profile-startup`, which names every file it loads: `--no-profile` loads `config.tosh` and `autoload/` and skips only `profile.tosh`; `--no-startup` and `--safe` skip all three. The specification's startup section said only that `--no-startup` exists and now describes all three
- [x] A guard exists so the precedence table cannot drift again — **done 2026-08-19**, as `tests/Tosh.Tests/OperatorPrecedenceTableTests.cs`, 22 tests

## The guard, and why it is not generated

The item asked for the table to be **generated from the `TS-P2-10` surface registry**.
It cannot be, and that is worth recording rather than deferring again: the registry
maps an operator to a *category* — `--export-command-metadata --surface` gives
`"!=": "Comparison"` — and carries **no precedence at all**. Generating from it would
have to invent the exact thing the four boxes disputed.

What the guard does instead needs no new data. Each adjacent pair of levels is pinned
by an expression whose **answer differs** depending on how it groups, executed through
the engine; then the LaTeX table is parsed and required to match the same list. The
parser cannot drift from the tests because they run it, and the table cannot drift from
the parser because the last test reads it.

Three of the sixteen pairs took a second attempt, and each failure is the reason the
first eleven boxes existed:

- `1 == 1 and 2` **pins nothing**. `and` yields a bool rather than its operand, and
  `1 == true` is true by conversion, so both groupings answer `True`. It reads like a
  test and asserts nothing. `false == false and false` distinguishes them.
- `null ?? 1 ? "a" : "b"` is `a` under both readings — which is why the earlier attempt
  at this item recorded ternary-versus-`??` as not reproducing, and stopped there. The
  grouping is visible only when the `??` has a non-null left operand.
- The LaTeX row scanner read the `and` level as `[and]`, silently dropping `&&`,
  because `(.*?)&` stops inside `\code{\&\&}`. A guard that quietly sees less than is
  there is worse than none.

**Negative control:** putting the ternary back above `??` in the specification fails the
guard with `level 14 reads [?, :] but is pinned as [??]`.

## Found on the way, filed separately

`TOAST-0024` — a range's *right* operand does not parse the bitwise levels, so
`1 .. 2 bor 4` fails while `1 bor 2 .. 4` works. Not a precedence defect: the
documented order is what the parser implements wherever the expression parses at all.

## Notes

The last box is the one that matters. Four of these are precedence-table entries, and
a table maintained by hand beside a parser will disagree with it again; the surface
registry from `TS-P2-10` exists precisely so it can be generated. Fixing the other
eleven without that one buys a year.

Three boxes are marked shell-side. They can be split into a `TOSH-` item if the
Tōast/TōSh separation makes that natural — the split is the point at which each of
these acquires an obvious owner.

Several name an owning item (`TS-P2-14`, `TS-P2-03`, `TS-P2-02`, `TS-P1-14`,
`TS-P2-10`); check those first, since some may already be resolved and only the prose
left behind.

## Note — 2026-08-17

None of the twelve above are closed by this, but two neighbouring things were:

**The specification gained a `Value Rendering` section**, and the format-clause paragraph
was corrected. It had said "every .NET format string works" and that "a clause a value
cannot honour leaves it rendered plainly rather than failing" — both true when this item was
filed and both falsified by `TOAST-0014`, which made rendering invariant and made an
unhonourable clause an error. Quoting inside a word, what an interpolation hole is, and a
trait's declared member types are documented too. **Every example in the new text was run
against the binary before it was written down**, which is the discipline the twelve above
exist because of.

**`buildtosh spec` recovers from a poisoned build.** A LaTeX run that fails leaves a
truncated `.out` or `.aux`, and the next run dies with "File ended while scanning use of
\BKM@entry" at `\begin{document}` — naming nothing about the edit that caused it. One
genuine error therefore made *every* later build fail until someone cleaned by hand. That is
a documentation-drift cause in its own right: a specification that is painful to rebuild is
a specification people stop rebuilding.

Measured rather than assumed, over several wrong attempts: it happens with and without
`-halt-on-error`, so dropping that flag does not fix it, and deleting the `.out` first is
worse — latexmk then cannot tell a rerun is needed and the *first* build after any edit
fails. What fixes it is recovering: clean and retry once, and report a second failure as
real. All three controls pass — a settled build does not warn, a genuine LaTeX error still
fails, and the build recovers on the run after.

Box 8 above — the absolute path to the cover image — was attempted and **reverted**. A
guard on the bare filename silently failed, because `\IfFileExists` does not search
`\graphicspath`, and the cover lost its crest while still building. It remains open.

## The specification builds clean — 2026-08-17

`docs/spec/toastscript-spec.tex` now produces **zero** overfull boxes, underfull boxes,
LaTeX warnings, package warnings and font warnings. It had ten overfull boxes.

They were all one cause in different clothes: a fixed-width column narrower than a word that
cannot break. `durationquantity` in a 2.4cm column, `System.Collections.Generic.List<object>`
in a 6.0cm one, `not in / is not in / not-in / is-not-in` in an unwrapped `c` column, BNF
productions in an unwrapped `l`, and long SDK task names at 0.32 of the line.

Two things worth keeping from doing it:

**Widening one column narrows another.** The first attempt widened column 1 of the type
table and pushed the overflow straight into column 2 — the count went from ten to five and
the *rows* changed. The table only used 12.6cm of a 15.5cm text block, so the real answer was
that it had been over-constrained all along.

**Column widths must leave room for `\tabcolsep`.** The next attempt sized three columns to
15.0cm against a 15.5cm block and still overflowed by 21.77pt, because six column edges add
about 1.3cm of padding. That is the difference between sizing the content and sizing the
table.

The *generated* fragments were checked too. `CommandLatexEmitter` uses wrapping columns, and
`extract-diagnostic-codes.tosh` already renders each code as a `description` item with
`style=nextline` — chosen, its comment says, to avoid exactly the column overlap long codes
caused in an older longtable. So regeneration cannot reintroduce these.
