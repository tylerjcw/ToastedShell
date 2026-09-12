---
id: TOAST-0128
title: "The specification's own code listings did not parse, in four places where the parser was narrower than the language it documents"
status: complete
area: toast
priority: 2
opened: 2026-09-12
---

## Problem

**Nothing checked that the code in the specification was code.** The document is
8,700 lines with 321 listings, and it is the language's definition — so a listing
that does not parse is either a defect in the parser or a lie in the definition,
and neither had a way of being noticed.

Parsing all 321 found five disagreements. Four were the parser being narrower than
the language the specification describes:

```tosh
# §Throw Expressions documents three positions side by side.
var x    = ($valid ? $value : throw "validation failed")   # parsed
var label = match ($input) { default => throw "bad" }      # parsed
var conn = $connectionString ?? throw "required"           # tosh.parser.missing_pipeline_separator

# §Events writes one field per line, as every other block in the language does.
event BuildCompleted {
    Project  = ""          # tosh.parser.missing_pipeline_separator at `Duration`
    Duration = (timespan 0s)
}
required event CriticalError { Message = "", Code = 0 }    # and again at `Code`

# §Event Handlers writes `priority` before `when` — the one order that failed.
func onBigCommand(event) handles CommandCompleted priority 10
    when { $event.Duration > (timespan 1s) }               # tosh.parser.expected_block
{ ... }
```

The fifth was the specification's: a loop binding written `for $e in $events`,
which is not how the language spells one. It was the only occurrence in the
document.

## Why the parser rather than the prose

The specification is the contract, so a disagreement is the implementation's
defect unless the prose is wrong about the language's intent. Here it was not.
Nothing about `??`, an event body, or three unordered modifier clauses implied the
restriction that existed — each was an accident of where the code happened to sit:

- **`throw` after `??`** — the throw-expression form lived *inside* the ternary
  branch parser and had no other caller, so the `??` right operand fell through to
  the command parser and read `throw` as a command name. It is a helper now, and a
  third position is a call rather than a copy.
- **An event field default** ran its pipeline past the end of its own line and read
  the next field as a second stage. It now ends at a field boundary — a line break,
  or a comma outside a literal. Not `singleExpressionBody`, which also ends a
  pipeline at a `|`: that is right for a lambda body and wrong here, where
  `Count = $items | count` is a perfectly good default. The boundary test sits after
  the pipe branch, so a continuation written with a leading `|` still joins.
- **The handler clauses** were a fixed sequence — `when`, then `priority`, then
  `once` — rather than a loop, so five of the six orders failed and the error named
  the missing block instead of the clause in the wrong place.

## Acceptance

- [x] `$value ?? throw "…"` parses and raises, and does **not** evaluate the throw
      when the left operand is non-null — the half a parse check cannot see
- [x] An event body separates its fields by line break, semicolon **or** comma, and
      a field default may still be a pipeline
- [x] `handles` takes `when`, `priority` and `once` in any order, and a non-integer
      priority is still reported where it stands
- [x] The specification's `for $e in` is `for e in`
- [x] `SpecificationListingsParseTests` parses all 317 listings that are whole
      programs on every run. A deliberate excerpt — signature lines lifted out of a
      `bind native` block, a body elided as `{ ... }`, CSV input — carries a
      `% spec-check: fragment --- <why>` LaTeX comment, which does not render
- [x] A negative control asserts the check still covers more than 300 listings and
      that no more than ten are excluded, so a marker cannot be used to silence a
      real failure
- [x] Confirmed load-bearing: restoring the `for $e in` typo fails the check
- [x] Full suite green — 7,568 passing, with only the four pre-existing
      `CompilerRuntimeStagingTests` failures from uncommitted compiler work
