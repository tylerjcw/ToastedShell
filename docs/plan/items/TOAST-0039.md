---
id: TOAST-0039
title: "A function and a method returning the same collection have different pipeline shapes"
status: open
area: toast
priority: 2
opened: 2026-08-21
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

- [ ] The four head forms give answers that can be stated in one sentence
- [ ] `docs/spec/` §Collection Shape says which, with the examples above
- [ ] Every `.tosh` in the repository is *run*, not `--help`ed, before and after
- [ ] A negative control

## Notes

Filed against `TOAST-0028`'s own work rather than discovered later, and the trigger was
`scripts/plan.tosh index` failing immediately after the five Phase B items were written —
the board tooling was the first real program to hit it.
