---
id: TOAST-0072
title: "A rune's block argument ran in the current scope, so a macro calling a macro never worked"
status: complete
area: toast
priority: 2
opened: 2026-08-23
---

## Problem

`EvaluateRuneThunkAsync` has two paths. The expression path evaluates the argument in the
scopes captured at the call site; the **block path ignored `CallerScopes` entirely** and ran
the block in whatever scope happened to be current. Inside an expansion that is the rune's
*own parameter scope*, so a block forwarded through a second rune resolved its free variables
against the wrong rune.

```tosh
rune t(b) { $b }
rune f(b) { t { t $b } }
f { echo "z" }
```

The block `{ t $b }` is written in `f`, so its `$b` means `f`'s parameter. It resolved to
`t`'s instead, which is the thunk for `{ t $b }` — so `t` re-entered itself forever.

Measured 2026-08-23 across three builds:

| Build | Result |
|---|---|
| `e9d968e`, before rune expansion landed | prints nothing, exit 0 |
| after `TOAST-0069` | `tosh.runtime.recursion_limit_exceeded` |
| after this fix | `z` |

So a macro that calls another macro with a forwarded block has **never** worked. The
`TOAST-0069` recursion guard did not cause it; it made it audible, turning silence into a
diagnostic that named the repeating frame.

## Cause

The two thunk paths disagreed about a rule that applies to both. `TOAST-0069` had already
corrected the expression path — `CallerScopes` being `null` for a sealed thunk was read as
"leaky" — and the block path was simply never brought along, because nothing exercised it:
every rune test passed its block straight to the rune that declared the parameter, one hop,
where the current scope and the caller's scope are the same.

## Resolution — 2026-08-23

The block executes under `UseScopes(thunk.CallerScopes)` when the thunk is sealed, the same
rule and the same helper the expression path uses. The leaky decision is taken before the
swap, since `IsInsideLeakyRune` reads the live scope stack.

A block must still *reach* the caller's variables — `do-twice { $n = $n + 1 }` has to
accumulate — and it does, because the caller's scope is exactly where `n` lives. A fix that
isolated the block would have satisfied the forwarding tests and broken every accumulating
macro, so that is the control.

## Acceptance

- [x] A block forwarded one hop still works
- [x] A block forwarded two hops resolves against the rune it was written in
- [x] Two nesting macros compose — `fr { … }` over `tw` yields four values, not zero
- [x] A block argument still mutates the caller's variables
- [x] A negative control: removing the scope swap fails both multi-hop rows while the
      one-hop and mutation cases keep passing
