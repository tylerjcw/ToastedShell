---
id: TOAST-0039
title: "A function and a method returning the same collection have different pipeline shapes"
status: complete
area: toast
priority: 2
opened: 2026-08-21
closed: 2026-08-21
---

## Problem

`TOAST-0028` made the producer decide collection shape. The rule it implements is
*syntactic* — an **expression** head is a sequence, a **command** stage is not — and two
spellings of "call something that returns a collection" fall on opposite sides of it:

```tosh
func fn() { return [1, 2, 3] }
class C { func m() { return [1, 2, 3] } }
var c = new C()

echo (fn | count)        # 1  -- parsed as a command
echo (fn() | count)      # 1  -- parens do not change that
echo ($c.m() | count)    # 3  -- a `$`-prefixed member call is an expression head
```

Both are a call returning a collection. Nothing about the author's intent differs. The
answers differ because one parses as a command invocation and the other as an expression.

## Measured 2026-08-21

The four head forms, on committed code:

| Head | Result |
|---|---|
| `fn` — bare function as a command | 1 |
| `fn()` — with parentheses | 1 |
| `$c.m()` — method on a variable | **3** |
| `$v` — variable holding the collection | 3 |

## Why this was not caught

`TOAST-0028` verified ten example scripts and the user's profile, and the change looked
clean. It was not: `scripts/plan.tosh` broke, because `load() | where { $_["board"] … }`
began handing `where` a single `Object[]` instead of the items. The sweep had covered
`examples/` and not `scripts/`, and `--help` on each script does not reach the code that
runs.

That is the second-order finding here and worth keeping: a shape change of this kind is not
verified by running programs that happen to be in front of you.

## The decision this needs

1. **Accept it, and specify it.** The rule is "an expression head is a sequence"; a bare
   command is not an expression. Defensible and already implemented — but it means the two
   spellings above differ forever, and nothing at the call site explains why.
2. **A call is a call.** Make `fn()` — with parentheses — an expression head like
   `$c.m()`, leaving bare `fn` a command. Parentheses become the thing that says "this is a
   value", which is a rule that can be taught. `fn` and `fn()` then differ, which is new.
3. **A collection from any call is a value.** Make `$c.m()` yield 1 too, so every call
   agrees, and `...` spreads when spreading is meant. The most consistent, and the most
   breaking: `$snapshot.code_files() | count` currently answers 1457 in
   `examples/common_tasks/06_workspace_brief.tosh` and would answer 1.

Option 3 is the only one where "what shape is this?" has an answer that does not require
knowing how the head parsed.

## Acceptance

- [x] The four head forms give answers that can be stated in one sentence
- [x] `docs/spec/` §Collection Shape says which, with the examples above
- [x] Every `.tosh` in the repository is *run*, not `--help`ed, before and after
- [x] A negative control

## Notes

Filed against `TOAST-0028`'s own work rather than discovered later, and the trigger was
`scripts/plan.tosh index` failing immediately after the five Phase B items were written —
the board tooling was the first real program to hit it.

## Resolution — 2026-08-21

**A collection written as an expression is a sequence; a collection returned by a call is a
value.** One sentence, no exceptions that depend on how the head parsed.

`new` is deliberately on the *written* side. Treating construction as a call was tried
first, and it made `new array(1, 2, 3) | count` answer 1 while the identical
`[1, 2, 3] | count` answered 3 — this item's own defect, reintroduced one spelling over. A
property read is on the written side for the same reason: `$c.Items` *is* the collection in
the way a variable is one, and it is the calling that produces a new value.

### Cost

Six tests, all probing type-argument resolution rather than shape, where `| count` was
counting the results of a static call: `Enumerable.Range(1, 3) | count` and its kin. They
say `echo ...(Enumerable.Range(1, 3)) | count` now, which says what they meant.

One example needed migrating — `examples/common_tasks/06_workspace_brief.tosh` called
`$this.code_files()` straight into a pipeline twice. Both bind to a variable first.

### The verification, and what it nearly missed

Every runnable `.tosh` was run before and after, not `--help`ed. That found the example —
**and the error count did not.** `06` reported no diagnostic at all: it printed
`Code-like files: 1` where it had printed 1457, and carried on. A sweep that counts errors
would have called that a pass.

The output diff had its own flaw worth recording: rebuilding between the two captures
changes the filesystem these scripts measure, so file counts and timestamps differ for
reasons that have nothing to do with the change. What discriminated was checking the
shape-sensitive values directly — a count that collapses to 1 is the signature of this
defect, and nothing else in the output looks like it.

## Notes

Filed against `TOAST-0028`'s own work and fixed the same day. The rule it shipped was
syntactic; this made it semantic.
