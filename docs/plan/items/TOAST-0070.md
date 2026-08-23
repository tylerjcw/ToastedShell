---
id: TOAST-0070
title: "Whether a rune is called is decided by a textual scan, so a name in a string disables compilation"
status: proposed
area: toast
priority: 3
opened: 2026-08-22
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

## Acceptance

- [ ] A rune name appearing in a string, comment, or unrelated identifier does not force
      whole-script replay
- [ ] A genuine call site still does
- [ ] An indirect call — through a variable holding the rune — is handled, or recorded as a
      deliberate over-approximation with the reason stated
- [ ] A negative control

## Notes

Found while answering whether runes compile. The scan is a reasonable thing to have written
before there was a reason to be exact; `TOAST-0069` is the reason.
