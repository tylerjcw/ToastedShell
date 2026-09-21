---
id: TOAST-0139
title: "A wide integer literal nothing asked for is silent"
status: complete
area: toast
priority: 2
opened: 2026-09-20
closed: 2026-09-20
---

## Problem

[`TOAST-0138`](TOAST-0138.md) made a decimal literal wider than 64 bits lex as a `BigInteger`,
so `Int128`, `UInt128` and `bigint` could finally be written down. It left a hole, named in
that item and objected to as soon as it was read: a typed digit too many now produces a
`BigInteger` rather than a diagnostic.

`NumericLiteralWidthTests` had recorded the original objection as two parts — the removed
behaviour "is silent, and it loses digits". Widening to `BigInteger` answers the second and
not the first.

## The split

The lexer has no context. It sees digits and must decide a type, and the only honest answer
for digits that exceed every fixed width is an exact arbitrary-width integer — anything else
either loses the value or refuses to express it.

The checker *does* have context. It can see whether anything asked for that width. So the
lexer's job is to represent the value and the checker's job is to notice nobody wanted it,
which is one question split along the line where the information actually changes.

| Written | Diagnostic |
|---|---|
| `var y: Int128 = 170141183460469231731687303715884105727` | none — the annotation vouches |
| `var u: UInt128 = 340282366920938463463374607431768211455` | none |
| `var b: bigint = 99999999999999999999999` | none |
| `var x = 99999999999999999999999` | `tosh.type.wide_integer_literal` |
| `echo 99999999999999999999999` | `tosh.type.wide_integer_literal` |
| `99999999999999999999999` | `tosh.type.wide_integer_literal` |
| `var x: int = 99999999999999999999999` | the existing mismatch, which already says the useful thing |
| `var x = 5`, `var x = 9223372036854775807` | none |

The diagnostic says the literal becomes a `bigint` and offers the annotation that would make
it deliberate, so the fix for a real wide value is to say so and the fix for a typo is to
count the digits.

## Two things that made this harder than it looks

**An inferred type cannot vouch.** Without a written annotation, `decl.Symbol.DeclaredType` is
whatever the inferrer read off the literal — `BigInteger` for a wide one. Vouching on that
meant the literal vouched for itself and the warning could never fire, which is exactly what
the first attempt did. `BoundSymbol.DeclaredTypeName` is null unless an annotation was
written, and that is the discriminator.

**The declaration shape does not reach the expression walk.** Nothing walks a command call's
arguments, and `WalkExpression` is where the literal case lives, so `var x = <wide>` was
silent while `(<wide>)` and `echo <wide>` warned. Found by printing the bound shape rather
than by reasoning about the grammar: it *is* a `BoundExpressionStage`, and the cause was the
self-vouching above. The declaration-site check stayed anyway, because it is where a typo is
most likely and it costs one lookup.

## Acceptance

- [x] A wide literal with an annotation that holds it is not questioned
- [x] A wide literal with nothing asking for that width is
- [x] An inferred type does not count as asking
- [x] A narrow annotation keeps reporting the mismatch rather than gaining a second complaint
- [x] Ordinary literals are untouched, including `long.MaxValue`
- [x] Tests per row of the table
