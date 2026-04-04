namespace Tosh.Language;

internal static class RuntimeNamespaceUtilities
{
    public static bool IsReservedRuntimeNamespaceName(string name)
    {
        return string.Equals(name, "tosh", StringComparison.Ordinal) ||
               string.Equals(name, "env", StringComparison.Ordinal);
    }
}
