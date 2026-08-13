using Tosh.Runtime;

namespace Tosh.Stdlib.Clr;

[CommandCategory("CLR")]
[CommandArgument("type", "The target type to cast to: a CLR type including generic types like list<int>, or a type declared in ToastScript.")]
[CommandArgument("value", "Optional value(s) to cast. If omitted, reads from the pipeline.", Required = false)]
[CommandExample("echo [1, 2, 3] | cast list<int>", Title = "Cast an array to a typed list")]
[CommandExample("echo 42 | cast string", Title = "Cast a number to a string")]
[CommandExample("cast Fuel 8", Title = "Cast a backing value to a declared enum member")]
[CommandNote("Cast converts to CLR target types, including constructed generic collection types like `list<int>`, and to types declared in ToastScript. A declared enum converts from a member name or a backing value; any other declared type converts only from a value that already is one, because cast does not construct.")]
[CommandOutput("The cast values in the target type.")]
[PipelineInput(AcceptsScalar = true, AcceptsList = true, Description = "Casts each piped value to the target type.")]
public sealed class CastCommand : ShellCommand
{
    public CastCommand()
        : base("cast", "Casts pipeline values to a CLR type or a type declared in ToastScript.", "cast <type> [value ...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count == 0)
        {
            throw new InvalidOperationException("cast requires a target type.");
        }

        var target = ResolveTarget(context, context.Arguments[0]);

        IReadOnlyList<object?> inputs = context.Arguments.Count > 1
            ? context.Arguments.Skip(1).ToArray()
            : await AsyncEnumerableExtensions.ToListAsync(context.Input, context.CancellationToken);

        foreach (var input in inputs)
        {
            yield return Convert(context, target, input);
        }
    }

    /// <summary>
    /// Resolves the target name to a CLR <see cref="Type"/> or a ToastScript-declared type.
    /// </summary>
    /// <remarks>
    /// `TS-P2-55`. A name that resolves to nothing is reported as its own kind of failure. Before
    /// this it arrived as "Unable to resolve type 'Fuel'" wrapped in `command_failed` — the same
    /// shape a *conversion* failure took, so "I misspelled the type" and "this value will not
    /// convert" were indistinguishable from the diagnostic.
    /// </remarks>
    private static object ResolveTarget(CommandContext context, object? argument)
    {
        if (argument is string or ShellTextLine)
        {
            var name = argument is ShellTextLine line ? line.Text : (string)argument!;

            // CLR first, declaration second. `cast` has always resolved against the CLR, and
            // its own documented example `cast list<int>` resolves to a *shell* descriptor for
            // the builtin list type — so asking the declaration side first turned that example
            // into "this value is not a 'list<int>'". Declared types are a fallback for names
            // the CLR resolver does not know, which is exactly the gap this item is about.
            var clrType = context.TypeResolver.Resolve(name);

            if (clrType is not null)
            {
                return clrType;
            }

            if (ReflectionMetadataUtilities.TryResolveShellType(context, name, out var declared))
            {
                return declared;
            }

            throw context.CreateDiagnostic(
                "tosh.runtime.unknown_cast_target",
                $"'{name}' does not name a type.",
                argumentIndex: 0,
                label: "no CLR type and no declaration by that name is in scope",
                help: "check the spelling, or 'using' the namespace the CLR type lives in. "
                    + "A type declared in another file needs 'require' or 'source' first.");
        }

        return ReflectionMetadataUtilities.ResolveType(context, argument);
    }

    private static object? Convert(CommandContext context, object target, object? input)
    {
        if (target is IShellTypeDescriptor declared)
        {
            if (ShellTypeConversion.TryConvert(input, declared, out var shellConverted, out var reason))
            {
                return shellConverted;
            }

            throw context.CreateDiagnostic(
                "tosh.runtime.cast_failed",
                $"Could not cast '{Describe(input)}' to '{declared.ShellTypeName}': {reason}.",
                argumentIndex: 0,
                label: $"this value is not a '{declared.ShellTypeName}'");
        }

        var clrType = (Type)target;

        if (TypeConversion.TryConvert(input, clrType, out var converted))
        {
            return converted;
        }

        // `TS-P2-111`. A refusal caused by fraction loss is answered by rounding;
        // one between unrelated types is not. Saying which is which costs a line
        // and saves the reader guessing why 7.0 casts and 7.9 does not.
        if (TypeConversion.WouldTruncate(input, clrType))
        {
            throw context.CreateDiagnostic(
                "tosh.runtime.cast_failed",
                $"Casting '{Describe(input)}' to {ReflectionMetadataUtilities.GetDisplayName(clrType)} " +
                "would discard its fractional part.",
                argumentIndex: 0,
                label: "round first with Math.Round, Math.Floor, Math.Ceiling or Math.Truncate");
        }

        throw context.CreateDiagnostic(
            "tosh.runtime.cast_failed",
            $"Could not cast '{Describe(input)}' to {ReflectionMetadataUtilities.GetDisplayName(clrType)}.",
            argumentIndex: 0,
            label: $"this value is not convertible to {ReflectionMetadataUtilities.GetDisplayName(clrType)}");
    }

    private static string Describe(object? value) => value?.ToString() ?? "null";
}
