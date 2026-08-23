---
id: TOAST-0070
title: "Whether a rune is called is decided by a textual scan, so a name in a string disables compilation"
status: complete
area: toast
priority: 3
opened: 2026-08-22
closed: 2026-08-22
---

## Problem

`ProgramNeedsWholeScriptReplay` decides whether a rune has a call site by scanning the
**source text** for the rune's name as a whole word outside its own definition span. Its own
comment says the approximation is deliberate and conservative.

It is conservative in one direction only, and the cost is total:

```tosh
rune retry(count, body) {
    $body
}
writeline "the word retry appears only in this string"
```

There is no call. Compiled with `--profile runtime` this reports
`tier 3 feature: whole-script replay (rune expansion)` — the whole program falls back to
being carried as source and re-evaluated, because a rune's name appears inside a string
literal.

A comment, a variable named after the rune, a different module's identifier, or a filename in
a path would each do the same.

## Why it is worth fixing separately

`TOAST-0069` removes the scan entirely by expanding runes at compile time, and is the real
answer. This is filed apart from it because the scan is wrong *today* and is much smaller to
repair: the bound tree already knows what is called, so the question "does this program
contain a rune call site" can be answered from `BoundCommandCall` names rather than from a
regex over source.

Fixing the detection alone would let a program that merely *mentions* a rune compile, which
is a real if narrow improvement, and it removes a false positive that is impossible to
diagnose from the outside — nothing in the message says "because the word `retry` appears in
a string on line 4".

## Resolution — 2026-08-22

**A token scan rather than a character scan.** A bareword is what a call site is written
with; a string literal is a *single token whose text is its whole content*, and a comment is
not a token at all. So `retry` inside `"the word retry appears only here"` can no longer
match, because the token it belongs to is the entire string.

The approximation itself is unchanged and still deliberate: this answers "does the program
contain something that looks like a call", not "does it call". The honest answer is a
bound-tree walk, and it arrives with `TOAST-0069`, at which point this predicate disappears
along with the replay it guards.

`ToshLexer` is public and takes the source, so re-lexing costs one pass over text that has
already parsed once. A lexing failure keeps the conservative answer rather than compiling on
a guess.

### The negative control that passed first

Removing the token-*kind* filter broke nothing, which was surprising until it wasn't: a
string literal never matched on kind, it failed on **equality** — its token text is the whole
literal, not the word inside it. The filter is defensive, not load-bearing.

Mutating the comparison to `Contains`, which is what the character scan effectively did,
fails the string case immediately. That is the control that means something.

## Acceptance

- [x] A rune name appearing in a string, comment, or unrelated identifier does not force
      whole-script replay — all three asserted, plus a name not mentioned at all
- [x] A genuine call site still does — asserted as a tripwire, to be *moved* rather than
      deleted when `TOAST-0069` makes it compile for the right reason
- [x] An indirect call — through a variable holding the rune — is handled, or recorded as a
      deliberate over-approximation with the reason stated — recorded: a bareword bearing the
      rune's name is treated as a call wherever it appears outside the definition
- [x] A negative control — `Contains` in place of equality fails the string case

## Notes

Found while answering whether runes compile. The scan is a reasonable thing to have written
before there was a reason to be exact; `TOAST-0069` is the reason.
