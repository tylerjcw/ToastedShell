# Binder benchmark — 2026-04-29

Hardware: AMD Ryzen 9 9950X · 32GB · Arch Linux · .NET 10.0.3
Code: master @ e7df0bd (post bind-time `[ShellOnly]` enforcement)

Run with:

    dotnet run -c Release --project bench/Tosh.Benchmarks -- --filter '*Binder*'

## Results

| Method              | Mean         | Allocated  | Notes                          |
|---------------------|-------------:|-----------:|--------------------------------|
| Tiny                |     1.194 µs |   1.14 KB  | `ls -la` — typical REPL line   |
| Small               |     3.431 µs |   2.77 KB  | 5-stage pipeline               |
| Medium              |    28.971 µs |  27.55 KB  | ~50 lines, vars/funcs/control  |
| Large               | 1,660.776 µs | 1304.94 KB | 500-line synthetic stress      |
| LargeNonInteractive | 1,658.782 µs | 1304.93 KB | same script, isInteractive=false |

## Takeaways

1. **Binder is not a hot spot.** A REPL line binds in ~1µs; a realistic
   profile/script binds in <30µs. Even the 500-line stress case stays
   under 2ms.

2. **`[ShellOnly]` reflection check is free.** The interactive vs.
   non-interactive runs are within noise; the `GetCustomAttribute`
   call for resolved commands does not register.

3. **Allocation dominates time.** ~2.6 KB per parsed command for the
   bind pass is the obvious lever if we ever need to optimize — but
   we don't, today.

4. **Phase 1.5 BoundCommand fast-path is unjustified by perf.**
   Caching resolved registry lookups on the AST would shave µs off
   inputs that already complete in µs. Re-evaluate only if later
   features (e.g. richer scope analysis, generics, etc.) push these
   numbers up by 10-100×.
