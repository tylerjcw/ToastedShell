using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Tosh.Cli")]
[assembly: InternalsVisibleTo("Tosh.Language")]
[assembly: InternalsVisibleTo("Tosh.Stdlib")]
// `TOAST-0007`. The language half of the standard library, which has the same claim the
// shell half does: its CLR commands are the surface these reflection helpers exist to serve,
// and the two were one assembly until the split.
[assembly: InternalsVisibleTo("Toast.Stdlib")]
[assembly: InternalsVisibleTo("Tosh.Tests")]
