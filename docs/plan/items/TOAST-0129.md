---
id: TOAST-0129
title: "Eighteen of the 609 built-in command examples were not valid ToastScript, and one of them was the command's fault"
status: complete
area: toast
priority: 2
opened: 2026-09-12
---

## Problem

**The most-read code in the project was unchecked.** The 609 `[CommandExample]`
entries across 257 built-in commands are what `help <command>` prints, what the
command reference in the specification is generated from, what the VS Code
extension shows on hover, and what the MCP metadata serves. Nothing parsed them,
and eighteen did not parse.

Fifteen wrote a list literal with spaces:

```tosh
[1 2 3] | permutations          # tosh.parser.missing_list_separator
echo a b c | chain [d e f]
unfold [0 1] func(s) => [($s[0]) [($s[1]) ($s[0] + $s[1])]]
```

The separator is a comma, as the specification says throughout. One example used
`{ |v| … }` block parameters, which is not ToastScript syntax, and one called a
composed function as `$f $input` rather than `$f($input)`.

## The eighteenth was not the example's fault

`unfold`'s own argument documentation reads "returns `[value, next-state]` **or
null to stop**", and the documented spelling could not stop it:

```tosh
unfold 1 func(n) => (($n <= 5) ? [$n, ($n + 1)] : null)
# tosh.runtime.unfold_requires_single_result — "this operation produced no values"
```

An arrow body, or a block whose value *is* `null`, produces no pipeline value at
all — only an explicit `return null` produces one. So the single-result check saw
zero results and raised where the loop should have ended, and `unfold` could be
stopped only by a spelling its own documentation does not use.

The fix is the canonical value-context collapse (`TS-P1-20`: none to `null`, one
to the item, several a diagnostic), applied to the one command whose contract
gives `null` a meaning. It stays off for the other thirteen callers of
`RequireSingleResultAsync` — for `map`, `sort` and `get` a lambda that produces
nothing is a mistake worth naming rather than a null to carry forward.

## Acceptance

- [x] All 609 examples parse, and `CommandExampleParseTests` keeps it that way,
      with a negative control asserting the corpus was actually found
- [x] Every corrected example was **run**, not merely parsed: `permutations` gives
      6, `zip` with a combiner gives 11, `unfold` gives exactly 1 through 5 and
      stops
- [x] `unfold` stops on `null` however it is spelled — arrow body, block, ternary,
      explicit `return` — and does **not** stop on an ordinary value
- [x] A `map` lambda that produces nothing is still the error it was
- [x] Full suite green — 7,576 passing, with only the four pre-existing
      `CompilerRuntimeStagingTests` failures from uncommitted compiler work

## Not done here

`docs/spec/command-reference.tex` is generated from this metadata and picks the
corrections up on the next regeneration. It is left alone in this commit because
the working tree's copy also carries unrelated pending TUI work.
