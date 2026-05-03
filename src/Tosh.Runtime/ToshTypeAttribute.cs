namespace Tosh.Runtime;

/// <summary>
/// Marks a CLR type as the shell of a compiled tosh user-defined
/// type (<c>class</c>, <c>record</c>, …). Emitted by
/// <c>Tosh.Compiler.BoundUnitEmitter</c> alongside the source-replay
/// registration so external .NET callers can discover compiled
/// tosh types via reflection without re-parsing the source.
///
/// <para>
/// <see cref="Kind"/> identifies which tosh declaration kind the
/// shell originated from (<c>class</c> or <c>record</c> in v1).
/// <see cref="SpanStart"/> / <see cref="SpanLength"/> record the
/// offset of the original declaration in the assembly's registered
/// source so a host bridge can re-bind the full semantics through
/// <c>ToshHost.RegisterTypeFromSource</c> on load.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class ToshTypeAttribute : Attribute
{
    public ToshTypeAttribute(string kind, int spanStart, int spanLength)
    {
        Kind = kind;
        SpanStart = spanStart;
        SpanLength = spanLength;
    }

    public string Kind { get; }

    public int SpanStart { get; }

    public int SpanLength { get; }
}
