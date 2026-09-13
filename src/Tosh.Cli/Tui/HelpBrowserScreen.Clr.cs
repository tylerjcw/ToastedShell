using System.Reflection;
using System.Text;
using Tosh.Runtime;
using Tosh.Tui;

namespace Tosh.Cli.Tui;

/// <summary>
/// The help browser's CLR explorer: the F4 group that walks assemblies, namespaces,
/// types and members by reflection.
/// </summary>
/// <remarks>
/// Split out of <c>HelpBrowserScreen.cs</c>, which had grown to 3,289 lines. This is the
/// larger half by volume and the more self-contained one: it owns the six fields declared
/// here, and the rest of the screen reaches it at a handful of points — group selection,
/// entry activation, detail titles and the sidebar cache key.
/// </remarks>
internal sealed partial class HelpBrowserScreen
{
    private readonly HashSet<string> _expandedClrNamespaces = new(StringComparer.Ordinal);
    private string? _clrAssemblyScope;
    private string? _clrNamespaceScope;
    private string? _clrTypeScope;
    private bool _clrDeclaredOnly;
    private ClrBrowseIndex? _clrBrowseIndex;

    private IReadOnlyList<HelpDetailEntry> BuildClrTypeDetailEntries(HelpTopic topic, Type type, int width)
    {
        var lines = new List<HelpDetailEntry>
        {
            new($"{topic.Kind} • {topic.Category}", HelpDetailEntryKind.Meta),
            new(string.Empty, HelpDetailEntryKind.Blank),
        };

        lines.AddRange(TextDocumentFormatter.WrapParagraph(topic.Description, width)
            .Select(line => new HelpDetailEntry(line, HelpDetailEntryKind.Text)));

        lines.Add(new(string.Empty, HelpDetailEntryKind.Blank));
        lines.Add(new("Identity", HelpDetailEntryKind.SectionHeading));
        lines.Add(new($"  Path: CLR / .NET / {type.Namespace ?? "<global>"} / {ReflectionMetadataUtilities.GetDisplayName(type)}", HelpDetailEntryKind.Meta));
        lines.Add(new($"  Full Name: {ReflectionMetadataUtilities.GetDisplayName(type)}", HelpDetailEntryKind.Meta));
        lines.Add(new($"  Namespace: {type.Namespace ?? "<global>"}", HelpDetailEntryKind.Meta));
        lines.Add(new($"  Assembly: {type.Assembly.GetName().Name}", HelpDetailEntryKind.Meta));
        lines.Add(new($"  Base: {ReflectionMetadataUtilities.GetDisplayName(type.BaseType ?? typeof(object))}", HelpDetailEntryKind.Meta));
        lines.Add(new($"  Kind: {GetClrTypeKindLabel(type)}", HelpDetailEntryKind.Meta));

        var interfaces = type.GetInterfaces()
            .Select(ReflectionMetadataUtilities.GetDisplayName)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToArray();

        if (interfaces.Length > 0)
        {
            lines.Add(new($"  Interfaces: {interfaces[0]}", HelpDetailEntryKind.Meta));
            lines.AddRange(TextDocumentFormatter.WrapParagraph(
                    string.Join(", ", interfaces.Skip(1)),
                    width,
                    "    ",
                    "    ")
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Select(line => new HelpDetailEntry(line, HelpDetailEntryKind.Meta)));
        }

        AddClrGenericSection(lines, type, width);

        lines.Add(new(string.Empty, HelpDetailEntryKind.Blank));
        lines.Add(new("Usage", HelpDetailEntryKind.SectionHeading));
        lines.AddRange(TextDocumentFormatter.WrapParagraph(topic.Usage, width, "  ", "    ")
            .Select(line => new HelpDetailEntry(line, HelpDetailEntryKind.Example)));

        var constructors = ReflectionMetadataUtilities.GetConstructorDescriptors(type);
        AddSignatureSection(
            lines,
            "Constructors",
            constructors.Select(constructor => constructor.Signature).ToArray(),
            constructors.Count,
            width);

        var factoryMethods = type.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => !method.IsSpecialName && method.ReturnType == type)
            .OrderBy(method => method.Name, StringComparer.OrdinalIgnoreCase)
            .Select(ReflectionMetadataUtilities.FormatMethodSignature)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToArray();

        if (constructors.Count == 1 && constructors[0].ParameterCount == 0 && type.IsValueType && factoryMethods.Length > 0)
        {
            lines.Add(new(string.Empty, HelpDetailEntryKind.Blank));
            lines.Add(new("Factory Methods", HelpDetailEntryKind.SectionHeading));

            foreach (var factory in factoryMethods)
            {
                lines.AddRange(TextDocumentFormatter.WrapParagraph(factory, width, "  ", "    ")
                    .Select(line => new HelpDetailEntry(line, HelpDetailEntryKind.Text)));
            }
        }

        var memberRows = new List<string>();
        memberRows.AddRange(type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .Where(property => property.GetIndexParameters().Length == 0)
            .OrderBy(property => property.Name, StringComparer.OrdinalIgnoreCase)
            .Select(property =>
            {
                var staticPrefix = ((property.GetMethod ?? property.SetMethod)?.IsStatic ?? false) ? "static " : string.Empty;
                var writableSuffix = property.CanWrite ? string.Empty : " readonly";
                return $"{staticPrefix}property {property.Name}: {ReflectionMetadataUtilities.GetDisplayName(property.PropertyType)}{writableSuffix}";
            }));
        memberRows.AddRange(type.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .OrderBy(field => field.Name, StringComparer.OrdinalIgnoreCase)
            .Select(field =>
            {
                var staticPrefix = field.IsStatic ? "static " : string.Empty;
                var writableSuffix = field.IsInitOnly || field.IsLiteral ? " readonly" : string.Empty;
                return $"{staticPrefix}field {field.Name}: {ReflectionMetadataUtilities.GetDisplayName(field.FieldType)}{writableSuffix}";
            }));
        AddSignatureSection(lines, "Properties & Fields", memberRows.ToArray(), memberRows.Count, width, take: 12);

        var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .Where(method => !method.IsSpecialName)
            .OrderBy(method => method.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(method => method.GetParameters().Length)
            .Select(ReflectionMetadataUtilities.FormatMethodSignature)
            .ToArray();
        AddSignatureSection(lines, "Methods", methods, methods.Length, width, take: 12);

        lines.Add(new(string.Empty, HelpDetailEntryKind.Blank));
        lines.Add(new("Shell Helpers", HelpDetailEntryKind.SectionHeading));
        foreach (var helper in new[]
                 {
                     $"describe-type {ReflectionMetadataUtilities.GetDisplayName(type)}",
                     $"constructors {ReflectionMetadataUtilities.GetDisplayName(type)}",
                     $"members {ReflectionMetadataUtilities.GetDisplayName(type)}",
                     $"methods {ReflectionMetadataUtilities.GetDisplayName(type)}",
                 })
        {
            lines.Add(new($"  {helper}", HelpDetailEntryKind.Example));
        }

        var examples = topic.ExampleItems?.Count > 0
            ? topic.ExampleItems
            : topic.Examples.Select(example => new HelpExample(example)).ToArray();

        if (examples.Count > 0)
        {
            lines.Add(new(string.Empty, HelpDetailEntryKind.Blank));
            lines.Add(new("Examples", HelpDetailEntryKind.SectionHeading));

            foreach (var example in examples)
            {
                if (!string.IsNullOrWhiteSpace(example.Title))
                {
                    lines.Add(new($"  {example.Title}", HelpDetailEntryKind.Meta));
                }

                lines.AddRange(TextDocumentFormatter.WrapParagraph(example.Code, width, "    ", "      ")
                    .Select(line => new HelpDetailEntry(line, HelpDetailEntryKind.Example)));

                if (!string.IsNullOrWhiteSpace(example.Description))
                {
                    lines.AddRange(TextDocumentFormatter.WrapParagraph(example.Description!, width, "      ", "      ")
                        .Select(line => new HelpDetailEntry(line, HelpDetailEntryKind.Text)));
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(topic.Notes))
        {
            lines.Add(new(string.Empty, HelpDetailEntryKind.Blank));
            lines.Add(new("Notes", HelpDetailEntryKind.SectionHeading));
            lines.AddRange(TextDocumentFormatter.WrapParagraph(topic.Notes!, width, "  ", "    ")
                .Select(line => new HelpDetailEntry(line, HelpDetailEntryKind.Text)));
        }

        if (topic.Related.Count > 0)
        {
            lines.Add(new(string.Empty, HelpDetailEntryKind.Blank));
            lines.Add(new("Related", HelpDetailEntryKind.SectionHeading));

            for (var index = 0; index < topic.Related.Count; index += 1)
            {
                lines.Add(new($"  [{index + 1}] {topic.Related[index]}", HelpDetailEntryKind.RelatedTopic, index + 1));
            }
        }

        return lines;
    }

    private IReadOnlyList<HelpDetailEntry> BuildClrAssemblyDetailEntries(string assemblyName, int width)
    {
        var index = EnsureClrBrowseIndex();
        if (!index.ByName.TryGetValue(assemblyName, out var assembly))
        {
            return [new HelpDetailEntry("The selected assembly is no longer available.", HelpDetailEntryKind.Meta)];
        }

        var lines = new List<HelpDetailEntry>
        {
            new("CLR Assembly", HelpDetailEntryKind.Meta),
            new(string.Empty, HelpDetailEntryKind.Blank),
            new($"Path: CLR / .NET / {assembly.Name}", HelpDetailEntryKind.Meta),
            new($"Name: {assembly.Name}", HelpDetailEntryKind.Meta),
            new($"Full Name: {assembly.FullName}", HelpDetailEntryKind.Text),
            new($"Types: {assembly.Types.Count}  Namespaces: {assembly.Namespaces.Count}", HelpDetailEntryKind.Meta),
        };

        lines.Add(new(string.Empty, HelpDetailEntryKind.Blank));
        lines.AddRange(TextDocumentFormatter.WrapParagraph("Press Enter to drill into this assembly's namespaces and visible types.", width)
            .Select(line => new HelpDetailEntry(line, HelpDetailEntryKind.Text)));

        if (assembly.Namespaces.Count > 0)
        {
            lines.Add(new(string.Empty, HelpDetailEntryKind.Blank));
            lines.Add(new("Namespaces", HelpDetailEntryKind.SectionHeading));
            foreach (var ns in assembly.Namespaces.Take(10))
            {
                lines.Add(new($"  {ns}", HelpDetailEntryKind.Text));
            }

            if (assembly.Namespaces.Count > 10)
            {
                lines.Add(new($"  … {assembly.Namespaces.Count - 10} more", HelpDetailEntryKind.Meta));
            }
        }

        if (assembly.Types.Count > 0)
        {
            lines.Add(new(string.Empty, HelpDetailEntryKind.Blank));
            lines.Add(new("Sample Types", HelpDetailEntryKind.SectionHeading));
            foreach (var type in assembly.Types.Take(10))
            {
                lines.Add(new($"  {ReflectionMetadataUtilities.GetDisplayName(type)}", HelpDetailEntryKind.Text));
            }

            if (assembly.Types.Count > 10)
            {
                lines.Add(new($"  … {assembly.Types.Count - 10} more", HelpDetailEntryKind.Meta));
            }
        }

        return lines;
    }

    private IReadOnlyList<HelpDetailEntry> BuildClrNamespaceDetailEntries(string namespaceName, int width)
    {
        var types = GetClrTypesForCurrentScope()
            .Where(type => string.Equals(type.Namespace ?? string.Empty, namespaceName, StringComparison.Ordinal))
            .OrderBy(type => type.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var hasDescendants = ClrNamespaceHasChildren(namespaceName);

        var lines = new List<HelpDetailEntry>
        {
            new("CLR Namespace", HelpDetailEntryKind.Meta),
            new(string.Empty, HelpDetailEntryKind.Blank),
            new($"Path: CLR / .NET / {namespaceName}", HelpDetailEntryKind.Meta),
            new($"Namespace: {namespaceName}", HelpDetailEntryKind.Meta),
            new("Assembly Scope: merged tree across all discoverable CLR assemblies", HelpDetailEntryKind.Meta),
            new($"Assemblies Contributing: {CountClrAssembliesForNamespace(namespaceName)}", HelpDetailEntryKind.Meta),
            new($"Direct Types: {types.Length}", HelpDetailEntryKind.Meta),
            new($"Subtree Types: {CountClrSubtreeTypes(namespaceName)}", HelpDetailEntryKind.Meta),
            new($"Child Namespaces: {CountClrChildNamespaces(namespaceName)}  Descendants: {TuiRenderHelpers.FormatBoolean(hasDescendants)}", HelpDetailEntryKind.Meta),
            new(string.Empty, HelpDetailEntryKind.Blank),
        };

        var guidance = ClrNamespaceHasTreeChildren(namespaceName)
            ? "Press Enter to collapse or expand this namespace branch. Press RightArrow or Tab to move into the detail pane."
            : "This namespace has no visible child namespaces or direct types in the current view.";
        lines.AddRange(TextDocumentFormatter.WrapParagraph(guidance, width)
            .Select(line => new HelpDetailEntry(line, HelpDetailEntryKind.Text)));

        if (types.Length > 0)
        {
            lines.Add(new(string.Empty, HelpDetailEntryKind.Blank));
            lines.Add(new("Sample Types", HelpDetailEntryKind.SectionHeading));

            foreach (var type in types.Take(8))
            {
                lines.Add(new($"  {ReflectionMetadataUtilities.GetDisplayName(type)}", HelpDetailEntryKind.Text));
            }

            if (types.Length > 8)
            {
                lines.Add(new($"  … {types.Length - 8} more", HelpDetailEntryKind.Meta));
            }
        }

        return lines;
    }

    private IReadOnlyList<HelpDetailEntry> BuildClrScopeDetailEntries(int width)
    {
        var lines = new List<HelpDetailEntry>
        {
            new("CLR Browser", HelpDetailEntryKind.Meta),
            new(string.Empty, HelpDetailEntryKind.Blank),
        };

        var text = _clrNamespaceScope is not null
            ? $"You are browsing CLR type scope under namespace '{_clrNamespaceScope}'. Use LeftArrow or Backspace to return to the unified namespace tree."
            : _clrAssemblyScope is not null
                ? $"You are browsing assembly '{_clrAssemblyScope}'. Press Enter or LeftArrow to go up to the CLR root."
                : "Browse the unified CLR namespace tree, expand down to the type you want, and inspect assembly information in the detail pane.";

        lines.AddRange(TextDocumentFormatter.WrapParagraph(text, width)
            .Select(line => new HelpDetailEntry(line, HelpDetailEntryKind.Text)));
        return lines;
    }

    private IReadOnlyList<HelpDetailEntry> BuildClrTypeScopeDetailEntries(int width)
    {
        if (_clrTypeScope is null)
        {
            return [new HelpDetailEntry("Select a CLR type to browse its API surface.", HelpDetailEntryKind.Meta)];
        }

        var topic = HelpCatalog.ResolveTopic(_runtime, _clrTypeScope);
        if (topic is not null && TryResolveClrType(topic, out var clrType))
        {
            return BuildClrTypeDetailEntries(topic, clrType, width);
        }

        return [new HelpDetailEntry("The selected CLR type is no longer available.", HelpDetailEntryKind.Meta)];
    }

    private IReadOnlyList<HelpDetailEntry> BuildClrFilterDetailEntries(int width)
    {
        var lines = new List<HelpDetailEntry>
        {
            new("CLR Browser Options", HelpDetailEntryKind.Meta),
            new(string.Empty, HelpDetailEntryKind.Blank),
            new($"Declared Only: {(_clrDeclaredOnly ? "on" : "off")}", HelpDetailEntryKind.Meta),
            new(string.Empty, HelpDetailEntryKind.Blank),
        };

        lines.AddRange(TextDocumentFormatter.WrapParagraph(
                _clrDeclaredOnly
                    ? "Only members declared directly on the current CLR type are shown in the sidebar. Press Enter to include inherited members again."
                    : "The sidebar currently includes inherited public members. Press Enter to switch to declared-only browsing for the current CLR type.",
                width)
            .Select(line => new HelpDetailEntry(line, HelpDetailEntryKind.Text)));
        return lines;
    }

    private IReadOnlyList<HelpDetailEntry> BuildClrConstructorDetailEntries(string signature, int width)
    {
        var type = ResolveCurrentClrTypeScope();
        if (type is null)
        {
            return [new HelpDetailEntry("The selected constructor is no longer available.", HelpDetailEntryKind.Meta)];
        }

        var constructor = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .FirstOrDefault(candidate =>
                string.Equals(
                    ReflectionMetadataUtilities.FormatConstructorSignature(candidate),
                    signature,
                    StringComparison.OrdinalIgnoreCase));
        var isImplicitValueTypeDefault = constructor is null &&
                                         type.IsValueType &&
                                         !type.IsEnum &&
                                         string.Equals(signature, $"{ReflectionMetadataUtilities.GetDisplayName(type)}()", StringComparison.OrdinalIgnoreCase);

        var lines = new List<HelpDetailEntry>
        {
            new("Constructor", HelpDetailEntryKind.Meta),
            new(string.Empty, HelpDetailEntryKind.Blank),
            new($"Path: CLR / .NET / {type.Namespace ?? "<global>"} / {ReflectionMetadataUtilities.GetDisplayName(type)} / .ctor", HelpDetailEntryKind.Meta),
            new($"Type: {ReflectionMetadataUtilities.GetDisplayName(type)}", HelpDetailEntryKind.Meta),
            new($"Assembly: {type.Assembly.GetName().Name}", HelpDetailEntryKind.Meta),
            new($"Signature: {signature}", HelpDetailEntryKind.Example),
        };

        if (constructor is not null)
        {
            lines.Add(new($"Parameter Count: {constructor.GetParameters().Length}", HelpDetailEntryKind.Meta));
            lines.Add(new($"Static: {constructor.IsStatic}", HelpDetailEntryKind.Meta));
            AddParameterSection(lines, constructor.GetParameters(), width);

            lines.Add(new(string.Empty, HelpDetailEntryKind.Blank));
            lines.Add(new("Example Invocation", HelpDetailEntryKind.SectionHeading));
            lines.Add(new($"  new {BuildConstructorInvocationExample(constructor)}", HelpDetailEntryKind.Example));
        }
        else if (isImplicitValueTypeDefault)
        {
            lines.Add(new("Parameter Count: 0", HelpDetailEntryKind.Meta));
            lines.Add(new("Static: no", HelpDetailEntryKind.Meta));
            lines.Add(new("This is the implicit default constructor available on CLR value types.", HelpDetailEntryKind.Text));
            lines.Add(new(string.Empty, HelpDetailEntryKind.Blank));
            lines.Add(new("Example Invocation", HelpDetailEntryKind.SectionHeading));
            lines.Add(new($"  new {ReflectionMetadataUtilities.GetDisplayName(type)}()", HelpDetailEntryKind.Example));
        }

        lines.Add(new(string.Empty, HelpDetailEntryKind.Blank));
        lines.Add(new("Shell Helpers", HelpDetailEntryKind.SectionHeading));
        foreach (var helper in new[]
                 {
                     $"constructors {ReflectionMetadataUtilities.GetDisplayName(type)}",
                     $"describe-type {ReflectionMetadataUtilities.GetDisplayName(type)}",
                 })
        {
            lines.Add(new($"  {helper}", HelpDetailEntryKind.Example));
        }

        lines.Add(new(string.Empty, HelpDetailEntryKind.Blank));
        lines.AddRange(TextDocumentFormatter.WrapParagraph("Use RightArrow or Tab to move into the detail pane, then scroll the full CLR type page for the surrounding API context.", width)
            .Select(line => new HelpDetailEntry(line, HelpDetailEntryKind.Text)));
        return lines;
    }

    private IReadOnlyList<HelpDetailEntry> BuildClrMemberDetailEntries(string value, int width)
    {
        var type = ResolveCurrentClrTypeScope();
        if (type is null)
        {
            return [new HelpDetailEntry("The selected member is no longer available.", HelpDetailEntryKind.Meta)];
        }

        var parts = value.Split('|', 3);
        var memberKind = parts.Length > 0 ? parts[0] : "member";
        var declaringAssemblyQualifiedName = parts.Length > 1 ? parts[1] : null;
        var memberName = parts.Length > 2 ? parts[2] : value;

        var declaringType = !string.IsNullOrWhiteSpace(declaringAssemblyQualifiedName)
            ? Type.GetType(declaringAssemblyQualifiedName!, throwOnError: false)
            : type;
        var flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        var property = memberKind == "property" && declaringType is not null
            ? declaringType.GetProperty(memberName, flags)
            : null;
        var field = memberKind == "field" && declaringType is not null
            ? declaringType.GetField(memberName, flags)
            : null;

        var lines = new List<HelpDetailEntry>
        {
            new(memberKind == "property" ? "Property" : "Field", HelpDetailEntryKind.Meta),
            new(string.Empty, HelpDetailEntryKind.Blank),
            new($"Path: CLR / .NET / {type.Namespace ?? "<global>"} / {ReflectionMetadataUtilities.GetDisplayName(type)} / {memberName}", HelpDetailEntryKind.Meta),
            new($"Type: {ReflectionMetadataUtilities.GetDisplayName(type)}", HelpDetailEntryKind.Meta),
            new($"Scope Assembly: {type.Assembly.GetName().Name}", HelpDetailEntryKind.Meta),
            new($"Name: {memberName}", HelpDetailEntryKind.Meta),
        };

        if (property is not null)
        {
            lines.Add(new($"Member Type: {ReflectionMetadataUtilities.GetDisplayName(property.PropertyType)}", HelpDetailEntryKind.Meta));
            lines.Add(new($"Declared On: {ReflectionMetadataUtilities.GetDisplayName(property.DeclaringType ?? type)}", HelpDetailEntryKind.Meta));
            lines.Add(new($"Assembly: {(property.DeclaringType ?? type).Assembly.GetName().Name}", HelpDetailEntryKind.Meta));
            lines.Add(new($"Static: {((property.GetMethod ?? property.SetMethod)?.IsStatic ?? false)}  Writable: {property.CanWrite}", HelpDetailEntryKind.Meta));
        }
        else if (field is not null)
        {
            lines.Add(new($"Member Type: {ReflectionMetadataUtilities.GetDisplayName(field.FieldType)}", HelpDetailEntryKind.Meta));
            lines.Add(new($"Declared On: {ReflectionMetadataUtilities.GetDisplayName(field.DeclaringType ?? type)}", HelpDetailEntryKind.Meta));
            lines.Add(new($"Assembly: {(field.DeclaringType ?? type).Assembly.GetName().Name}", HelpDetailEntryKind.Meta));
            lines.Add(new($"Static: {field.IsStatic}  Writable: {!(field.IsInitOnly || field.IsLiteral)}", HelpDetailEntryKind.Meta));
        }

        lines.Add(new(string.Empty, HelpDetailEntryKind.Blank));
        lines.Add(new("Shell Helpers", HelpDetailEntryKind.SectionHeading));
        foreach (var helper in new[]
                 {
                     $"members {ReflectionMetadataUtilities.GetDisplayName(type)}",
                     $"describe-type {ReflectionMetadataUtilities.GetDisplayName(type)}",
                 })
        {
            lines.Add(new($"  {helper}", HelpDetailEntryKind.Example));
        }

        lines.Add(new(string.Empty, HelpDetailEntryKind.Blank));
        lines.AddRange(TextDocumentFormatter.WrapParagraph("This member is part of the current CLR type scope. Use LeftArrow or Backspace to go back up to the type-level browser.", width)
            .Select(line => new HelpDetailEntry(line, HelpDetailEntryKind.Text)));
        return lines;
    }

    private IReadOnlyList<HelpDetailEntry> BuildClrMethodDetailEntries(string methodName, int width)
    {
        var type = ResolveCurrentClrTypeScope();
        if (type is null)
        {
            return [new HelpDetailEntry("The selected method is no longer available.", HelpDetailEntryKind.Meta)];
        }

        var methods = type.GetMethods(GetClrMemberBindingFlags())
            .Where(candidate => !candidate.IsSpecialName)
            .Where(candidate => string.Equals(candidate.Name, methodName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(candidate => candidate.GetParameters().Length)
            .ThenBy(candidate => ReflectionMetadataUtilities.FormatMethodSignature(candidate), StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var lines = new List<HelpDetailEntry>
        {
            new("Method Overloads", HelpDetailEntryKind.Meta),
            new(string.Empty, HelpDetailEntryKind.Blank),
            new($"Path: CLR / .NET / {type.Namespace ?? "<global>"} / {ReflectionMetadataUtilities.GetDisplayName(type)} / {methodName}", HelpDetailEntryKind.Meta),
            new($"Type: {ReflectionMetadataUtilities.GetDisplayName(type)}", HelpDetailEntryKind.Meta),
            new($"Assembly: {type.Assembly.GetName().Name}", HelpDetailEntryKind.Meta),
            new($"Name: {methodName}", HelpDetailEntryKind.Meta),
            new($"Overloads: {methods.Length}  Declared Only: {(_clrDeclaredOnly ? "yes" : "no")}", HelpDetailEntryKind.Meta),
        };

        lines.Add(new(string.Empty, HelpDetailEntryKind.Blank));
        lines.Add(new("Overloads", HelpDetailEntryKind.SectionHeading));

        if (methods.Length == 0)
        {
            lines.Add(new("  <none>", HelpDetailEntryKind.Meta));
        }
        else
        {
            foreach (var method in methods.Take(12))
            {
                var declaringType = ReflectionMetadataUtilities.GetDisplayName(method.DeclaringType ?? type);
                var declaringAssembly = (method.DeclaringType ?? type).Assembly.GetName().Name;
                lines.Add(new($"  {ReflectionMetadataUtilities.FormatMethodSignature(method)}", HelpDetailEntryKind.Example));
                lines.Add(new($"    declared on {declaringType} | assembly: {declaringAssembly} | static: {method.IsStatic}", HelpDetailEntryKind.Meta));

                var parameters = method.GetParameters();
                if (parameters.Length > 0)
                {
                    foreach (var parameterLine in BuildParameterSummaryLines(parameters, width, "      "))
                    {
                        lines.Add(new(parameterLine, HelpDetailEntryKind.Text));
                    }
                }
            }

            if (methods.Length > 12)
            {
                lines.Add(new($"  … {methods.Length - 12} more", HelpDetailEntryKind.Meta));
            }
        }

        lines.Add(new(string.Empty, HelpDetailEntryKind.Blank));
        lines.Add(new("Shell Helpers", HelpDetailEntryKind.SectionHeading));
        foreach (var helper in new[]
                 {
                     $"methods {ReflectionMetadataUtilities.GetDisplayName(type)}",
                     $"describe-type {ReflectionMetadataUtilities.GetDisplayName(type)}",
                 })
        {
            lines.Add(new($"  {helper}", HelpDetailEntryKind.Example));
        }

        lines.Add(new(string.Empty, HelpDetailEntryKind.Blank));
        lines.AddRange(TextDocumentFormatter.WrapParagraph("This method is part of the current CLR type scope. Use LeftArrow or Backspace to go back up to the type-level browser.", width)
            .Select(line => new HelpDetailEntry(line, HelpDetailEntryKind.Text)));
        return lines;
    }

    private Type? ResolveCurrentClrTypeScope()
    {
        return string.IsNullOrWhiteSpace(_clrTypeScope)
            ? null
            : ResolveClrTypeByDisplayName(_clrTypeScope!);
    }

    private IReadOnlyList<HelpBrowserListEntry> BuildClrSidebarEntriesCore()
    {
        var entries = new List<HelpBrowserListEntry>();
        var index = EnsureClrBrowseIndex();

        if (_clrTypeScope is not null)
        {
            var type = ResolveClrTypeByDisplayName(_clrTypeScope);
            entries.Add(HelpBrowserListEntry.Up(".. CLR / .NET", "clr:up:type"));

            if (type is not null)
            {
                AddClrTypeNavigationEntries(entries, type);
                AddClrTypeOptionEntries(entries);
                AddClrConstructorEntries(entries, type);
                AddClrMemberEntries(entries, type);
                AddClrMethodEntries(entries, type);
            }

            return entries;
        }

        if (_clrNamespaceScope is not null)
        {
            entries.Add(HelpBrowserListEntry.Up(".. CLR / .NET", "clr:up:namespace"));
            AddClrTypeSection(entries, $"clr:namespace:{_clrNamespaceScope}:types", GetClrTypesForCurrentScope()
                .Where(type => string.Equals(type.Namespace ?? string.Empty, _clrNamespaceScope, StringComparison.Ordinal))
                .ToArray(), compactLabels: true);
            return entries;
        }

        if (_clrAssemblyScope is not null)
        {
            entries.Add(HelpBrowserListEntry.Up(".. CLR / .NET", "clr:up:assembly"));

            if (index.ByName.TryGetValue(_clrAssemblyScope, out var assembly))
            {
                AddClrNamespaceSection(entries, "Namespaces", "clr:assembly:namespaces", assembly.Namespaces);

                var assemblyTypes = FilterTypesForQuery(assembly.Types);
                var rootTypes = assemblyTypes
                    .Where(type => string.IsNullOrWhiteSpace(type.Namespace))
                    .ToArray();

                if (_query.Length > 0)
                {
                    AddClrTypeSection(entries, "clr:assembly:types", assemblyTypes);
                }
                else if (rootTypes.Length > 0)
                {
                    AddClrTypeSection(entries, "clr:assembly:types", rootTypes);
                }
            }

            return entries;
        }

        AddClrNamespaceSection(
            entries,
            "Namespaces",
            "clr:namespaces",
            index.AllTypes
                .Select(type => type.Namespace)
                .Where(ns => !string.IsNullOrWhiteSpace(ns))
                .Cast<string>()
                .Distinct(StringComparer.Ordinal)
                .ToArray());

        var globalTypes = index.AllTypes
            .Where(type => string.IsNullOrWhiteSpace(type.Namespace))
            .ToArray();
        AddClrTypeSection(entries, "clr:global-types", globalTypes, "Global Types", compactLabels: true);

        if (_query.Length > 0)
        {
            var matchingTypes = FilterTypesForQuery(index.AllTypes).Take(60).ToArray();
            AddClrTypeSection(entries, "clr:types", matchingTypes, "Matching Types");
        }

        var clrHelpTopics = FilterTopics()
            .Where(summary => DetermineGroup(summary) == HelpBrowserGroup.Clr)
            .OrderBy(summary => summary.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (clrHelpTopics.Length > 0)
        {
            var collapsed = _collapsedSections.Contains("clr:commands");
            entries.Add(HelpBrowserListEntry.SectionHeader("Commands", "clr:commands", collapsed));
            if (!collapsed)
            {
                foreach (var summary in clrHelpTopics)
                {
                    entries.Add(HelpBrowserListEntry.Topic(summary));
                }
            }
        }

        return entries;
    }

    private void AddClrTypeNavigationEntries(List<HelpBrowserListEntry> entries, Type type)
    {
        var sectionKey = $"clr:type:{_clrTypeScope}:navigation";
        var collapsed = _collapsedSections.Contains(sectionKey);
        entries.Add(HelpBrowserListEntry.SectionHeader("Navigation", sectionKey, collapsed));
        if (collapsed)
        {
            return;
        }

        if (type.BaseType is not null)
        {
            var baseDisplayName = ReflectionMetadataUtilities.GetDisplayName(type.BaseType);
            if (MatchesQuery(baseDisplayName, "base"))
            {
                entries.Add(HelpBrowserListEntry.ClrTypeLink($"base: {baseDisplayName}", type.BaseType));
            }
        }

        foreach (var iface in type.GetInterfaces()
                     .OrderBy(iface => ReflectionMetadataUtilities.GetDisplayName(iface), StringComparer.OrdinalIgnoreCase))
        {
            var displayName = ReflectionMetadataUtilities.GetDisplayName(iface);
            if (MatchesQuery(displayName, "interface"))
            {
                entries.Add(HelpBrowserListEntry.ClrTypeLink($"interface: {displayName}", iface));
            }
        }
    }

    private void AddClrTypeOptionEntries(List<HelpBrowserListEntry> entries)
    {
        var sectionKey = $"clr:type:{_clrTypeScope}:options";
        var collapsed = _collapsedSections.Contains(sectionKey);
        entries.Add(HelpBrowserListEntry.SectionHeader("View Options", sectionKey, collapsed));
        if (collapsed)
        {
            return;
        }

        entries.Add(HelpBrowserListEntry.ClrFilterToggle(_clrDeclaredOnly));
    }

    private void AddClrAssemblySection(
        List<HelpBrowserListEntry> entries,
        string label,
        string sectionKey,
        IReadOnlyList<ClrAssemblyBrowseInfo> assemblies)
    {
        if (assemblies.Count == 0)
        {
            return;
        }

        var collapsed = _collapsedSections.Contains(sectionKey);
        entries.Add(HelpBrowserListEntry.SectionHeader(label, sectionKey, collapsed));
        if (collapsed)
        {
            return;
        }

        foreach (var assembly in assemblies)
        {
            entries.Add(HelpBrowserListEntry.ClrAssembly(assembly));
        }
    }

    private void AddClrNamespaceSection(
        List<HelpBrowserListEntry> entries,
        string label,
        string sectionKey,
        IReadOnlyList<string> namespaces)
    {
        var filtered = FilterClrNamespacesForTree(namespaces);

        if (filtered.Length == 0)
        {
            return;
        }

        var collapsed = _collapsedSections.Contains(sectionKey);
        entries.Add(HelpBrowserListEntry.SectionHeader(label, sectionKey, collapsed));
        if (collapsed)
        {
            return;
        }

        var typesByNamespace = GetClrTypesForCurrentScope()
            .Where(type => !string.IsNullOrWhiteSpace(type.Namespace))
            .GroupBy(type => type.Namespace!, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderBy(type => BuildCompactClrTypeLabel(type), StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                StringComparer.Ordinal);

        var childNamespaces = filtered
            .GroupBy(candidate => GetClrParentNamespace(candidate) ?? string.Empty, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(candidate => candidate, StringComparer.OrdinalIgnoreCase).ToArray(),
                StringComparer.Ordinal);

        if (!childNamespaces.TryGetValue(string.Empty, out var roots))
        {
            return;
        }

        for (var index = 0; index < roots.Length; index += 1)
        {
            AddClrNamespaceTreeNode(
                entries,
                roots[index],
                prefix: string.Empty,
                isLast: index == roots.Length - 1,
                childNamespaces,
                typesByNamespace);
        }
    }

    private void AddClrNamespaceTreeNode(
        List<HelpBrowserListEntry> entries,
        string namespaceName,
        string prefix,
        bool isLast,
        IReadOnlyDictionary<string, string[]> childNamespaces,
        IReadOnlyDictionary<string, Type[]> typesByNamespace)
    {
        var sectionKey = BuildClrNamespaceTreeSectionKey(namespaceName);
        var childNamespaceList = childNamespaces.TryGetValue(namespaceName, out var childNamespaceCandidates)
            ? childNamespaceCandidates
            : Array.Empty<string>();
        var directTypes = typesByNamespace.TryGetValue(namespaceName, out var namespaceTypes)
            ? namespaceTypes
            : Array.Empty<Type>();
        var visibleTypes = FilterClrTypesForTree(directTypes);
        var hasTreeChildren = childNamespaceList.Length > 0 || visibleTypes.Length > 0;
        var isExpanded = IsClrNamespaceExpanded(namespaceName);
        var connector = isLast ? "└" : "├";
        var marker = hasTreeChildren ? (isExpanded ? "▾ " : "▸ ") : "─ ";
        var leafName = namespaceName.Split('.').Last();
        var treeStyle = _runtime.Config.Theme.Tui.TreeStyle;
        var label = treeStyle == ToshTuiTreeStyle.Clean && prefix.Length == 0
            ? $"{marker}{leafName} [{directTypes.Length}T/{childNamespaceList.Length}N]"
            : $"{prefix}{connector}{marker}{leafName} [{directTypes.Length}T/{childNamespaceList.Length}N]";

        entries.Add(HelpBrowserListEntry.ClrNamespaceTree(namespaceName, label, sectionKey, collapsed: !isExpanded));

        if (!hasTreeChildren || !isExpanded)
        {
            return;
        }

        var childPrefix = treeStyle == ToshTuiTreeStyle.Clean && prefix.Length == 0
            ? "  "
            : prefix + (isLast ? "  " : "│ ");

        for (var index = 0; index < childNamespaceList.Length; index += 1)
        {
            var isLastNamespace = index == childNamespaceList.Length - 1 && visibleTypes.Length == 0;
            AddClrNamespaceTreeNode(
                entries,
                childNamespaceList[index],
                childPrefix,
                isLastNamespace,
                childNamespaces,
                typesByNamespace);
        }

        for (var index = 0; index < visibleTypes.Length; index += 1)
        {
            var type = visibleTypes[index];
            var typeConnector = index == visibleTypes.Length - 1 ? "└" : "├";
            var typeLabel = $"{childPrefix}{typeConnector}─ {BuildCompactClrTypeLabel(type)}";
            entries.Add(HelpBrowserListEntry.ClrType(type, typeLabel));
        }
    }

    private void AddClrTypeSection(
        List<HelpBrowserListEntry> entries,
        string sectionKey,
        IReadOnlyList<Type> types,
        string label = "Types",
        bool compactLabels = false)
    {
        if (types.Count == 0)
        {
            return;
        }

        var collapsed = _collapsedSections.Contains(sectionKey);
        entries.Add(HelpBrowserListEntry.SectionHeader(label, sectionKey, collapsed));
        if (collapsed)
        {
            return;
        }

        foreach (var type in types
                     .OrderBy(type => ReflectionMetadataUtilities.GetDisplayName(type), StringComparer.OrdinalIgnoreCase)
                     .Take(120))
        {
            entries.Add(compactLabels
                ? HelpBrowserListEntry.ClrType(type, BuildCompactClrTypeLabel(type))
                : HelpBrowserListEntry.ClrType(type));
        }
    }

    private void AddClrConstructorEntries(List<HelpBrowserListEntry> entries, Type type)
    {
        var constructors = ReflectionMetadataUtilities.GetConstructorDescriptors(type);

        var sectionKey = $"clr:type:{_clrTypeScope}:constructors";
        var collapsed = _collapsedSections.Contains(sectionKey);
        entries.Add(HelpBrowserListEntry.SectionHeader("Constructors", sectionKey, collapsed));
        if (collapsed)
        {
            return;
        }

        foreach (var constructor in constructors)
        {
            entries.Add(HelpBrowserListEntry.ClrConstructor(constructor.Signature));
        }
    }

    private void AddClrMemberEntries(List<HelpBrowserListEntry> entries, Type type)
    {
        var flags = GetClrMemberBindingFlags();
        var members = new List<HelpBrowserListEntry>();
        members.AddRange(type.GetProperties(flags)
            .Where(property => property.GetIndexParameters().Length == 0)
            .OrderBy(property => property.Name, StringComparer.OrdinalIgnoreCase)
            .Select(property => HelpBrowserListEntry.ClrMember(
                $"property|{property.DeclaringType?.AssemblyQualifiedName}|{property.Name}",
                BuildClrMemberLabel(type, property.Name, ReflectionMetadataUtilities.GetDisplayName(property.PropertyType), property.DeclaringType))));
        members.AddRange(type.GetFields(flags)
            .OrderBy(field => field.Name, StringComparer.OrdinalIgnoreCase)
            .Select(field => HelpBrowserListEntry.ClrMember(
                $"field|{field.DeclaringType?.AssemblyQualifiedName}|{field.Name}",
                BuildClrMemberLabel(type, field.Name, ReflectionMetadataUtilities.GetDisplayName(field.FieldType), field.DeclaringType))));

        var sectionKey = $"clr:type:{_clrTypeScope}:members";
        var collapsed = _collapsedSections.Contains(sectionKey);
        entries.Add(HelpBrowserListEntry.SectionHeader("Properties & Fields", sectionKey, collapsed));
        if (collapsed)
        {
            return;
        }

        entries.AddRange(members);
    }

    private void AddClrMethodEntries(List<HelpBrowserListEntry> entries, Type type)
    {
        var methods = type.GetMethods(GetClrMemberBindingFlags())
            .Where(method => !method.IsSpecialName)
            .OrderBy(method => method.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(method => method.GetParameters().Length)
            .ToArray();

        var sectionKey = $"clr:type:{_clrTypeScope}:methods";
        var collapsed = _collapsedSections.Contains(sectionKey);
        entries.Add(HelpBrowserListEntry.SectionHeader("Methods", sectionKey, collapsed));
        if (collapsed)
        {
            return;
        }

        foreach (var group in methods
                     .GroupBy(method => method.Name, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            var label = BuildClrMethodGroupLabel(type, group);
            entries.Add(HelpBrowserListEntry.ClrMethodGroup(group.Key, label));
        }
    }

    private void ExpandClrNamespace(string namespaceName)
    {
        foreach (var ancestor in GetClrNamespaceAncestors(namespaceName).Reverse())
        {
            _expandedClrNamespaces.Add(ancestor);
        }

        _expandedClrNamespaces.Add(namespaceName);
        InvalidateSidebarCache();
        InvalidateDetailCache();
        ApplyFilter(_sidebar.Scroll.PageSize > 0 ? _sidebar.Scroll.PageSize : 10);
    }

    private void ToggleClrNamespaceExpansion(string namespaceName)
    {
        if (!_expandedClrNamespaces.Add(namespaceName))
        {
            _expandedClrNamespaces.Remove(namespaceName);
        }

        InvalidateSidebarCache();
        InvalidateDetailCache();
        ApplyFilter(_sidebar.Scroll.PageSize > 0 ? _sidebar.Scroll.PageSize : 10);
    }

    private void OpenClrTypeScope(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        _activeGroup = HelpBrowserGroup.Clr;
        _clrAssemblyScope = null;
        _clrNamespaceScope = type.Namespace;
        _clrTypeScope = ReflectionMetadataUtilities.GetDisplayName(type);
        _currentTopicName = _clrTypeScope;
        InvalidateDerivedCaches();
        ApplyFilter(_sidebar.Scroll.PageSize > 0 ? _sidebar.Scroll.PageSize : 10);
    }

    private bool NavigateClrUp()
    {
        if (_clrTypeScope is not null)
        {
            _clrTypeScope = null;
            _clrAssemblyScope = null;
            _clrNamespaceScope = null;
            _currentTopicName = null;
            InvalidateDerivedCaches();
            ApplyFilter(_sidebar.Scroll.PageSize > 0 ? _sidebar.Scroll.PageSize : 10);
            return true;
        }

        if (_clrNamespaceScope is not null)
        {
            _clrNamespaceScope = null;
            _clrAssemblyScope = null;
            _currentTopicName = null;
            InvalidateDerivedCaches();
            ApplyFilter(_sidebar.Scroll.PageSize > 0 ? _sidebar.Scroll.PageSize : 10);
            return true;
        }

        if (_clrAssemblyScope is not null)
        {
            _clrAssemblyScope = null;
            _currentTopicName = null;
            InvalidateDerivedCaches();
            ApplyFilter(_sidebar.Scroll.PageSize > 0 ? _sidebar.Scroll.PageSize : 10);
            return true;
        }

        return false;
    }

    private BindingFlags GetClrMemberBindingFlags()
    {
        return BindingFlags.Public |
               BindingFlags.Instance |
               BindingFlags.Static |
               (_clrDeclaredOnly ? BindingFlags.DeclaredOnly : 0);
    }

    private static string BuildClrMemberLabel(Type scopeType, string memberName, string memberTypeName, Type? declaringType)
    {
        if (declaringType is null || declaringType == scopeType)
        {
            return $"{memberName} : {memberTypeName}";
        }

        return $"{memberName} : {memberTypeName} [from {ReflectionMetadataUtilities.GetDisplayName(declaringType)}]";
    }

    private string[] FilterClrNamespacesForTree(IReadOnlyList<string> namespaces)
    {
        var directNamespaces = namespaces
            .Where(ns => !string.IsNullOrWhiteSpace(ns))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(ns => ns, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var expanded = new HashSet<string>(directNamespaces, StringComparer.Ordinal);
        foreach (var ns in directNamespaces)
        {
            foreach (var ancestor in GetClrNamespaceAncestors(ns))
            {
                expanded.Add(ancestor);
            }
        }

        var all = expanded
            .OrderBy(ns => ns, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (string.IsNullOrWhiteSpace(_query))
        {
            return all;
        }

        var allSet = all.ToHashSet(StringComparer.Ordinal);
        var visible = new HashSet<string>(StringComparer.Ordinal);

        foreach (var ns in all.Where(ns => MatchesQuery(ns)))
        {
            visible.Add(ns);

            foreach (var ancestor in GetClrNamespaceAncestors(ns))
            {
                if (allSet.Contains(ancestor))
                {
                    visible.Add(ancestor);
                }
            }
        }

        return all.Where(visible.Contains).ToArray();
    }

    private bool HasCollapsedClrNamespaceAncestor(string namespaceName)
    {
        foreach (var ancestor in GetClrNamespaceAncestors(namespaceName))
        {
            if (_collapsedSections.Contains(BuildClrNamespaceTreeSectionKey(ancestor)))
            {
                return true;
            }
        }

        return false;
    }

    private bool ClrNamespaceHasChildren(string namespaceName)
    {
        return GetClrNamespaceDescendants(namespaceName)
            .Any(candidate => !string.Equals(candidate, namespaceName, StringComparison.Ordinal));
    }

    private bool ClrNamespaceHasTreeChildren(string namespaceName)
    {
        return CountClrChildNamespaces(namespaceName) > 0 || GetClrDirectTypes(namespaceName).Length > 0;
    }

    private Type[] GetClrDirectTypes(string namespaceName)
    {
        return GetClrTypesForCurrentScope()
            .Where(type => string.Equals(type.Namespace ?? string.Empty, namespaceName, StringComparison.Ordinal))
            .OrderBy(type => BuildCompactClrTypeLabel(type), StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private int CountClrSubtreeTypes(string namespaceName)
    {
        return GetClrTypesForCurrentScope()
            .Count(type =>
            {
                var candidate = type.Namespace ?? string.Empty;
                return string.Equals(candidate, namespaceName, StringComparison.Ordinal) ||
                       IsClrNamespaceDescendant(namespaceName, candidate);
            });
    }

    private int CountClrChildNamespaces(string namespaceName)
    {
        return GetClrTypesForCurrentScope()
            .Select(type => type.Namespace)
            .Where(ns => !string.IsNullOrWhiteSpace(ns))
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .Count(candidate => string.Equals(GetClrParentNamespace(candidate), namespaceName, StringComparison.Ordinal));
    }

    private int CountClrAssembliesForNamespace(string namespaceName)
    {
        return GetClrTypesForCurrentScope()
            .Where(type =>
            {
                var candidate = type.Namespace ?? string.Empty;
                return string.Equals(candidate, namespaceName, StringComparison.Ordinal) ||
                       IsClrNamespaceDescendant(namespaceName, candidate);
            })
            .Select(type => type.Assembly.GetName().Name ?? type.Assembly.FullName ?? "<unknown>")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
    }

    private IEnumerable<string> GetClrNamespaceDescendants(string namespaceName)
    {
        return GetClrTypesForCurrentScope()
            .Select(type => type.Namespace)
            .Where(ns => !string.IsNullOrWhiteSpace(ns))
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .Where(candidate => string.Equals(candidate, namespaceName, StringComparison.Ordinal) || IsClrNamespaceDescendant(namespaceName, candidate))
            .OrderBy(candidate => candidate, StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> GetClrNamespaceAncestors(string namespaceName)
    {
        var current = namespaceName;
        while (true)
        {
            var lastDot = current.LastIndexOf('.');
            if (lastDot <= 0)
            {
                yield break;
            }

            current = current[..lastDot];
            yield return current;
        }
    }

    private static string? GetClrParentNamespace(string namespaceName)
    {
        var lastDot = namespaceName.LastIndexOf('.');
        return lastDot <= 0 ? null : namespaceName[..lastDot];
    }

    private static bool IsClrNamespaceDescendant(string parentNamespace, string candidateNamespace)
    {
        return candidateNamespace.Length > parentNamespace.Length &&
               candidateNamespace.StartsWith(parentNamespace + ".", StringComparison.Ordinal);
    }

    private static int GetClrNamespaceDepth(string namespaceName) => namespaceName.Count(character => character == '.');

    private bool IsClrNamespaceExpanded(string namespaceName)
    {
        return !string.IsNullOrWhiteSpace(_query) || _expandedClrNamespaces.Contains(namespaceName);
    }

    private static string BuildClrNamespaceTreeSectionKey(string namespaceName) => $"clr:namespace-tree:{namespaceName}";

    private static string BuildCompactClrTypeLabel(Type type)
    {
        var displayName = ReflectionMetadataUtilities.GetDisplayName(type);
        var ns = type.Namespace;

        if (!string.IsNullOrWhiteSpace(ns) &&
            displayName.StartsWith(ns + ".", StringComparison.Ordinal))
        {
            return displayName[(ns.Length + 1)..];
        }

        return displayName;
    }

    private Type[] FilterClrTypesForTree(IReadOnlyList<Type> types)
    {
        if (types.Count == 0)
        {
            return Array.Empty<Type>();
        }

        if (string.IsNullOrWhiteSpace(_query))
        {
            return types.ToArray();
        }

        return types
            .Where(type =>
                MatchesQuery(ReflectionMetadataUtilities.GetDisplayName(type)) ||
                MatchesQuery(BuildCompactClrTypeLabel(type)) ||
                MatchesQuery(type.FullName ?? string.Empty))
            .ToArray();
    }

    private static string BuildClrMethodGroupLabel(Type scopeType, IGrouping<string, MethodInfo> group)
    {
        var count = group.Count();
        var label = count == 1 ? $"{group.Key}(...)" : $"{group.Key}(...) × {count}";
        var declaringTypes = group
            .Select(method => method.DeclaringType)
            .Where(type => type is not null)
            .Cast<Type>()
            .DistinctBy(type => type.AssemblyQualifiedName ?? type.FullName ?? type.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (declaringTypes.Length == 0)
        {
            return label;
        }

        if (declaringTypes.Length == 1 &&
            !string.Equals(declaringTypes[0].AssemblyQualifiedName, scopeType.AssemblyQualifiedName, StringComparison.Ordinal))
        {
            return $"{label} [from {ReflectionMetadataUtilities.GetDisplayName(declaringTypes[0])}]";
        }

        return declaringTypes.Length > 1 ? $"{label} [mixed]" : label;
    }

    private static void AddClrGenericSection(List<HelpDetailEntry> lines, Type type, int width)
    {
        if (!type.IsGenericType)
        {
            return;
        }

        var definition = type.IsGenericTypeDefinition ? type : type.GetGenericTypeDefinition();
        var definitionParameters = definition.GetGenericArguments();
        var concreteArguments = type.GetGenericArguments();

        lines.Add(new(string.Empty, HelpDetailEntryKind.Blank));
        lines.Add(new("Generic Parameters", HelpDetailEntryKind.SectionHeading));

        for (var index = 0; index < definitionParameters.Length; index += 1)
        {
            var parameter = definitionParameters[index];
            var concrete = concreteArguments.Length > index ? concreteArguments[index] : parameter;
            var constraints = BuildGenericConstraintDescription(parameter);
            var resolvedSuffix = concrete.IsGenericParameter
                ? string.Empty
                : $" = {ReflectionMetadataUtilities.GetDisplayName(concrete)}";

            lines.Add(new($"  {parameter.Name}{resolvedSuffix}", HelpDetailEntryKind.Meta));

            if (!string.IsNullOrWhiteSpace(constraints))
            {
                lines.AddRange(TextDocumentFormatter.WrapParagraph(constraints, width, "    ", "    ")
                    .Select(line => new HelpDetailEntry(line, HelpDetailEntryKind.Text)));
            }
        }
    }

    private static string BuildGenericConstraintDescription(Type parameter)
    {
        if (!parameter.IsGenericParameter)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        var attributes = parameter.GenericParameterAttributes;

        if (attributes.HasFlag(GenericParameterAttributes.ReferenceTypeConstraint))
        {
            parts.Add("class");
        }

        if (attributes.HasFlag(GenericParameterAttributes.NotNullableValueTypeConstraint))
        {
            parts.Add("struct");
        }

        if (attributes.HasFlag(GenericParameterAttributes.DefaultConstructorConstraint))
        {
            parts.Add("new()");
        }

        foreach (var constraint in parameter.GetGenericParameterConstraints())
        {
            parts.Add(ReflectionMetadataUtilities.GetDisplayName(constraint));
        }

        return parts.Count == 0
            ? "No explicit constraints."
            : $"Constraints: {string.Join(", ", parts)}";
    }

    private static void AddParameterSection(List<HelpDetailEntry> lines, IReadOnlyList<ParameterInfo> parameters, int width)
    {
        lines.Add(new(string.Empty, HelpDetailEntryKind.Blank));
        lines.Add(new("Parameters", HelpDetailEntryKind.SectionHeading));

        if (parameters.Count == 0)
        {
            lines.Add(new("  <none>", HelpDetailEntryKind.Meta));
            return;
        }

        foreach (var parameterLine in BuildParameterSummaryLines(parameters, width, "  "))
        {
            lines.Add(new(parameterLine, HelpDetailEntryKind.Text));
        }
    }

    private static IReadOnlyList<string> BuildParameterSummaryLines(IReadOnlyList<ParameterInfo> parameters, int width, string indent)
    {
        var lines = new List<string>();

        foreach (var parameter in parameters)
        {
            var prefix = parameter.IsOut ? "out " : parameter.ParameterType.IsByRef ? "ref " : string.Empty;
            var defaultSuffix = parameter.HasDefaultValue
                ? $" = {FormatDefaultValue(parameter.DefaultValue)}"
                : string.Empty;
            var text = $"{prefix}{ReflectionMetadataUtilities.GetDisplayName(UnwrapByRef(parameter.ParameterType))} {parameter.Name}{defaultSuffix}";
            lines.AddRange(TextDocumentFormatter.WrapParagraph(text, width, indent, indent));
        }

        return lines;
    }

    private static string BuildConstructorInvocationExample(ConstructorInfo constructor)
    {
        var typeName = constructor.DeclaringType is null
            ? ".ctor"
            : ReflectionMetadataUtilities.GetDisplayName(constructor.DeclaringType);
        var parameters = string.Join(", ", constructor.GetParameters().Select(parameter => parameter.Name ?? "value"));
        return $"{typeName}({parameters})";
    }

    private static string FormatDefaultValue(object? value)
    {
        return value switch
        {
            null => "null",
            string text => $"\"{text}\"",
            char c => $"'{c}'",
            bool b => b ? "true" : "false",
            _ => value.ToString() ?? string.Empty,
        };
    }

    private IReadOnlyList<Type> GetClrTypesForCurrentScope()
    {
        var index = EnsureClrBrowseIndex();
        if (_clrAssemblyScope is not null && index.ByName.TryGetValue(_clrAssemblyScope, out var assembly))
        {
            return assembly.Types;
        }

        return index.AllTypes;
    }

    private bool TryResolveClrType(HelpTopic topic, out Type type)
    {
        ArgumentNullException.ThrowIfNull(topic);

        if (_sidebar.TryGetSelected(out var selected) &&
            selected.Kind == HelpBrowserListEntryKind.ClrType &&
            !string.IsNullOrWhiteSpace(selected.Value))
        {
            var selectedType = ResolveClrTypeByDisplayName(selected.Value!);
            if (selectedType is not null)
            {
                type = selectedType;
                return true;
            }
        }

        if (!string.IsNullOrWhiteSpace(_currentTopicName))
        {
            var currentType = ResolveClrTypeByDisplayName(_currentTopicName!);
            if (currentType is not null)
            {
                type = currentType;
                return true;
            }
        }

        var resolved = ResolveClrTypeByDisplayName(topic.Name);
        if (resolved is not null)
        {
            type = resolved;
            return true;
        }

        type = typeof(object);
        return false;
    }

    private ClrBrowseIndex EnsureClrBrowseIndex()
    {
        if (_clrBrowseIndex is not null)
        {
            return _clrBrowseIndex;
        }

        var assemblies = TypeCatalog.GetAssemblies()
            .OrderBy(assembly => assembly.GetName().Name, StringComparer.OrdinalIgnoreCase)
            .Select(assembly =>
            {
                var types = TypeCatalog.GetAssemblyTypes(assembly)
                    .DistinctBy(type => type.AssemblyQualifiedName ?? type.FullName ?? type.Name, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(type => ReflectionMetadataUtilities.GetDisplayName(type), StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var namespaces = types
                    .Select(type => type.Namespace)
                    .Where(ns => !string.IsNullOrWhiteSpace(ns))
                    .Cast<string>()
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(ns => ns, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                var name = assembly.GetName().Name ?? assembly.FullName ?? "<unknown>";
                return new ClrAssemblyBrowseInfo(name, assembly.FullName ?? name, types, namespaces);
            })
            .ToArray();

        var byName = new Dictionary<string, ClrAssemblyBrowseInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var assembly in assemblies)
        {
            byName.TryAdd(assembly.Name, assembly);
        }
        var allTypes = assemblies
            .SelectMany(assembly => assembly.Types)
            .DistinctBy(type => type.AssemblyQualifiedName ?? type.FullName ?? type.Name, StringComparer.OrdinalIgnoreCase)
            .OrderBy(type => ReflectionMetadataUtilities.GetDisplayName(type), StringComparer.OrdinalIgnoreCase)
            .ToArray();

        _clrBrowseIndex = new ClrBrowseIndex(assemblies, byName, allTypes);
        return _clrBrowseIndex;
    }

    private Type? ResolveClrTypeByDisplayName(string displayName)
    {
        var resolved = _runtime.TypeResolver.Resolve(displayName);
        if (resolved is not null)
        {
            return resolved;
        }

        return EnsureClrBrowseIndex().AllTypes.FirstOrDefault(type =>
            string.Equals(ReflectionMetadataUtilities.GetDisplayName(type), displayName, StringComparison.OrdinalIgnoreCase));
    }

    private static void AddSignatureSection(
        List<HelpDetailEntry> lines,
        string heading,
        IReadOnlyList<string> rows,
        int totalCount,
        int width,
        int take = 8)
    {
        lines.Add(new(string.Empty, HelpDetailEntryKind.Blank));
        lines.Add(new(heading, HelpDetailEntryKind.SectionHeading));

        if (totalCount == 0)
        {
            lines.Add(new("  <none>", HelpDetailEntryKind.Meta));
            return;
        }

        foreach (var row in rows.Take(take))
        {
            lines.AddRange(TextDocumentFormatter.WrapParagraph(row, width, "  ", "    ")
                .Select(line => new HelpDetailEntry(line, HelpDetailEntryKind.Text)));
        }

        if (totalCount > take)
        {
            lines.Add(new($"  … {totalCount - take} more", HelpDetailEntryKind.Meta));
        }
    }

    private static string GetClrTypeKindLabel(Type type)
    {
        if (type.IsInterface)
        {
            return "Interface";
        }

        if (type.IsEnum)
        {
            return "Enum";
        }

        if (type.IsArray)
        {
            return "Array";
        }

        if (type.IsValueType)
        {
            return "Struct";
        }

        if (type.IsClass && type.IsAbstract && type.IsSealed)
        {
            return "Static Class";
        }

        if (type.IsAbstract)
        {
            return "Abstract Class";
        }

        return type.IsClass ? "Class" : "Type";
    }

    private IEnumerable<(string Text, ToshTextStyleConfig Style)> BuildClrNamespaceSegments(
        HelpBrowserListEntry item,
        bool isSelected,
        ToshTuiThemeConfig theme)
    {
        var label = item.Label;
        var leafName = item.RawLabel.Split('.').Last();
        var leafIndex = label.LastIndexOf(leafName, StringComparison.Ordinal);
        if (leafIndex < 0)
        {
            yield return (label, TuiRenderHelpers.MergeListStyles(theme.Namespace, theme.SelectedItem, isSelected, preserveForeground: true));
            yield break;
        }

        var countsIndex = label.IndexOf(" [", leafIndex, StringComparison.Ordinal);
        var prefix = label[..leafIndex];
        var name = countsIndex >= 0 ? label[leafIndex..countsIndex] : label[leafIndex..];
        var suffix = countsIndex >= 0 ? label[countsIndex..] : string.Empty;

        if (prefix.Length > 0)
        {
            yield return (prefix, theme.TreeGuide);
        }

        yield return (name, TuiRenderHelpers.MergeListStyles(theme.Namespace, theme.SelectedItem, isSelected, preserveForeground: true));

        if (suffix.Length > 0)
        {
            yield return (suffix, theme.Meta);
        }
    }

    private IEnumerable<(string Text, ToshTextStyleConfig Style)> BuildClrTypeSegments(
        HelpBrowserListEntry item,
        bool isSelected,
        ToshTuiThemeConfig theme)
    {
        var label = item.Label;
        var guideIndex = label.LastIndexOf("─ ", StringComparison.Ordinal);
        if (guideIndex >= 0)
        {
            var prefix = label[..(guideIndex + 2)];
            var name = label[(guideIndex + 2)..];

            if (prefix.Length > 0)
            {
                yield return (prefix, theme.TreeGuide);
            }

            yield return (name, TuiRenderHelpers.MergeListStyles(theme.Type, theme.SelectedItem, isSelected, preserveForeground: true));
            yield break;
        }

        yield return (label, TuiRenderHelpers.MergeListStyles(theme.Type, theme.SelectedItem, isSelected, preserveForeground: true));
    }

    private string? BuildConstructorInsertionText(string? signature)
    {
        if (string.IsNullOrWhiteSpace(signature))
        {
            return null;
        }

        var type = ResolveCurrentClrTypeScope();
        if (type is null)
        {
            return null;
        }

        var constructor = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .FirstOrDefault(candidate =>
                string.Equals(
                    ReflectionMetadataUtilities.FormatConstructorSignature(candidate),
                    signature,
                    StringComparison.OrdinalIgnoreCase));

        if (constructor is not null)
        {
            return $"new {BuildConstructorInvocationExample(constructor)}";
        }

        if (type.IsValueType &&
            !type.IsEnum &&
            string.Equals(signature, $"{ReflectionMetadataUtilities.GetDisplayName(type)}()", StringComparison.OrdinalIgnoreCase))
        {
            return $"new {ReflectionMetadataUtilities.GetDisplayName(type)}()";
        }

        return $"new {ReflectionMetadataUtilities.GetDisplayName(type)}(";
    }

    private static string? ExtractClrMemberInsertionText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var parts = value.Split('|', 3);
        return parts.Length == 3 ? parts[2] : value;
    }

    private static string? BuildMethodInsertionText(string? methodName)
    {
        if (string.IsNullOrWhiteSpace(methodName))
        {
            return null;
        }

        return methodName + "(";
    }
}
