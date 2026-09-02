using Tosh.Language.Parsing;
using Tosh.Runtime;

namespace Tosh.Stdlib.Data;

/// <summary>
/// Rejects everything a TON document may not contain, before any value is built —
/// <c>TOAST-0092</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>An allowlist, not a blocklist.</b> The language has forty argument node kinds and the
/// notation admits about ten. Naming what is permitted means a node kind added to the language
/// later is refused by default; naming what is forbidden would mean it is admitted by default,
/// and the difference between those two defaults is the whole security posture.
/// </para>
/// <para>
/// <b>Validation precedes evaluation.</b> The document is parsed with the real parser, walked
/// here, and only then evaluated — so a construct is refused before it can do anything, rather
/// than being caught partway through doing it.
/// </para>
/// <para>
/// <b>Only Tōast-declared types are admitted.</b> Not CLR types.
/// <c>new System.Diagnostics.ProcessStartInfo("ls")</c> is ordinary Tōast, and a notation that
/// resolved through the same path would let a document name any public type in any loaded
/// assembly — the `TypeNameHandling` and Java gadget surface exactly, where the attacker
/// supplies no code, only the name of a type whose construction does something. Restricting to
/// declared types closes the class structurally rather than by blocklist.
/// </para>
/// <para>
/// <b>A path is a lookup, never an invocation.</b> Member access is not in the accepted set at
/// all, so <c>DateTime::Now</c> cannot be written; and a path's head must name a declared enum
/// or union, so <c>Math::PI</c> is refused on the kind of the type rather than by naming it.
/// </para>
/// </remarks>
internal static class TonValidator
{
    internal static void Validate(StatementSyntax root, IShellNamedTypeView? types)
    {
        ValidateStatement(root, types);
    }

    private static void ValidateStatement(StatementSyntax statement, IShellNamedTypeView? types)
    {
        switch (statement)
        {
            case ScriptStatementSyntax script:
                foreach (var child in script.Statements)
                {
                    ValidateStatement(child, types);
                }
                return;

            case PipelineStatementSyntax pipeline:
                ValidatePipeline(pipeline.Pipeline, types);
                return;

            default:
                throw Refuse(
                    Describe(statement),
                    statement.Span,
                    "a document is a sequence of values, and nothing else");
        }
    }

    private static void ValidatePipeline(PipelineSyntax pipeline, IShellNamedTypeView? types)
    {
        // A pipeline with more than one stage is a computation, whatever the stages contain.
        if (pipeline.Stages.Count != 1)
        {
            throw Refuse(
                "a pipeline",
                pipeline.Stages.Count > 0 ? pipeline.Stages[0].Span : default,
                "a document holds values, and a pipeline computes one");
        }

        if (pipeline.Stages[0] is not ExpressionPipelineStageSyntax stage)
        {
            throw Refuse(Describe(pipeline.Stages[0]), pipeline.Stages[0].Span, "expected a value");
        }

        ValidateValue(stage.Expression, types);
    }

    private static void ValidateValue(ArgumentSyntax argument, IShellNamedTypeView? types)
    {
        switch (argument)
        {
            case LiteralArgumentSyntax:
                return;

            case ArrayLiteralArgumentSyntax array:
                foreach (var item in array.Items) { ValidateValue(item, types); }
                return;

            case SetLiteralArgumentSyntax set:
                foreach (var item in set.Items) { ValidateValue(item, types); }
                return;

            case TupleLiteralArgumentSyntax tuple:
                foreach (var item in tuple.Items) { ValidateValue(item, types); }
                return;

            case DictLiteralArgumentSyntax dict:
                foreach (var entry in dict.Entries)
                {
                    ValidateValue(entry.Key, types);
                    ValidateValue(entry.Value, types);
                }
                return;

            case RecordLiteralArgumentSyntax record:
                ValidateRecordFields(record, types);
                return;

            case NewObjectArgumentSyntax construction:
                ValidateConstruction(construction, types);
                return;

            case StaticMemberAccessArgumentSyntax path:
                ValidatePath(path.Path, path.Span, types);
                return;

            case StaticMethodCallArgumentSyntax call:
                ValidateVariantCall(call, types);
                return;

            case NamedArgumentSyntax named:
                ValidateValue(named.Value, types);
                return;

            default:
                throw Refuse(Describe(argument), argument.Span, "not part of the notation");
        }
    }

    private static void ValidateRecordFields(RecordLiteralArgumentSyntax record, IShellNamedTypeView? types)
    {
        foreach (var entry in record.Fields)
        {
            if (entry is not RecordFieldSyntax field)
            {
                throw Refuse("a record entry that is not a named field", entry.Span, "write 'Name = value'");
            }

            ValidateValue(field.Value, types);
        }
    }

    private static void ValidateConstruction(NewObjectArgumentSyntax construction, IShellNamedTypeView? types)
    {
        RequireDeclaredType(construction.EffectiveBareName, construction.Span, types);

        foreach (var argument in construction.Arguments)
        {
            // Positional arguments are accepted for a *union variant* only, which has none to
            // permute. A record's field order is not part of its meaning, so a positional
            // document would be corrupted the day someone reorders the declaration.
            if (argument is not NamedArgumentSyntax)
            {
                throw Refuse(
                    "a positional constructor argument",
                    argument.Span,
                    "name the field — 'new Exchange(Item = \"Emerald\")' — so the document survives "
                        + "a change to the declaration's field order");
            }

            ValidateValue(argument, types);
        }

        if (construction.Initializer is { } initializer)
        {
            ValidateRecordFields(initializer, types);
        }
    }

    private static void ValidateVariantCall(StaticMethodCallArgumentSyntax call, IShellNamedTypeView? types)
    {
        ValidatePath(call.Path, call.Span, types);

        foreach (var argument in call.Arguments)
        {
            // A variant takes positions only. Its field names belong to pattern matching and
            // member access, so a named argument parses and then fails to convert deep inside
            // evaluation — refused here instead, where the reason can be stated.
            if (argument is NamedArgumentSyntax named)
            {
                throw Refuse(
                    $"the named variant argument '{named.Name}'",
                    argument.Span,
                    "a union variant is constructed positionally; its field names are for "
                        + "pattern matching, not construction");
            }

            ValidateValue(argument, types);
        }
    }

    /// <summary>
    /// A path names a member of a declared enum or union, and nothing else.
    /// </summary>
    private static void ValidatePath(string path, TextSpan span, IShellNamedTypeView? types)
    {
        var separator = path.LastIndexOf('.');

        if (separator <= 0)
        {
            throw Refuse($"the bare name '{path}'", span, "a path names a type's member, as 'Profession::Librarian'");
        }

        RequireDeclaredType(path[..separator], span, types);
    }

    private static void RequireDeclaredType(string name, TextSpan span, IShellNamedTypeView? types)
    {
        if (types is null)
        {
            throw Refuse(
                $"'{name}'",
                span,
                "no declared types are in scope to resolve it against");
        }

        // The whole of the safety rule: a name the *program* declared, never a CLR type.
        if (!types.TryGetNamedType(name, out _))
        {
            throw Refuse(
                $"'{name}'",
                span,
                "a document may name only types this program declares — not a CLR type");
        }
    }

    private static string Describe(object node) => node.GetType().Name switch
    {
        "CommandSyntax" or "CommandPipelineStageSyntax" => "a command",
        "PipeForwardStageSyntax" => "a pipeline",
        nameof(VariableReferenceArgumentSyntax) => "a variable",
        nameof(MemberAccessArgumentSyntax) => "member access",
        nameof(MethodCallArgumentSyntax) => "a method call",
        nameof(OperatorArgumentSyntax) => "an operator",
        nameof(InterpolatedStringArgumentSyntax) => "an interpolated string",
        nameof(CommandSubstitutionArgumentSyntax) => "a command substitution",
        nameof(SubexpressionArgumentSyntax) => "a subexpression",
        nameof(AnonymousFunctionArgumentSyntax) => "a function",
        nameof(MatchArgumentSyntax) => "a match",
        nameof(ConditionalArgumentSyntax) => "a conditional",
        nameof(IndexAccessArgumentSyntax) => "an index access",
        nameof(QuoteArgumentSyntax) => "a quote",
        nameof(ThrowArgumentSyntax) => "a throw",
        nameof(BarewordArgumentSyntax) => "a bare word",
        var other => $"'{other}'",
    };

    private static ToshDiagnosticException Refuse(string what, TextSpan span, string why) =>
        ToshDiagnosticException.Create(new ToshDiagnostic(
            Code: "tosh.ton.not_a_document",
            Title: $"A TON document cannot contain {what}.",
            SourceName: "<ton>",
            SourceText: null,
            Span: span,
            Label: why));
}
