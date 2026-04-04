# Operators and Expressions

[Back to Index](INDEX.md)

## Arithmetic Operators

| Operator | Description | Example |
|----------|-------------|---------|
| `+` | Addition / string concatenation | `2 + 3` → `5`, `"a" + "b"` → `"ab"` |
| `-` | Subtraction | `10 - 3` → `7` |
| `*` | Multiplication | `4 * 5` → `20` |
| `/` | Division | `10 / 3` → `3` (integer), `10.0 / 3` → `3.333...` |
| `%` | Modulo | `10 % 3` → `1` |

Arithmetic operators also work with `StorageSize`, `TimeSpan`, `DateTimeOffset`, and other numeric CLR types.

```tosh
echo (1gb + 500mb)                  # 1.5 GB
echo ((date now) - (timespan 7d))   # 7 days ago
```

## Comparison Operators

| Operator | Description | Example |
|----------|-------------|---------|
| `==` | Equality | `$x == 42` |
| `!=` | Inequality | `$x != 0` |
| `>` | Greater than | `$x > 10` |
| `>=` | Greater than or equal | `$x >= 10` |
| `<` | Less than | `$x < 100` |
| `<=` | Less than or equal | `$x <= 100` |

Comparison operators perform type-aware comparisons. They work across numeric types, strings, dates, storage sizes, and any `IComparable` type.

```tosh
ls | where _.Size > 1mb
ls | where _.Modified >= (date parse "2024-01-01")
```

## Regex Operators

| Operator | Description | Example |
|----------|-------------|---------|
| `=~` | Regex match | `$name =~ "^test"` |
| `!~` | Regex non-match | `$name !~ "^\\."` |

```tosh
ls | where _.Name =~ "\\.cs$"       # Files ending in .cs
ls | where _.Name !~ "^\\."         # Non-hidden files
```

## Logical Operators

| Operator | Description | Example |
|----------|-------------|---------|
| `and` | Logical AND | `$a > 0 and $a < 100` |
| `or` | Logical OR | `$x == 0 or $x == 1` |
| `not` | Logical NOT (unary) | `not ($x == 0)` |

```tosh
ls | where { _.Type == file; _.Size > 1kb }    # Implicit AND in predicate blocks
ls | where (_.Size > 1mb or _.Name =~ "\\.log$")
```

## String and Collection Operators

| Operator | Description | Example |
|----------|-------------|---------|
| `in` | Membership test | `$ext in [".cs", ".fs", ".vb"]` |
| `not-in` | Negated membership | `$ext not-in [".tmp", ".bak"]` |
| `contains` | Contains check | `$name contains "test"` |
| `starts-with` | String prefix check | `$name starts-with "pre_"` |
| `ends-with` | String suffix check | `$name ends-with ".cs"` |

```tosh
ls | where _.Extension in [".cs", ".fs"]
ls | where _.Name contains "test"
ls | where _.Name starts-with "README"
```

## Type Operators

| Operator | Description | Example |
|----------|-------------|---------|
| `is` | Type check | `$x is int` |
| `is-not` | Negated type check | `$x is-not string` |

```tosh
echo 42 | where _ is int            # Passes
echo [1, "two", 3] | flatten | where _ is string
```

## Null-Coalescing Operator

| Operator | Description | Example |
|----------|-------------|---------|
| `??` | Null coalescing | `$x ?? "default"` |

Returns the left operand if it is not null; otherwise returns the right operand.

```tosh
var name = ($maybeNull ?? "unnamed")
```

## Null-Safe Member Access

| Operator | Description | Example |
|----------|-------------|---------|
| `?.` | Null-safe member access | `$obj?.Property` |

Returns null instead of throwing if the receiver is null.

```tosh
var parent = $entry?.Parent?.Name
$result?.ToString()
```

## Conditional (Ternary) Expression

```tosh
var label = ($count > 0 ? "some" : "none")
echo ($x > 100 ? "big" : "small")
```

## Assignment Operators

| Operator | Description | Example |
|----------|-------------|---------|
| `=` | Assignment | `x = 42` |
| `+=` | Add and assign | `$count += 1` |
| `-=` | Subtract and assign | `$count -= 1` |
| `*=` | Multiply and assign | `$total *= 2` |
| `/=` | Divide and assign | `$value /= 10` |
| `%=` | Modulo and assign | `$value %= 3` |

## Operator Precedence

From highest to lowest precedence:

1. Member access (`.`, `?.`)
2. Method calls (`()`)
3. Unary (`not`, `-`)
4. Multiplicative (`*`, `/`, `%`)
5. Additive (`+`, `-`)
6. Comparison (`<`, `<=`, `>`, `>=`)
7. Equality (`==`, `!=`, `=~`, `!~`)
8. Type check (`is`, `is-not`, `in`, `not-in`, `contains`, `starts-with`, `ends-with`)
9. Logical AND (`and`)
10. Logical OR (`or`)
11. Null coalescing (`??`)
12. Conditional (`? :`)

Use parentheses to override precedence:

```tosh
echo ((2 + 3) * 4)                 # 20, not 14
ls | where (_.Size > 1mb or _.Name starts-with "important")
```

## Subexpressions

### Grouping

```tosh
var result = (2 + 3)               # Evaluate and capture
echo (String.Join " " ["a", "b"])  # Inline expression
```

### Command Substitution

```tosh
var user = $(whoami)                # Capture command output as string
var count = $(ls | count)           # Capture pipeline result
```

### Process Substitution

```tosh
/bin/diff <(cat file1.txt) <(cat file2.txt)    # Input process substitution
command >(write-file output.txt)               # Output process substitution
```

## See Also

- [Language Reference](LANGUAGE.md)
- [Type System](TYPES.md)
- [Pipeline Model](PIPELINES.md)
