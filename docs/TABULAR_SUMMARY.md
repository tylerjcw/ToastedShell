# Tabular Summary Design

## Goal

Add a first-class `summarize` / `summary` pipeline command that computes structured aggregates over pipeline input without mixing the original rows back into the result.

This keeps ToSh object-oriented:

- source rows stay source rows until the user explicitly summarizes them
- summary output is its own reusable object model
- later display-only footer rows can reuse the same aggregate engine without changing pipeline semantics

## Principles

1. `summarize` returns only summary data.
   It does not append synthetic total rows onto the original pipeline.

2. Summary output should be structured and reusable.
   The result should not be preformatted text.

3. Aggregation should operate on the rows actually received.
   Tree-shaped commands such as `lsblk` and `findmnt` summarize their current pipeline rows. Users can switch to `-l` when they want flattened row-by-row summaries.

4. The command should support both explicit and ergonomic modes.
   Users can request exact operations with flags such as `--sum`, `--avg`, `--min`, `--max`, and `--count`, or let ToSh infer the sensible operations automatically.

## Command Surface

```tosh
summarize
summarize _.Used
summarize Size
summarize --sum Size
summarize --sum Size,Used --avg UsePercent
summarize --count
summarize --count Size --min Size --max Size

summary _.Used
summary --sum Size
```

### Supported operations

- `--sum [columns]`
- `--avg [columns]`
- `--average [columns]`
- `--min [columns]`
- `--max [columns]`
- `--count [columns]`

If an operation is provided without columns, it applies to the incoming scalar values. For `--count` without columns, that means row count.

### Auto mode

- `summarize` with no arguments infers every applicable operation for every summarizable column.
- `summarize Size` infers every applicable operation for that one column.
- `summarize _.Used` is shorthand for a single member path and normalizes the label back to `Used`.

Auto mode follows these rules:

- numeric, `StorageSize`, and `TimeSpan` values get `sum`, `avg`, `min`, `max`, and `count`
- `string`, `DateTime`, and `DateTimeOffset` values get `min`, `max`, and `count`
- other values get `count`

Typed object rows participate in auto mode the same way expando/record rows do. ToSh discovers public readable fields/properties plus shell-facing adapted members such as `FsType`.

## Output Model

The command returns one `ColumnSummary` object per summarized column or scalar stream target.

Suggested fields:

- `Column`
- `RowCount`
- `ValueCount`
- `Count`
- `Sum`
- `Average`
- `Min`
- `Max`

Notes:

- `RowCount` is the number of input rows seen by the command.
- `ValueCount` is the number of non-null values found for that column.
- `Count` is the requested aggregate result for `--count`.
- aggregate properties that were not requested remain `null`

## Type Semantics

### `sum` and `average`

Supported:

- numeric values
- `StorageSize`
- `TimeSpan`

### `min` and `max`

Supported:

- numeric values
- `StorageSize`
- `TimeSpan`
- `DateTime`
- `DateTimeOffset`
- `string`

### `count`

Supported for any input.

- without columns: counts input rows
- with columns: counts non-null values projected from that column

## Examples

```tosh
df | summarize
```

```tosh
df | summarize _.Used
```

```tosh
seq 5 | summarize --sum --avg --min --max --count
```

```tosh
lsblk -l | summarize --sum Size
```

```tosh
findmnt -l | summarize --count FsType --sum Size --sum Used
```

```tosh
ps | summarize --avg Memory --max Memory
```

## Follow-Up

After `summarize` lands:

- add display profiles for `ColumnSummary`
- reuse the same aggregate engine for command-local footer rows later
- consider grouped summaries after `group-by`
- consider adding `median`, `mode`, `distinct-count`, and `stddev`
