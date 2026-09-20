---
id: TOAST-0101
title: "A pipeline into a user function does not bind to a parameter, and the arity error misdescribes why"
status: complete
area: toast
priority: 3
opened: 2026-08-30
revised: 2026-08-31
---

## Correction

An earlier revision of this item claimed the pipeline was *silently discarded*. That was wrong.
Piped values reach a function perfectly well through `$tosh.Function.Input`:

```tosh
func mine() { return (($tosh.Function.Input ?? []) | count) }
echo ([1, 2, 3] | mine)
# → 3
```

`~/.config/tosh/autoload/aliases.tosh` has used that spelling for months — `getext`, `getname`,
`up` and `lsrecent` are all written as `$tosh.Function.Input ?? $parameter`. The evidence was in
front of the author of the original item.

## What is actually missing

Two smaller things.

**Pipeline values do not bind to a declared parameter.** A function that names one is told it got
nothing:

```tosh
func mine(items) { … }
echo ([1, 2, 3] | mine)
# ✖ tosh.runtime.function_argument_count_mismatch
# │ Function 'mine' expects 1 argument(s) but received 0.
```

The author *did* supply an argument — through the pipeline, which is the shell's own idiom for
supplying one. Whether that should bind is a design question: `$tosh.Function.Input` is explicit
and composes with a defaulted parameter, and implicit binding would make arity depend on whether a
pipeline is present. But the current arrangement means a builtin and a ToastScript function read
differently at their definition even when they read identically at the call site.

**The diagnostic describes a call the author did not write.** "Received 0 arguments" is true of
the parenthesised argument list and false of the invocation, and it points away from
`$tosh.Function.Input`, which is the answer. It should mention the pipeline when one is present.

## Acceptance

- [x] The arity diagnostic, when a pipeline is present, names `$tosh.Function.Input`
- [x] A decision is recorded on whether a pipeline may bind to a declared parameter — **it
      may not.** See below.
- [x] If it may: arity accounting includes the pipeline-supplied value, and the rule for a
      function with both a pipeline and explicit arguments is specified — **moot; it may
      not, so there is no new accounting and no new rule.**
- [x] The spec documents `$tosh.Function.Input` as *the* way a function consumes the pipeline
- [x] Corpus covers: no parameter, defaulted parameter, required parameter, and both together

## Decision — 2026-09-20: a pipeline does not bind to a declared parameter

`$tosh.Function.Input` stays the one way a function consumes a pipeline. Implicit binding
would make arity depend on whether a pipeline is present, and would need a rule for a
function given both a pipeline and explicit arguments — a rule nothing currently needs.
`~/.config/tosh/autoload/aliases.tosh` has used the explicit spelling for months across
`getext`, `getname`, `up` and `lsrecent`, so it is already the established idiom rather than
a workaround.

## Fix

The asymmetry the item describes was real and sat in one place. `CheckCommandCall` already
carried `receivesPipedInput` and passed it to `CheckBuiltinCommandCall`, which decrements a
builtin's required count — a builtin genuinely does take its subject from the pipe, and
counting only what is written "warned on the ordinary way to use every subject-taking
command". `CheckUserFunctionCommandCall` was never given the flag.

It is given it now, and does *not* decrement, because a ToastScript function does not take
its subject from the pipe. It changes what the diagnostic says:

```
Function 'mine' expects 1 argument(s) but received 0.
  help: A pipeline reaches 'mine' through $tosh.Function.Input, not through its parameter
        list. Read it there, give the parameter a default, or pass the value as an argument.
```

"Received 0" remains true of the parenthesised list and false of the invocation the author
wrote; the help is what closes that gap. Without a pipeline the help is absent, because
there is nothing to explain.

`TypeCheckerTests` covers the four shapes: required parameter with and without a pipeline,
no parameter, and a defaulted parameter. The last two warn about nothing, which is what
consuming a pipeline correctly looks like.
