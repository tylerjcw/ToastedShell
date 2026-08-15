using Tosh.Runtime;
using Tosh.Language.Parsing;

namespace Tosh.Language;

public sealed class ToshInterfaceDefinition : IShellNamedType
{
    /// <summary>The declaration's own `##` documentation (`TS-P2-101`).</summary>
    public DocComment? Documentation { get; internal set; }

    /// <inheritdoc />
    public string? ShellDocumentation => Documentation?.Description is { Length: > 0 } summary
        ? summary
        : null;

    public ToshInterfaceDefinition(
        string name,
        IReadOnlyList<InterfaceMethodSignature> methods,
        string sourceName,
        string sourceText,
        TextSpan span,
        IReadOnlyList<string>? typeParameterNames = null,
        IReadOnlyList<TypeParameterConstraintSyntax>? typeParameterConstraints = null,
        IReadOnlyList<TypeParameterVariance>? typeParameterVariances = null,
        DocComment? documentation = null)
    {
        Documentation = documentation;
        Name = name;
        Methods = methods;
        SourceName = sourceName;
        SourceText = sourceText;
        Span = span;
        TypeParameterNames = typeParameterNames ?? Array.Empty<string>();
        TypeParameterConstraints = typeParameterConstraints ?? Array.Empty<TypeParameterConstraintSyntax>();
        TypeParameterVariances = typeParameterVariances ?? Array.Empty<TypeParameterVariance>();
    }

    public IReadOnlyList<string> TypeParameterNames { get; }

    public IReadOnlyList<TypeParameterConstraintSyntax> TypeParameterConstraints { get; }

    /// <summary>
    /// Per-type-parameter variance markers. Aligned with
    /// <see cref="TypeParameterNames"/> by position; an empty list
    /// implies all parameters are invariant. Currently honored only
    /// when judging assignability between two
    /// <c>GenericInstanceType</c>s wrapping the same interface
    /// template (covariant args allow widening; contravariant args
    /// allow narrowing; invariant args require exact match).
    /// </summary>
    public IReadOnlyList<TypeParameterVariance> TypeParameterVariances { get; }

    public string Name { get; }

    public IReadOnlyList<InterfaceMethodSignature> Methods { get; }

    public string SourceName { get; }

    public string SourceText { get; }

    public TextSpan Span { get; }

    public string ShellTypeName => Name;

    public string ShellFullName => Name;

    public string? ShellNamespace => null;

    public string? ShellAssemblyName => "ToSh";

    public string? ShellBaseTypeName => null;

    public bool ShellIsClass => false;

    public bool ShellIsInterface => true;

    public bool ShellIsEnum => false;

    public bool ShellIsValueType => false;

    public bool ShellIsAbstract => true;

    public bool ShellIsGenericType => TypeParameterNames.Count > 0;

    public bool ShellIsArray => false;

    public bool ShellIsPublic => true;

    public object CreateInstance(IReadOnlyList<object?> arguments) =>
        throw new InvalidOperationException($"Cannot create an instance of interface '{Name}'.");

    public InvocationResult InvokeStaticMethod(string methodName, IReadOnlyList<object?> arguments) =>
        throw new InvalidOperationException($"Interface '{Name}' has no static methods.");

    public bool TryGetStaticMember(string memberName, out object? value)
    {
        value = null;
        return false;
    }

    public bool TryGetMember(string name, out object? value, bool includeHidden = false)
    {
        foreach (var field in GetMembers(includeHidden))
        {
            if (string.Equals(field.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                value = field.Value;
                return true;
            }
        }

        value = null;
        return false;
    }

    public bool TrySetMember(string name, object? value) => false;

    public IReadOnlyList<KeyValuePair<string, object?>> GetMembers(bool includeHidden = false)
    {
        return
        [
            new KeyValuePair<string, object?>("Name", ShellTypeName),
            new KeyValuePair<string, object?>("FullName", ShellFullName),
            new KeyValuePair<string, object?>("IsInterface", true),
            new KeyValuePair<string, object?>("IsAbstract", true),
            new KeyValuePair<string, object?>("MethodCount", Methods.Count),
        ];
    }

    public IReadOnlyList<ShellMemberDescriptor> GetShellMembers(bool includeHidden = false) =>
        Array.Empty<ShellMemberDescriptor>();

    public IReadOnlyList<ShellMethodDescriptor> GetShellMethods(bool includeHidden = false)
    {
        return Methods
            .Select(m => new ShellMethodDescriptor(
                m.Name,
                ReturnTypeName: m.ReturnTypeName ?? "any",
                IsStatic: false,
                ParameterCount: m.Parameters.Count,
                Signature: $"{m.Name}({string.Join(", ", m.Parameters.Select(p => p.Name))})",
                IsHidden: false))
            .ToArray();
    }

    public IReadOnlyList<ShellConstructorDescriptor> GetShellConstructors() =>
        Array.Empty<ShellConstructorDescriptor>();

    /// <summary>
    /// Validates that a class definition fulfills all methods required by this interface.
    /// Returns a list of missing method names (empty if satisfied).
    /// </summary>
    public IReadOnlyList<string> GetMissingMethods(ToshClassDefinition classDefinition)
    {
        var missing = new List<string>();

        foreach (var required in Methods)
        {
            var found = classDefinition.Methods
                .Any(m => string.Equals(m.Name, required.Name, StringComparison.OrdinalIgnoreCase) &&
                          m.Parameters.Count == required.Parameters.Count);

            if (!found)
            {
                missing.Add(required.Name);
            }
        }

        return missing;
    }
}

public sealed record InterfaceMethodSignature(
    string Name,
    IReadOnlyList<FunctionParameterDefinition> Parameters,
    string? ReturnTypeName);
