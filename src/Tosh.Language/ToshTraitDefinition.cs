using Tosh.Core;
using Tosh.Language.Parsing;

namespace Tosh.Language;

public sealed class ToshTraitDefinition : IShellNamedType
{
    public ToshTraitDefinition(
        string name,
        IReadOnlyList<TraitMethodDefinition> methods,
        IReadOnlyList<TraitPropertyDefinition> properties,
        string sourceName,
        string sourceText,
        TextSpan span)
    {
        Name = name;
        Methods = methods;
        Properties = properties;
        SourceName = sourceName;
        SourceText = sourceText;
        Span = span;
    }

    public string Name { get; }

    public IReadOnlyList<TraitMethodDefinition> Methods { get; }

    public IReadOnlyList<TraitPropertyDefinition> Properties { get; }

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

    public bool ShellIsGenericType => false;

    public bool ShellIsArray => false;

    public bool ShellIsPublic => true;

    public object CreateInstance(IReadOnlyList<object?> arguments) =>
        throw new InvalidOperationException($"Cannot create an instance of trait '{Name}'.");

    public InvocationResult InvokeStaticMethod(string methodName, IReadOnlyList<object?> arguments) =>
        throw new InvalidOperationException($"Trait '{Name}' has no static methods.");

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
            new KeyValuePair<string, object?>("IsTrait", true),
            new KeyValuePair<string, object?>("IsAbstract", true),
            new KeyValuePair<string, object?>("MethodCount", Methods.Count),
            new KeyValuePair<string, object?>("PropertyCount", Properties.Count),
        ];
    }

    public IReadOnlyList<ShellMemberDescriptor> GetShellMembers(bool includeHidden = false)
    {
        return Properties
            .Select(p => new ShellMemberDescriptor(
                p.Name,
                Kind: "Property",
                TypeName: p.TypeName ?? "any",
                IsStatic: false,
                IsWritable: false,
                IsHidden: false))
            .ToArray();
    }

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
    /// Returns the names of required methods that the class does not implement.
    /// Methods with a default body are not required.
    /// </summary>
    public IReadOnlyList<string> GetMissingMethods(ToshClassDefinition classDefinition)
    {
        var missing = new List<string>();

        foreach (var required in Methods)
        {
            if (required.HasDefaultBody)
            {
                continue;
            }

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

    /// <summary>
    /// Returns required properties that the class does not declare.
    /// Properties with default values are not required.
    /// </summary>
    public IReadOnlyList<string> GetMissingProperties(ToshClassDefinition classDefinition)
    {
        var missing = new List<string>();

        foreach (var required in Properties)
        {
            if (required.DefaultValue is not null)
            {
                continue;
            }

            var found = classDefinition.Properties
                .Any(p => string.Equals(p.Name, required.Name, StringComparison.OrdinalIgnoreCase));

            if (!found)
            {
                missing.Add(required.Name);
            }
        }

        return missing;
    }
}

public sealed record TraitMethodDefinition(
    string Name,
    IReadOnlyList<FunctionParameterDefinition> Parameters,
    string? ReturnTypeName,
    BlockSyntax? DefaultBody,
    bool HasDefaultBody);

public sealed record TraitPropertyDefinition(
    string Name,
    string? TypeName,
    PipelineSyntax? DefaultValue);
