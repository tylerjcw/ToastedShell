namespace Tosh.Core;

/// <summary>
/// Marks a <see cref="ShellCommand"/> subclass as deliberately missing a particular
/// documentation field. Skipped by <c>DocumentationCoverageTests</c>.
/// </summary>
/// <remarks>
/// Use sparingly. Recognized field values match the documentation tier names:
/// <list type="bullet">
///   <item><c>"category"</c> — opt out of the [CommandCategory] requirement.</item>
///   <item><c>"example"</c> — opt out of the [CommandExample] requirement.</item>
///   <item><c>"output"</c> — opt out of the [CommandOutput] requirement.</item>
/// </list>
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class UndocumentedForAttribute : Attribute
{
    public UndocumentedForAttribute(string field, string reason)
    {
        Field = field;
        Reason = reason;
    }

    public string Field { get; }
    public string Reason { get; }
}
