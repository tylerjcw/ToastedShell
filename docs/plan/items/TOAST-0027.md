---
id: TOAST-0027
title: "An unrecognised escape in a double-quoted string is kept as text instead of being reported"
status: open
area: toast
priority: 2
opened: 2026-08-20
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

- [ ] `"\u00E9"` either resolves or is reported, and the specification says which
- [ ] `$'\u00E9'` is unchanged, pinned as a control
- [ ] A literal backslash in ordinary text still works, whatever is chosen
- [ ] `docs/spec/` states which escapes each string kind takes, in one place
- [ ] A negative control

## Notes

Found writing `TOAST-0018`'s Unicode section — and it had already been written into the
specification as fact before the corpus caught it. Every probe had gone through
`tosh -c`, and the **command line performs its own shell-level escape processing**, so
`tosh -c 'echo ("\u00E9".Length)'` answers `1` while the identical line in a script
answers `6`.

That is the sharper half of this item: `-c` and a script file disagree about what a string
literal means, which makes the command line an unreliable instrument for measuring the
language. Worth a check of its own — it is how a false claim reached the specification
despite the measure-first discipline.
