# Functional Language Design

## Goal

Add functional-programming features that feel native in ToastScript instead of importing a second language into the shell.

The first priority is **anonymous functions as first-class values**. Once those exist cleanly, higher-order commands and functional helpers become much easier to design and implement.

## Current Foundation

ToastScript already has most of the runtime pieces we want:

- lexical scopes
- captured scopes on `FunctionDefinition`
- blocks as values (`ShellBlock`)
- block-driven pipeline commands like `each`, `where`, `sort`, `take-while`, and `skip-while`
- rich collection and object pipelines

What is missing is not “functional execution” in general. What is missing is:

- a syntax for anonymous functions
- a first-class callable function value
- a standard higher-order toolkit that accepts callables consistently

## Design Principles

### 1. Fit ToastScript, Don’t Clone Another Language

ToastScript already uses:

- `func` for functions
- `{ ... }` for blocks
- `=>` for wrappers and match arms
- `$name` for variable references

New functional features should build on that surface instead of introducing unrelated syntax like `\x -> x + 1` or a Haskell-style operator vocabulary.

### 2. Blocks Stay Important

Block predicates are already good shell syntax:

```tosh
ls | where { _.Type == file }
ps | each { echo _.Name }
```

We should not replace that with lambdas everywhere.

Instead:

- blocks remain the ergonomic pipeline-local tool
- lambdas become the reusable, first-class callable tool

### 3. Prefer Small Composable Features

The first slice should be:

- anonymous function literals
- function invocation for callable values
- a small set of higher-order commands

Not:

- currying
- point-free operators
- custom infix function composition
- a full LINQ clone

## Recommended Lambda Syntax

### Recommendation

Use **anonymous `func` expressions**:

```tosh
func(x) => ($x * 2)
func(x, y) => ($x + $y)
func(item) { echo $item.Name }
func() => (date now)
```

This is the best fit for ToastScript because it:

- reuses the existing `func` keyword
- keeps `=>` aligned with existing function-wrapper style
- avoids parser ambiguity with plain `(x) => ...`
- keeps the mental model simple: named and anonymous functions are the same kind of thing

### Why Not Plain `(x) => ...`?

That syntax is familiar, but it introduces more ambiguity with:

- parenthesized expressions
- subexpressions
- match arm parsing
- existing `=>` uses

It also looks more C#-specific than ToastScript-specific.

`func(x) => ...` is slightly more explicit, but much more coherent with the language we already have.

## Example Gallery

These examples are the intended surface, not implemented syntax today.

### Small Inline Transform

```tosh
echo 1 2 3 | map (func(x) => ($x * 2))
```

### Reusable Predicate

```tosh
var isLarge = func(file) => ($file.Size > 1gb)
ls | filter $isLarge
```

### Closure Over Local State

```tosh
var factor = 10
var scale = func(x) => ($x * $factor)

invoke $scale 5
```

### Object Projection

```tosh
ps | map (func(p) => { Name = $p.Name, Id = $p.Id, Memory = $p.Memory })
```

### Reduction

```tosh
echo 1 2 3 4 | reduce 0 (func(acc, x) => ($acc + $x))
```

### Returning A Function

```tosh
func makeScaler(factor) {
    return func(x) => ($x * $factor)
}

var double = makeScaler 2
invoke $double 21
```

### Block-Body Lambda

```tosh
var describe = func(item) {
    if ($item.Size > 1gb) {
        return $"big: {$item.Name}"
    }

    return $"small: {$item.Name}"
}

ls | map $describe
```

### Typed Parameters

```tosh
var over = func(limit: StorageSize) {
    return func(file) => ($file.Size > $limit)
}

ls | filter (invoke $over 500mb)
```

## Callable Value Model

Anonymous functions should compile to the same underlying concept as named functions:

- parameters
- optional return annotation later if needed
- body block
- captured lexical scopes

That suggests a reusable runtime object, for example:

- `ToshLambda`
- or a generalized callable wrapper over `FunctionDefinition`

Desired behavior:

- can be stored in variables
- can be passed to commands
- can capture local variables
- can be returned from functions
- should display clearly as a callable/lambda value
- should be inspectable without exposing a giant parser dump

Example:

```tosh
var factor = 10
var scale = func(x) => ($x * $factor)
```

`$scale` should keep the captured `factor`.

## Closure Semantics

### Recommendation

Closures should capture **live lexical bindings**, not flattened copies of values.

That means this should work:

```tosh
var total = 0
var add = func(x) {
    $total += $x
    return $total
}

invoke $add 2   # 2
invoke $add 5   # 7
```

Why this is the better default:

- it matches user expectations from modern scripting languages
- it enables useful stateful closures
- it aligns with ToastScript already being a mutable scripting language

We should still keep the implementation scoped and predictable:

- capture the bindings visible at definition time
- do not dynamically re-resolve names through unrelated future scopes
- preserve module/function/block lexical boundaries

## Lambda Body Forms

### Expression Body

```tosh
func(x) => ($x * 2)
func(item) => ($item.Name)
func(a, b) => ($a + $b)
```

Recommendation:

- expression-body lambdas return the value of that expression
- they are the concise default for `map`, `filter`, `reduce`, etc.

### Block Body

```tosh
func(item) {
    if ($item is null) {
        return "<missing>"
    }

    return $item.ToString()
}
```

Recommendation:

- block-body lambdas behave like small normal functions
- `return` works inside them
- they can use `if`, loops, `match`, `try`, and local variables

### Return Type Annotations

First slice recommendation:

- allow typed parameters immediately
- defer explicit lambda return type annotations until later

That keeps the parser and syntax surface smaller:

```tosh
func(size: StorageSize) => ($size > 1gb)
```

Later, if needed:

```tosh
func(x: int) -> int { return ($x * 2) }
```

## Invocation

### First Slice Recommendation

Add an explicit command:

```tosh
invoke $scale 3
invoke (func(x) => ($x * 2)) 21
```

Why this first:

- easiest to implement cleanly
- no parser ambiguity
- fits shell command flow
- works immediately with pipeline composition

### Recommendation: Keep `invoke` Explicit-Arg-First

For the first slice, `invoke` should **not** try to infer arguments from pipeline input.

Good:

```tosh
invoke $scale 3
invoke $join a b c
```

Avoid for now:

```tosh
echo 1 2 3 | invoke $f
```

Why:

- it avoids ambiguity between “pipeline input as input stream” and “pipeline input as argument list”
- it keeps `invoke` small and unsurprising
- higher-order commands like `map` and `filter` already provide the natural pipeline-integrated story

### Possible Later Sugar

Potential later sugar, only if it proves worth it:

```tosh
$scale(3)
```

But that should be a second step, not the first.

## Higher-Order Command Semantics

### `map`

`map` should call the lambda once per input item and emit the returned values.

```tosh
ls | map (func(item) => ($item.Name))
```

Equivalent spirit:

```tosh
ls | each { _.Name }
```

Difference:

- `each` is block-first and shell-local
- `map` is callable-first and reusable

### `filter`

`filter` should preserve the original item when the callable returns `true`.

```tosh
ls | filter (func(item) => ($item.Type == file))
```

It should not emit the callable's result. It should emit the input item, like standard functional filter semantics.

### `reduce`

`reduce` should accept:

- an initial accumulator
- a callable taking `(acc, item)`

```tosh
echo 1 2 3 4 | reduce 0 (func(acc, x) => ($acc + $x))
```

Recommendation:

- always require an explicit initial value in the first slice
- do not add a “first item as seed” overload yet

That avoids edge cases around empty input and mixed typing.

### `any` / `all` / `none`

These should return booleans:

```tosh
ps | any (func(p) => ($p.Name == "sshd"))
ps | all (func(p) => ($p.Responding))
ls | none (func(item) => ($item.Type == link))
```

## Functional Toolkit

Once anonymous functions exist, add a small, high-value toolkit.

### Recommended First Commands

- `map`
- `filter`
- `reduce`
- `any`
- `all`
- `none`

Examples:

```tosh
echo 1 2 3 | map (func(x) => ($x * 2))
ls | filter (func(item) => ($item.Type == file))
echo 1 2 3 4 | reduce 0 (func(acc, x) => ($acc + $x))
ps | any (func(p) => ($p.Name == "sshd"))
```

### Relationship To Existing Commands

These should not replace the current shell-style commands:

- `map` complements `each`
- `filter` complements `where`
- `reduce` is new
- `any` / `all` / `none` are new

Rough guidance:

- use blocks when the transformation is local and shell-like
- use lambdas when the callable is reusable, passed around, or stored

## Interop With Existing Commands

### First Recommendation

Do not immediately retrofit every block-accepting command.

Instead:

- add a small callable-first toolkit
- let users get value from lambdas immediately
- then selectively allow callable values in existing commands

This is lower risk than changing `where`, `each`, `sort`, and friends all at once.

### Later Interop Targets

Once lambdas are established, these should probably accept either a block or a callable:

- `where`
- `each`
- `sort`
- `take-while`
- `skip-while`
- `group-by`

Examples of the desired eventual surface:

```tosh
var isService = func(u) => ($u.Type == service)
systemctl | where $isService

var byMemory = func(p) => ($p.Memory)
ps | sort $byMemory
```

## Block And Lambda Interop

This is now part of the implemented direction: commands that previously only accepted `ShellBlock` can increasingly accept callable values where that makes sense.

Examples:

```tosh
var isFile = func(item) => ($item.Type == file)
ls | where $isFile

var showName = func(item) { echo $item.Name }
ls | each $showName
```

That compatibility pass is underway now. The remaining work is mostly consistency, ergonomics, and deeper hardening rather than proving out the basic model.

## Nice Future Features

These are good future candidates after the basics land:

- `sort-by <callable>`
- `group-by <callable>`
- `find-first <callable>`
- `partition <callable>`
- `zip`
- `flat-map`

Potentially later, if justified:

- partial application
- composition helpers
- collection instance methods over shell lists/arrays
- callable-aware `sort`
- callable-aware `where` / `each`

## Syntax We Should Explicitly Reject

To keep the language coherent, these should stay out unless we discover a very strong need:

### Bare C#-Style Lambdas

```tosh
(x) => ($x * 2)
```

Rejected because:

- parser ambiguity
- duplicates `func(...) => ...`
- pushes ToastScript toward “C# shell clone” surface syntax

### Backslash / Arrow Functional Syntax

```tosh
\x -> x * 2
```

Rejected because it is alien to the rest of the language.

### Implicit Placeholder Lambdas

```tosh
map (_.Name)
```

Tempting, but too magical for the first slice. It becomes hard to tell when `_` means:

- current pipeline item
- lambda parameter
- predicate-local item

We already have a clear block story for `_`, so we should not overload it further yet.

## Features We Should Avoid For Now

- point-free syntax
- custom pipe-to-function-application operators
- monadic vocabulary in the surface language
- implicit currying
- multiple competing lambda syntaxes

Those would add complexity faster than they add practical shell value.

## Implementation Order

### Phase 1

- parse anonymous `func(...) => ...` and `func(...) { ... }` expressions
- create a first-class callable lambda runtime object
- support lexical capture
- add `invoke`
- add clear rendering/inspection for callable values
- document invocation and closure behavior

### Phase 2

- add `map`, `filter`, `reduce`, `any`, `all`, `none`
- support passing callable values to those commands
- keep `reduce` initial-value-first

### Phase 3

- allow selected existing block commands to accept callable values too
- consider invocation sugar like `$f(...)`
- consider `sort-by`, `group-by`, and `flat-map`
- consider typed lambda return annotations

## Recommendation Summary

If we implement one functional slice next, it should be:

1. anonymous `func(...) => ...` expressions
2. explicit `invoke`
3. `map`, `filter`, `reduce`

That gets ToastScript real functional power without losing the shell’s current feel.

## Short Recommendation

If we want the feature to feel elegant in practice, the “happy path” should look like this:

```tosh
var nameOf = func(item) => ($item.Name)
ls | map $nameOf

var big = func(item) => ($item.Size > 1gb)
ls | filter $big

echo 1 2 3 4 | reduce 0 (func(acc, x) => ($acc + $x))
```

That is terse, readable, and still unmistakably ToastScript.
