---
id: TOAST-0101
title: "A pipeline into a user function does not bind to a parameter, and the arity error misdescribes why"
status: proposed
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

- [ ] The arity diagnostic, when a pipeline is present, names `$tosh.Function.Input`
- [ ] A decision is recorded on whether a pipeline may bind to a declared parameter
- [ ] If it may: arity accounting includes the pipeline-supplied value, and the rule for a
      function with both a pipeline and explicit arguments is specified
- [ ] The spec documents `$tosh.Function.Input` as *the* way a function consumes the pipeline
- [ ] Corpus covers: no parameter, defaulted parameter, required parameter, and both together
