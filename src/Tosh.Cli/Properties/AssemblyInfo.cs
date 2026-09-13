using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Tosh.Tests")]

// The browser screens are the only realistic full-screen render this repository has, so
// the TUI benchmarks measure them rather than a synthetic stand-in (TUI-0012).
[assembly: InternalsVisibleTo("Tosh.Benchmarks")]
