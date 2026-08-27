namespace Tosh.Runtime;

/// <summary>
/// Marks an assembly as containing one or more compiled tosh
/// modules. Emitted by <c>Tosh.Compiler.BoundUnitEmitter</c> for
/// every top-level <c>module</c> declaration so that downstream
/// tooling (the runtime, C# consumers, IDE integrations) can
/// discover compiled modules via reflection without re-parsing the
/// source.
///
/// <para>
/// <see cref="QualifiedName"/> is the dotted module path
/// (e.g. <c>App.Math</c>). <see cref="Span"/> records the
/// source-text offset and length of the module body in the
/// assembly's registered source so the host bridge can replay the
/// definition through <c>ToshHost.RegisterModuleFromSource</c> on
/// load.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class ToshModuleAttribute : Attribute
{
    public ToshModuleAttribute(string qualifiedName, int spanStart, int spanLength)
    {
        QualifiedName = qualifiedName;
        SpanStart = spanStart;
        SpanLength = spanLength;
    }

    public string QualifiedName { get; }

    public int SpanStart { get; }

    public int SpanLength { get; }
}
