---
id: TOAST-0034
title: "A declared type is not used: the compile-time inferrer pins down literals and `new` and nothing else"
status: complete
area: toast
priority: 2
opened: 2026-08-21
closed: 2026-08-22
---

## Problem

Phase B's first bullet is "complete type-system support needed by compiler data
structures". Measured rather than assumed, the gap is sharper than that wording: **a type
the author has already written down is not used.**

```tosh
func f() -> int => 7
var v = f()          # tosh.compile.implicit_dynamic: could not pin down a concrete type
```

The return type is declared. The inferrer does not consult it.

## Measured 2026-08-21

Each case compiled on its own with `tosh --compile`, no flags:

| Expression | Inferred? |
|---|---|
| `var a = 1` | yes |
| `var xs = [1, 2, 3]` | yes |
| `var k = new K()` — a declared class | yes |
| `var v = f()` where `func f() -> int` | **no** |
| `var v = $k.M()` where `func M() -> int` | **no** |
| `var v = $k.V` where `prop V: int` | **no** |
| `var h = new System.Collections.Hashtable()` | **no** |
| `var v = Math.Round(1.5, 0)` | **no** |
| `var r = {\| a = 1 \|}` | **no** |

So inference reaches literals and a user-class construction, and stops at every call and
every member read. Annotating the *variable* works (`var v: int = (new K()).M()` compiles),
which is what makes this a propagation gap rather than a representation one: the compiler
can hold the type, it just will not derive it.

## Why this is the load-bearing one

`TOAST-0038`'s readiness probe fails with four `implicit_dynamic` errors, and every one is
a value that came from a call. Compiler-shaped code is mostly calls — a lexer returns
tokens, a parser returns a tree — so this decides whether Phase B's exit is reachable by
fixing the compiler or only by annotating every local in every program.

## Progress — 2026-08-21

| Expression | before | after |
|---|---|---|
| `var a = 1` | yes | yes |
| `var xs = [1, 2, 3]` | yes | yes |
| `var k = new K()` | yes | yes |
| `var v = f()` where `func f() -> int` | no | **yes** |
| `var v = $k.M()` where `func M() -> int` | no | **yes** |
| `var v = $k.V` where `prop V: int` | no | **yes** |
| `var h = new System.Collections.Hashtable()` | no | **yes** |
| `var n = $b.Length` on a CLR value | no | **yes** |
| `var s = $b.ToString()` on a CLR value | no | **yes** |
| `var v = Math.Sqrt(2.0)` | no | **yes** |
| `var v = Math.Round(1.5, 0)` | no | no — *correctly* |
| `var r = {\| a = 1 \|}` | no | no — still open |

### Four changes, each in the place that already knew the answer

1. **A local function's declared return** now rides on `BoundCommandCall.LocalReturnType`. A
   user function is *called as a command*, and command output was inferred solely from
   `[CommandOutput]` — an attribute only builtins carry.
2. **A property read and a method call** consult `UserTypeMembers`, which already existed,
   and fall back to reflection on a concrete CLR target.
3. **`TypeNameResolver` gained the platform index** — static, so it needs no runtime and does
   not compromise the resolver running without one. It is the same index `is` uses for a
   bare CLR name and the compiled `new` uses for a bare type name, so this aligns a fourth
   opinion with three existing ones rather than inventing one.
4. **Static calls** resolve their owner through that same index.

### The rule that runs through all four

**Overloads that disagree about a return type say nothing about a call.** One declaring a
type while another does not is a disagreement. That is why `Math.Round` still infers
nothing: it returns `double` for one signature and `decimal` for another, and answering
either without resolving the arguments would be a guess. Both are in the corpus, on
opposite sides.

### What it bought

The readiness probe (`TOAST-0038`) went from **six** strict errors to **five**, and the
character of what remains changed completely: every one is now the probe declaring no return
type of its own, not the compiler failing to propagate one. `Tokenize()`, `ParseExpr()` and
`Compile()` are unannotated, which is `TOAST-0038`'s work.

Proved directly rather than inferred from the count — a compiler-shaped program with a
class, a declared method return, a local taking that method's result, and a function with a
declared return now compiles with **no annotations on any local**, and runs.

### The last row — 2026-08-22

**A record literal infers `System.Dynamic.ExpandoObject`.** It needed a type rather than a
lookup, and the type it needed already existed: a record *is* an `ExpandoObject` on both
backends — the interpreter builds one and `EmitRecordLiteral` emits one. Inventing a
structural record type here would have introduced a fifth opinion about what a record is,
which is the mistake the four changes above were written to avoid.

The risk was real and was measured before choosing rather than after. A record's fields are
not CLR properties, so giving the literal a **concrete** type could have turned `$r.a` into a
static member lookup and traded one `implicit_dynamic` for a worse failure. Annotating a
variable with the concrete `ExpandoObject` and reading a field back off it compiles and
prints on both backends, so the concrete type is safe — and the tests read the field back
rather than trusting that.

| Expression | before | after |
|---|---|---|
| `var r = {\| a = 1 \|}` | no | **yes** |
| `var r = {\| a = 1, b = "x" \|}` | no | **yes** |
| `var r = {\| a = {\| b = 2 \|} \|}` — nested | no | **yes** |

### What it turned up

The first differential case for this was written `echo $r.a $r.b`, and it diverged — because
**`echo` with several arguments** emits one value per argument interpreted and a single
joined string compiled. Nothing to do with records, and not previously recorded: every corpus
case had used a single argument. Filed as `TOAST-0067` and added to `KnownDivergences()`; the
record case was rewritten with interpolation, since it is about inference.

## Acceptance

- [x] A call to a function with a declared return type infers that type
- [x] A method call on a value of known type infers the method's declared return type
- [x] A property read infers the property's declared type
- [x] `new SomeClrType()` infers that type
- [x] A record literal infers a record type
- [x] A CLR static call infers its return type when its overloads agree on one — and
      declines when they do not, which is not the same as failing
- [x] The table above becomes a test, including the rows that already pass as controls
- [x] A negative control

## Notes

Found while turning Phase B's five bullets into items — the bullets were transcribed from
`docs/SELF_HOSTING_RFC.md` and each was measured before being written down. This one came
back much more specific than its bullet.
