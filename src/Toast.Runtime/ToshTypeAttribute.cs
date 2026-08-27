namespace Tosh.Runtime;

/// <summary>
/// Marks a CLR type as the shell or metadata representation of a compiled
/// tosh user-defined type (<c>class</c>, <c>record</c>, <c>enum</c>, …).
/// Emitted by <c>Tosh.Compiler.BoundUnitEmitter</c> so external .NET callers
/// can discover compiled tosh types via reflection without re-parsing the
/// source.
///
/// <para>
/// <see cref="Kind"/> identifies which tosh declaration kind the
/// shell originated from (<c>class</c>, <c>record</c>, <c>enum</c>, …).
/// <see cref="SpanStart"/> / <see cref="SpanLength"/> record the
/// offset of the original declaration in the registered source; replay-backed
/// declarations can use those offsets to re-bind full interpreter semantics
/// through <c>ToshHost.RegisterTypeFromSource</c> when needed.
/// </para>
/// </summary>
[AttributeUsage(
    AttributeTargets.Class |
    AttributeTargets.Struct |
    AttributeTargets.Enum |
    AttributeTargets.Interface,
    AllowMultiple = false)]
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
