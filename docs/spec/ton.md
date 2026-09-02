# Tōast Object Notation

**Status:** draft, tracking `TOAST-0092`.
**File extension:** `.ton`. **Media type:** `text/plain; charset=utf-8`.
**Verbs:** `to ton` writes, `from ton` reads. `toast` is an accepted alias for the format name.

---

## What it is

TON is **the subset of Tōast's own value syntax that is meaningful without a schema** — the
relationship JSON has to JavaScript, EDN to Clojure, `literal_eval` to Python.

Every TON document is valid Tōast source. That is the property the design rests on, and it has a
practical consequence worth stating first: there is one grammar and one parser. A reader is not
a second implementation that can drift from the writer, and a document cannot mean one thing to
the notation and another to the language.

It exists because no format carried enough to get a value back. `$v | to json | from json`
returns an `ExpandoObject`, never the type that went in — a `Villager` written out and read back
was a bag of fields that no longer matched `match`, narrowing, or its own methods.

```tosh
new Villager {|
    # bumped after the trade rebalance
    Name = "Steve"
    Job  = Profession::Librarian
    Trades = [
        new Exchange(Item = "Emerald", Amount = 1),
    ]
|}
```

Comments are part of the notation. So are trailing commas and newline separators.

---

## Grammar

Written to be small enough to implement in another language. `IDENT` is a Tōast identifier;
`STRING`, `NUMBER` and `QUANTITY` are Tōast literals.

```
document      := value*

value         := literal
               | array | dict | set | tuple | record
               | construction
               | path
               | variant

literal       := NUMBER | STRING | "true" | "false" | "null" | QUANTITY

array         := "[" ( value ("," value)* ","? )? "]"
set           := "{:" ( value ("," value)* ","? )? ":}"
tuple         := "(" value ("," value)+ ","? ")"
dict          := "{%" ( entry ("," entry)* ","? )? "%}"
entry         := value "=>" value

record        := "{|" ( field (sep field)* sep? )? "|}"
field         := IDENT "=" value
sep           := "," | NEWLINE

construction  := "new" typename ( "(" named-args? ")" )? ( record )?
named-args    := field ("," field)* ","?

path          := typename "::" IDENT
variant       := typename "::" IDENT type-args? "(" args? ")"
type-args     := "<" typename ("," typename)* ">"
args          := value ("," value)* ","?          # positional only

typename      := IDENT ("::" IDENT)*
```

A `#` begins a comment that runs to end of line, anywhere whitespace is allowed.

---

## Rules a grammar cannot express

Three, in decreasing order of how much they matter.

### 1. Only Tōast-declared types are admitted

Not CLR types. `new System.Diagnostics.ProcessStartInfo("ls")` is ordinary Tōast, and a notation
resolving through the same path would let a document name **any public type in any loaded
assembly**. That is the `TypeNameHandling` and Java gadget surface exactly: the attacker supplies
no code, only the name of a type whose *construction* does something.

Restricting to types the reading program declares closes the class structurally. There is no
blocklist to keep up to date.

```
new System.Text.StringBuilder()
→ A TON document cannot contain 'System.Text.StringBuilder'.
```

### 2. A path is a lookup, never an invocation

`Profession::Librarian` resolves by member name. Member access is not in the grammar at all, so
`DateTime::Now` cannot be written; and a path's head must name a declared type, so a static
property is refused on the *kind* of its type rather than by naming the member:

```
System::Math::PI
→ A TON document cannot contain 'System.Math'.
```

Whatever .NET adds later is refused by the same rule, unchanged.

### 3. Validation precedes evaluation

The document is parsed, walked, and only then evaluated. A construct outside the notation is
refused before it can do anything, rather than being caught partway through doing it.

**The validator is an allowlist.** The language has forty argument node kinds; the notation
admits about ten. Naming what is permitted means a node kind added to the language later is
refused by default. Naming what is forbidden would admit it by default, and the difference
between those two defaults is the whole security posture.

---

## Named fields, not positions

`new Exchange("Emerald", 1)` parses anywhere and *means* nothing without knowing the field
order. Reordering a record's fields would silently corrupt every existing document, so positional
constructor arguments are refused:

```
new Exchange("Emerald", 1)
→ A TON document cannot contain a positional constructor argument.
```

**Union variants are the exception, and are always positional.** A variant's field names are for
*pattern matching* and member access; construction takes positions only, so
`Shape::Circle(radius = 2)` parses and then fails to convert the named argument to the field's
type. Writing it would emit a document the language cannot read back — which the conformance
corpus caught, and which is the one bug a round trip is supposed to make impossible.

Portability is not weakened the way it would be for a record: a variant's arity and order are
fixed by its declaration, and there is no named alternative to fall back to, so there is nothing
a document could have said more robustly.

This is why the notation is "the subset that is meaningful without a schema" rather than "a
subset of the grammar". Parsing is schema-free; *binding* needs the schema. A foreign reader
produces a faithful tree and resolves nothing, exactly as a JSON reader handles a `$type` key.

---

## How each shape is written

| shape | written as |
|---|---|
| record | `new Exchange(Item = "Emerald", Amount = 1)` |
| class, struct | `new Villager {\| Name = "Steve" \|}` |
| class with constructor | `new Villager(name = "Steve") {\| Level = 3 \|}` |
| enum member | `Profession::Librarian` |
| union variant | `Shape::Circle(2)` — always positional |
| quantity | `483.06\`MW` |
| array, dict, set | `[1, 2]`, `{% "k" => 3 %}`, `{: "a", "b" :}` |

A record's fields *are* its constructor parameters, which is why it takes named arguments rather
than a `{| … |}` literal — there is no zero-argument constructor to fill afterwards.

**Type arguments appear only where the payload cannot supply them.** `Option::Some(5)` pins `T`
from its payload; `Option::None<int>()` and `Result::Ok<int, string>(3)` cannot, so they carry
theirs. This is the shortest spelling that still reconstructs without a target type — which
matters because a heterogeneous stream has no single target to supply, and is why the notation
needs no `--as <type>` flag and no envelope.

### Known gap

An immutable struct with no constructor and only properties has no literal form: it cannot be
filled after construction, and has no constructor to fill it during. Rare, and recorded rather
than solved.

---

## A document is a sequence of values

No envelope, no root object. Every value carries its own type, so a heterogeneous stream needs
nothing wrapped around it:

```tosh
new Exchange(Item = "Emerald", Amount = 1)
Profession::Librarian
483.06`MW
```

---

## Conformance

An implementation conforms if, for every document in the corpus, it agrees on **accept or
refuse** — and for accepted documents, produces a tree with the same shape and values.

Resolution is explicitly *not* required. A reader in another language cannot know what types the
producing program declared, so it may produce a faithful unresolved tree, in the way a JSON
reader handles `$type`. What it must not do is accept a document this specification refuses.

The corpus lives beside this document as `ton-conformance/`, split into `accept/` and `refuse/`.
Each refusal case names the rule it violates.
