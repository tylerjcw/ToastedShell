---
id: TOAST-0027
title: "An unrecognised escape in a double-quoted string is kept as text instead of being reported"
status: complete
area: toast
priority: 2
opened: 2026-08-20
closed: 2026-08-20
---

## Problem

`ReadEscapeSequence` ends with `_ => $"\\{escaped}"`: an escape it does not know becomes
the two characters that were written. So a string containing what looks like a Unicode
escape silently is not one:

```tosh
echo ("\u00E9".Length)      # 6  -- the characters \ u 0 0 E 9
echo ($'\u00E9'.Length)     # 1  -- an ANSI-C string does resolve it
```

Nothing reports the difference. The value is six characters of what the author meant as
one, and it flows onward as ordinary text.

## Why this is worth reporting rather than tolerating

`\n`, `\t`, `\e` and nine others *are* resolved in a double-quoted string, so the author
has every reason to believe `\u` is too. The two escape tables differ —
`ReadEscapeSequence` for `"..."`, `ReadAnsiCEscapeSequence` for `$'...'`, the latter
adding `\xHH` and `\uHHHH` — and nothing in the language says which is which at the point
of writing.

A leading backslash is also common in ordinary text, which is presumably why the fallback
exists: `"C:\path"` should not be an error. That is a real constraint on the fix and the
reason this is filed rather than changed on sight.

## Options

1. **Report an unknown escape**, and require `\\` for a literal backslash. Safest, and
   the most disruptive: every Windows path written with single backslashes becomes an
   error.
2. **Report only the escapes the *other* table knows** — `\u` and `\x` — since those are
   the ones an author demonstrably expects to work. Narrow, and it fixes the case that
   actually bites.
3. **Resolve `\uHHHH` and `\xHH` in double-quoted strings too**, making the two tables
   agree. Removes the trap entirely rather than reporting it, at the cost of changing what
   an existing `"\u..."` string means.

Option 2 or 3; the choice is whether the two string kinds should differ at all.

## Acceptance

- [x] `"\u00E9"` resolves, and the specification says so
- [x] `$'\u00E9'` is unchanged, pinned as a control — and the corpus asserts the two
      kinds agree rather than testing them separately
- [x] A literal backslash in ordinary text still works — an escape naming nothing is
      still kept, so `"\q"` is two characters
- [x] `docs/spec/` states which escapes each string kind takes, in one place
- [x] A negative control — 18 of 29

## Resolution — 2026-08-20

**Both string kinds now take `\xHH` and `\uHHHH`**, from one shared reader. The two tables
had a copy each of the hex logic, which is how they came to differ about whether `\u` was
an escape at all; there is one implementation now.

Reporting the difference was the narrower option and was not taken, because the difference
had no reason to exist. Resolving it removes the trap instead of announcing it.

### The Windows-path argument does not survive measurement

This item was filed saying a literal backslash is "common in ordinary text, which is
presumably why the fallback exists", and treating that as a constraint. Measured:

```tosh
("C:\path\to".Length)      # 9, not 10 — `\t` is a tab, and always was
```

`\t`, `\n` and the rest already resolved in a double-quoted string, so a Windows path
written with single backslashes was already mangled. Adding `\u` and `\x` changes nothing
about that hazard, and the strictest option — reporting *every* unrecognised escape — was
the only one it would have argued against. Pinned in the corpus so the argument is not made
again from memory.

### How the false claim reached the specification — and it was not the tooling

This item was filed blaming `tosh -c` for "shell-level escape processing", on the evidence
that the same line answered `1` on the command line and `6` in a script. **That was wrong,
and measuring it properly is what showed it.** Running identical bytes through both, with
the pre-fix lexer, gives `6` either way:

```
PRE-FIX via -c  : 6
PRE-FIX via file: 6
```

`-c` never processed escapes. What differed was what I had typed: the `-c` probes contained
the *literal character* `é` and the script file contained the *escape* `\u00E9`. Two
different programs, and the instrument took the blame.

The real lesson is smaller and sharper than the one first recorded: a probe that types a
character where it means an escape proves nothing about escapes, and no amount of
measure-first discipline helps if the two runs are not the same program. The corpus caught
it because a corpus repeats the same source verbatim.

## Notes

Found writing `TOAST-0018`'s Unicode section — and it had already been written into the
specification as fact before the corpus caught it. The cause is recorded above, and it is
not the one this item was filed with.

That is the sharper half of this item: `-c` and a script file disagree about what a string
literal means, which makes the command line an unreliable instrument for measuring the
language. Worth a check of its own — it is how a false claim reached the specification
despite the measure-first discipline.
