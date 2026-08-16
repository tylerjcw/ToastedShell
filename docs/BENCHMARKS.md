# Pipeline benchmarks — 2026-04-30

> **Stale — measured 2026-04-30 and not re-run.** These numbers predate the
> performance work of July and August 2026, some of which moved the same paths by an
> order of magnitude: interpolation went from 19,562 ns to 996 ns per operation, an
> annotated one-million-iteration loop from 11.9 s to 1.487 s, and a CLR method call
> from 4,095 ns to 2,810 ns. Treat every figure below as a historical reading on
> `a1fa021`, not as current performance. Re-measure before citing.

Hardware: AMD Ryzen 9 9950X · 32 GB · Arch Linux · .NET 10.0.3
Code: master @ a1fa021 (post precise interpolation spans)

Run with:

    dotnet run -c Release --project bench/Tosh.Benchmarks -- --filter '*Benchmarks*'

The benchmark project lives in `bench/Tosh.Benchmarks/` and uses
BenchmarkDotNet 0.14 with `MemoryDiagnoser` enabled.

---

## Binder — `Binder.Bind(parseResult, commands)`

Parsing is done once in `[GlobalSetup]`, so timings cover the bind pass
only.

| Method              | Mean         | Allocated  | Notes                            |
|---------------------|-------------:|-----------:|----------------------------------|
| Tiny                |     1.194 µs |   1.14 KB  | `ls -la` — typical REPL line     |
| Small               |     3.431 µs |   2.77 KB  | 5-stage pipeline                 |
| Medium              |    28.971 µs |  27.55 KB  | ~50 lines, vars/funcs/control    |
| Large               | 1,660.776 µs | 1304.94 KB | 500-line synthetic stress        |
| LargeNonInteractive | 1,658.782 µs | 1304.93 KB | same script, isInteractive=false |

**Takeaways**

1. **Binder is not a hot spot.** A REPL line binds in ~1 µs; a
   realistic profile/script binds in <30 µs. Even the 500-line stress
   case stays under 2 ms.
2. **`[ShellOnly]` reflection check is free.** The interactive vs.
   non-interactive runs are within noise.
3. **Allocation dominates time.** ~2.6 KB per parsed command for the
   bind pass is the obvious lever if optimization is ever needed.

---

## Parser — `ToshEngine.Parse(source, name)`

Lex + parse only. No binding, no evaluation. Inputs match the binder
benchmarks for direct comparison.

| Method | Mean         | Allocated  | Notes                            |
|--------|-------------:|-----------:|----------------------------------|
| Tiny   |     0.965 µs |   0.97 KB  | `ls -la`                         |
| Small  |     1.869 µs |   3.17 KB  | 5-stage pipeline                 |
| Medium |    20.033 µs |  26.77 KB  | ~50 lines, vars/funcs/control    |
| Large  |   869.940 µs | 1390.59 KB | 500-line synthetic stress        |

**Takeaways**

1. **Parser is faster than the binder per command.** ~1 µs per
   pipeline stage. Combined parse + bind for a typical interactive
   command stays under ~5 µs.
2. **Parser and binder allocations are comparable in magnitude.**
3. **A 500-line script costs ~0.9 ms to parse and another ~1.6 ms
   to bind.** Fine for shell startup; matters for an incremental
   compiler driving many files.

---

## Evaluator — full pipeline (`ExecuteToListAsync`)

Includes parse + bind + evaluation. Inputs are pure (no I/O, no
external processes) so timings reflect the engine itself.

| Method              | Mean       | Allocated   | Notes                                    |
|---------------------|-----------:|------------:|------------------------------------------|
| Echo                |   3.19 µs  |   4.65 KB   | `echo hello`                             |
| VariableDeclaration |   4.13 µs  |   6.85 KB   | `var x = 42; echo $x`                    |
| ListSum             |   3.42 µs  |   6.31 KB   | `1..10 \| sum`                           |
| FunctionCall        |   6.84 µs  |  13.52 KB   | declare + call `square 7`                |
| ForLoop             |  12.05 µs  |  29.52 KB   | 5-iteration `for…in [1..5]`              |
| InterpolatedString  |   6.04 µs  |  11.23 KB   | `$"hello, {$name}!"`                     |
| WhereSort           | 150.89 µs  | 466.08 KB   | `1..100 \| where >50 \| sort \| first 5` |

**Takeaways**

1. **Trivial commands run in ~3 µs.** The interactive REPL is bound
   by terminal I/O, not by the engine.
2. **Function-call dispatch costs ~3 µs of overhead.** Acceptable for
   typical shell depths; relevant for recursion-heavy workloads.
3. **`WhereSort` is the outlier — 150 µs and ~466 KB.** Pipeline
   materialization currently boxes every element to `object`; a
   bound IR with concrete element types would shrink that
   dramatically.
4. **For-loop overhead is ~2.4 µs/iteration.** Fine for shell
   workloads; the next obvious target for a compiled backend.

---

## Strategic implications for the IL backend

The `WhereSort` result is the clearest signal: the evaluator boxes
every pipeline element. An IL backend that specializes pipeline
stages on element type (`int`, `string`, `FileSystemEntry`) should
yield roughly an order of magnitude improvement on numeric pipelines
— independent of any binder/parser work.
