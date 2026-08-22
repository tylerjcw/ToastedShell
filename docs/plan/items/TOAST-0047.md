---
id: TOAST-0047
title: "A bottom type, so an expression that never returns stops poisoning inference"
status: proposed
area: toast
priority: 3
opened: 2026-08-21
---

## Idea

Tōast has no bottom type. `BoundTypeKind` is exactly `{ Dynamic, Concrete, Void }`, and
`never`, `bottom`, `none`, `unit`, `any` and `unknown` all resolve to `dynamic`.

The gap is measurable:

```tosh
var v = (true ? 1 : throw new Error("x"))   # "could not pin down a concrete type"
```

`BoundThrowExpression` is typed `BoundType.Dynamic`, so joining it with `int` gives
`dynamic`. With a bottom type, `int ⊔ never = int` and this infers.

That is not hypothetical: `throw` in a ternary arm and in a `match` arm both compile and
run today, and `default => throw …` is the ordinary way to say an arm cannot happen —
`TOAST-0044` taught the emitter to handle exactly that.

## Reference

The same argument for C#: [dotnet/csharplang#8604](https://github.com/dotnet/csharplang/issues/8604),
which proposes `never` as a subtype of every type, to give `throw` expressions a return
type and let `return`, `break` and `continue` be expressions.

## Three concepts that get conflated

Worth separating, because the names overlap and Tōast has taken some of them:

| | Meaning | Tōast today |
|---|---|---|
| **Bottom** (`never`) | a type with **no** values; types an expression that does not return | missing |
| **Unit** (`void`, `nothing`) | a type with **exactly one** value; "nothing meaningful came back" | exists, unspecified and inconsistent — `TOAST-0046` |
| **Option** (`maybe`, `none`) | a value that **may be absent** | `null`, with `?.` and `??`, fully specified in §What null Means |

And a fourth axis that is not types at all: **quantifiers**. `any`, `all` and `none` already
exist in Tōast as pipeline commands (`any { … }`), so those words are taken in a different
sense.

## Recommendation

**Add `never`, narrowly.** A type the inferrer knows, produced by `throw` — and later by
`return`, `break` and `continue` if those become expressions — and absorbed by every join.
Not a value anyone writes, not a variable anyone declares.

**Do not add `nothing`/`none` as values.** `null` already means "absent" and is specified in
detail; a second spelling is the kind of drift several items this month exist to remove. And
`none` is already a command.

**Do not overload the quantifier words.** `any` and `all` are commands; giving them
type-level meanings would make both harder to read.

## Sequencing

After `TOAST-0046`, which settles what `void` means. A bottom type introduced beside an
unspecified and self-inconsistent unit type would make both harder to explain.

## Acceptance

- [ ] `never` exists as a `BoundType` and is the type of a `throw` expression
- [ ] Joining `never` with any type yields that type — `(cond ? 1 : throw …)` infers `int`
- [ ] `never` cannot be declared as a variable's or parameter's type
- [ ] `docs/spec/` states what it is and how it joins
- [ ] The interpreted and compiled backends agree
- [ ] A negative control
