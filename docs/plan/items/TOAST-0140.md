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

**One trap, not fixed.** Every quantity answers to a short and a long name for the *same* type
— `length` and `lengthquantity` are both `LengthQuantity`. `duration` is the exception:
`duration` is `TemporalAmount` and `durationquantity` is `DurationQuantity`, two different
types one letter-group apart. `timequantity` is a third name for the second. Nothing warns.

**A duplicate that the table accepts in silence.** `nint` and `nuint` were each defined twice
after `TOAST-0061` added them beside `Half` and `Int128` without noticing they already sat
beside `intptr`. A collection initialiser using the indexer overwrites rather than throwing, so
two identical entries are invisible. Removed; nothing detects the next one.

## What would clean it up

- [ ] One definition. The spec table is generated from `DotNetTypeResolver.Aliases`, the way
      `diagnostic-codes.tex` is generated from the source, so drift becomes impossible rather
      than merely noticed
- [ ] The shell static types (`vec`, `mat`, `complex`, `Math`) are either in the same table or
      documented as a separate kind, rather than mixed into one table in the prose and two
      registries in the code
- [ ] A test refusing duplicate keys in the alias table
- [ ] `duration` versus `durationquantity` is resolved — renamed, aliased to one type, or
      documented as the deliberate exception it currently is not
- [ ] A name that resolves *only* through the platform-index fallback is either blessed or
      documented as unblessed; `datetimeoffset` and `version` are documented as aliases and
      are not aliases

## Notes

Not included: whether `dynamic`/`any` mapping to `object` is right. The CLR resolver maps
both to `System.Object` while the checker treats `dynamic` as a sentinel that short-circuits
assignability, so the same word means two things one layer apart. That is a semantics question
rather than a table question and wants its own item.
