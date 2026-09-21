---
id: TOAST-0140
title: "The type-alias table is documented in one place and defined in two, and the two have drifted"
status: open
area: toast
priority: 2
opened: 2026-09-20
---

## Why this exists

An audit of the base types, asked for after `Vector3` and friends were blessed. The aliases
mostly work; what is wrong is that nothing keeps the definition and the documentation in step,
and the drift is now large enough to mislead.

## Measured — 2026-09-20

**126 alias names in code, 84 distinct target types.** `§Built-in Type Aliases` names 85 and
says "over 40". Comparing the two sets:

- **49 aliases exist and are undocumented.** The whole long-form quantity family
  (`lengthquantity` beside `length`, twenty-six of them), `bigint`/`biginteger`, `any`,
  `dynamic`, `cstr`/`cstring`, `intptr`/`uintptr`/`uptr`, `storagesize`, `temporalamount`, the
  collection shapes `queue`/`stack`/`linkedlist`/`sortedset`/`sorteddict`/`sortedmap`/`map`/
  `table`/`dynamicrecord`, and — least expected — `error`, `failure`, `diagnostic` and
  `nativeerror`, which `§Errors and catch` treats as language vocabulary without either
  section mentioning that they are also aliases.
- **9 are documented and not in the table.** `datetimeoffset`, `version`, `dir`/`directory`
  resolve only through the platform-index fallback that finds a CLR type by simple name — the
  same mechanism that made `func` resolve to `System.Func`1`. `vec`/`vector`/`mat`/`matrix`
  are not aliases at all; they are shell static types registered in
  `BuiltInShellTypes.RegisterDefaults`. `t[]` is array syntax that got into a table of names.

**Three flat errors in the table**, fixed in the same change as this item:

| Row | Was | Is |
|---|---|---|
| `ptr` | listed twice, the second labelled "(alias)" | `ptr`/`nint`/`intptr` and the `uptr`/`nuint`/`uintptr` pair |
| `tuple` | `System.Tuple` types | `Tosh.Runtime.ToshTuple` |
| count | "over 40" | over 120 |

**One near-trap, and a correction to this item.** Every quantity answers to a short and a long
name for the *same* type — `length` and `lengthquantity` are both `LengthQuantity`. `duration`
is the exception: `duration` is `TemporalAmount` and `durationquantity` is `DurationQuantity`,
two different types one letter-group apart, with `timequantity` a third name for the second.

This item first said "nothing warns". That was under-reading: `§Quantities` states it outright
— "the existing `duration` alias continues to mean calendar-aware `TemporalAmount`; fixed
physical time uses" the quantity. So it is deliberate and documented, just not where a reader
comparing the two names would look. The remaining work is placement, not a decision.

**A duplicate that the table accepts in silence.** `nint` and `nuint` were each defined twice
after `TOAST-0061` added them beside `Half` and `Int128` without noticing they already sat
beside `intptr`. A collection initialiser using the indexer overwrites rather than throwing, so
two identical entries are invisible. Removed; nothing detects the next one.

## What would clean it up

- [x] Drift becomes impossible rather than merely noticed — **by a test rather than by
      generation**, so the table keeps its hand-written notes. `Every_alias_is_documented`
      compares the resolver's own keys against the section, treating a quantity's long form as
      covered by its short one; generating the table would have replaced "32-bit IEEE float"
      and "Calendar-aware duration" with nothing
- [x] A test refusing duplicate keys in the alias table — it reads the *source*, because a
      collection initialiser overwrites and the duplicate is invisible at runtime
- [x] A name that resolves *only* through the platform-index fallback is blessed:
      `datetimeoffset`, `version`, `dir` and `directory` are aliases now
- [x] The nineteen genuinely undocumented aliases have rows, including the four error kinds
- [ ] The shell static types (`vec`, `mat`, `complex`, `Math`) are either in the same table or
      documented as a separate kind, rather than mixed into one table in the prose and two
      registries in the code
- [ ] `duration` versus `durationquantity` is stated where the names are, not only in
      `§Quantities`

## Notes

Not included: whether `dynamic`/`any` mapping to `object` is right. The CLR resolver maps
both to `System.Object` while the checker treats `dynamic` as a sentinel that short-circuits
assignability, so the same word means two things one layer apart. That is a semantics question
rather than a table question and wants its own item.

## Done — 2026-09-20

`dir` was documented as `DirectoryInfo` and was not an alias at all, so it did not resolve;
`directory` reached the platform-index fallback and came back as the **static**
`System.IO.Directory`, a type no annotation can ever hold a value of. `file` had been blessed
and its counterpart had not. Both are aliases now, as are `version` and `datetimeoffset`.

Both guards were verified by breaking them: a reintroduced duplicate fails
`No_alias_name_is_defined_twice`, an added undocumented alias fails
`Every_alias_is_documented`, and both pass again when restored. A guard that has only ever
passed is not yet a guard.

Generation was considered for the table and rejected. `diagnostic-codes.tex` is generated
because its content is mechanical; this table's third column is hand-written and worth keeping
— "32-bit IEEE float", "Calendar-aware duration", "Text at a native boundary". A completeness
test gives the same guarantee and keeps the prose.
