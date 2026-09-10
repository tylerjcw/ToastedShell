---
id: TOAST-0115
title: "Unary minus glued to a variable is read as a command name outside brackets"
status: complete
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

## Widened after the first fix

Assignment was not the only unbracketed value position, and it was not the worst one. Two
more turned up the moment the operators of `TOAST-0056` were used in earnest:

```tosh
-$pt            # Command '-$pt' was not found
2 + -$pt        # "2-$pt"   — no error at all
true and -$pt   # true, because a non-empty word is
```

The second kind is worse than what was filed. `-$pt` alone at least fails; `2 + -$pt`
concatenates the literal text and hands back a plausible-looking wrong answer.
`return -$x` and `func f() => -$x` fail the first way, and a class negating its own member
— `return -$this.X` — fails with them.

The fix that covers all of them tests **how the stage opened** rather than what token
precedes the sign. A stage that opened with a bareword is a command and its arguments are
left exactly as they were; a stage that opened with a number, a string, a boolean, a
`$`-prefixed word, or `return`/`throw` cannot be a command, so a leading sign there cannot
be a flag. A stage with nothing in it yet is command-*name* position, where a sign is
impossible for the same reason. Keying on the stage rather than the neighbouring token is
what lets `2 * -$x` negate while `echo a -$x` keeps its arguments, with no list of
operators to curate and keep in step.

`|`, `&&` and `||` are deliberately excluded: each wants a command on its right, so
breaking the sign off would trade `Command '-$x' was not found` for the less helpful
`Command '-' was not found`.

## Acceptance

- [x] `var u = -$x` and `$y = -$x` negate, for numbers and for classes overloading `-`
- [x] `echo -$x` still passes a flag
- [x] `--opt=value`, `a$b`, paths and `--name` are unchanged
- [x] `+$x` behaves as `-$x` does
- [x] The spec's unary examples no longer need parentheses to be correct
- [x] `-$x` alone, after `;`, inside a block, and as an arrow body all negate
- [x] `2 + -$x` is -1 rather than the string "2-$x", and `2 * -$x` is -6
- [x] `return -$x` and `return -$this.X` negate
- [x] A command stage keeps every argument it had: `echo a -$x` is unchanged
- [x] `1 | -$x` keeps the message that names the whole word
