---
id: TOAST-0115
title: "Unary minus glued to a variable is read as a command name in assignment position"
status: proposed
area: toast
priority: 3
opened: 2026-09-10
---

## Problem

`-$x` lexes as a single bareword wherever the lexer is not in expression context, and an
assignment's right-hand side is not. So the negation of a variable cannot be written the
obvious way:

```tosh
var x = 3
var u = -$x     # Command '-$x' was not found
$y = -$x        # Command '-$x' was not found
```

while every neighbouring spelling works:

```tosh
var u = - $x    # -3, spaced
var u = (-$x)   # -3, parenthesised
echo (-$x)      # -3
```

The value is never wrong and nothing is silently mis-parsed — the failure is a
`tosh.runtime.unknown_command` naming `-$x`, which reads as though a program by that name
was expected. It affects **numbers as much as classes**: this is not about operator
overloading, though it was found while writing `TOAST-0056`'s examples, which are
parenthesised for this reason.

## Why it is not already fixed

`TS-P2-02` fixed exactly this, and deliberately narrowly. The bareword scanner breaks
before `$` when the text so far is a lone sign character — but only
`InExpressionContext`, because in *command-argument* position `-$x` is a flag and must
stay one:

```tosh
echo -$x        # a flag named -$x, correctly
```

`_expressionDepth` is raised by `(`, `[` and collection literals, and by nothing else. An
assignment right-hand side is a value position but not a bracketed one, so the guard that
makes the fix safe is also what stops it applying where it is most wanted.

## Shape of a fix

The distinguishing fact is available: the previously emitted token. After an assignment
operator — `=`, `+=`, `-=`, `*=`, `/=`, `//=`, `%=`, `**=`, `??=` — what follows is a
value, never a flag. Widening the existing break to fire there as well keeps
command-argument position untouched, because `--opt=value` is one bareword rather than a
standalone `=` token.

## Acceptance

- [ ] `var u = -$x` and `$y = -$x` negate, for numbers and for classes overloading `-`
- [ ] `echo -$x` still passes a flag
- [ ] `--opt=value`, `a$b`, paths and `--name` are unchanged
- [ ] `+$x` behaves as `-$x` does
- [ ] The spec's unary examples no longer need parentheses to be correct
