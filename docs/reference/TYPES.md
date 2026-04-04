# Type System

[Back to Index](INDEX.md)

ToSh carries real CLR objects through the pipeline. Its “type system” is a mix of:

- normal CLR types
- a few shell-native value types with literal syntax and converters
- a handful of shell-friendly aliases for common CLR types

## Common Scalar Aliases

| Alias | CLR Type |
|-------|----------|
| `string` | `System.String` |
| `char` | `System.Char` |
| `bool` | `System.Boolean` |
| `byte` | `System.Byte` |
| `short` | `System.Int16` |
| `int` | `System.Int32` |
| `long` | `System.Int64` |
| `float` | `System.Single` |
| `double` | `System.Double` |
| `decimal` | `System.Decimal` |
| `object` | `System.Object` |
| `guid` | `System.Guid` |
| `uri` | `System.Uri` |
| `ip` / `ipaddress` | `System.Net.IPAddress` |
| `datetime` | `System.DateTimeOffset` |
| `dateonly` | `System.DateOnly` |
| `timeonly` | `System.TimeOnly` |
| `duration` / `timespan` | `System.TimeSpan` |

## Shell-Native Literal Types

### `StorageSize`

```tosh
1b
512kb
4gb
1tb
```

Useful anywhere ToSh expects a size:

```tosh
ls | where _.Size > 1gb
ls | sum Size
```

### `TimeSpan`

```tosh
500ms
30s
5m
2h
7d
```

Useful for fixed durations:

```tosh
sleep 250ms
var cutoff = (date now) - 7d
```

### `TemporalAmount`

Calendar-aware mixed durations:

```tosh
1y
2mo
1y2mo3d4h
```

Use these when months and years matter semantically instead of collapsing everything into a flat `TimeSpan`.

### `IPAddress`

IPv4 and IPv6 literals are intrinsic:

```tosh
127.0.0.1
::1
2001:db8::1
```

## Date And Time Values

Common date/time types in ToSh:

- `DateTimeOffset`
- `DateTime`
- `DateOnly`
- `TimeOnly`
- `TimeSpan`
- `TemporalAmount`

Examples:

```tosh
date now
date today
date parse 2026-04-03T12:34:56Z
date -dt now
cast dateonly (date now)
cast timeonly (date now)
```

`date -d` returns `DateOnly`.  
`date -t` returns `TimeOnly`.  
`date -dt` returns both values.

## Collections

### Arrays

```tosh
[1, 2, 3]
["a", "b", "c"]
[1, "mixed", true, null]
```

Arrays are ordinary CLR arrays in the pipeline.

### Ranges

```tosh
1..10
1..2..10
```

Ranges are enumerable and work naturally in loops and pipelines.

### Table Literals

```tosh
{ Name = "Alice", Age = 30, Active = true }
```

This is the common open record / table shape for ad hoc structured data in ToSh.

### Shell-Friendly Collection Constructors

```tosh
new list(1, 2, 3)
new dict("name", "Alice", "age", 30)
new set(1, 2, 2, 3)
new tuple(1, "a", true)
```

## Conversion

Typed parameters and `cast` both use ToSh’s conversion system:

```tosh
func recent(span: TimeSpan) { ls | where _.Modified > ((date now) - $span) }
recent 7d

cast int "42"
cast guid "0195e7d1-4b88-7f7a-9a34-12ab34cd56ef"
cast ip 127.0.0.1
```

## Inspection

Useful commands:

```tosh
type-of 42
members (date now)
describe-type dateonly
constructors System.Net.IPEndPoint
```

## Notes

- ToSh display modes change how values render, not what their runtime types are.
- Use `collect` when you want one list value from a stream.
- Use `flatten` when you want to expand nested enumeration by one level.
