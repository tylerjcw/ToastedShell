using Tosh.Language.Parsing;
using Tosh.Runtime;
using Tosh.Runtime.Formats;

namespace Tosh.Stdlib.Data;

/// <summary>
/// Tōast Object Notation — <c>TOAST-0092</c>.
/// </summary>
/// <remarks>
/// <para>
/// The notation is the subset of Tōast's own value syntax that means something without a schema.
/// Reading is therefore not a second parser: the document is parsed by the real one, walked by
/// <see cref="TonValidator"/> to refuse everything outside the notation, and only then
/// evaluated. One grammar, one parser, and no reader that can drift from the writer.
/// </para>
/// <para>
/// This is the first format in the registry that is not a pure text transform, which is why it
/// implements <see cref="IContextualDataFormat"/>: it resolves names against the program's own
/// declared types, deliberately, because that is what makes a value recoverable as itself.
/// </para>
/// </remarks>
internal sealed class TonDataFormat : IContextualDataFormat
{
    public string Name => "ton";

    public IReadOnlyList<string> Aliases => ["toast"];

    public string Description =>
        "Tōast Object Notation — the subset of Tōast's value syntax that is meaningful without a schema.";

    public async IAsyncEnumerable<object?> SerializeAsync(
        IReadOnlyList<object?> values,
        IReadOnlyList<object?> arguments)
    {
        await Task.CompletedTask;

        // A heterogeneous stream needs no envelope: every value carries its own type, so the
        // document is simply the values one after another.
        foreach (var value in values)
        {
            yield return TonWriter.Write(value, null);
        }
    }

    public async IAsyncEnumerable<object?> SerializeAsync(
        IReadOnlyList<object?> values,
        IReadOnlyList<object?> arguments,
        CommandContext context)
    {
        await Task.CompletedTask;

        foreach (var value in values)
        {
            yield return TonWriter.Write(value, context.ShellTypes);
        }
    }

    public IAsyncEnumerable<object?> DeserializeAsync(string text, IReadOnlyList<object?> arguments) =>
        throw new InvalidOperationException(
            "'from ton' needs the declared types of the program reading it, which this entry "
            + "point does not carry. Call it through `from ton`, which supplies them.");

    public async IAsyncEnumerable<object?> DeserializeAsync(
        string text,
        IReadOnlyList<object?> arguments,
        CommandContext context)
    {
        var parsed = ToshParser.Parse(text, "<ton>");

        if (parsed.Diagnostics.Count > 0)
        {
            var first = parsed.Diagnostics[0];
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh.ton.malformed",
                Title: $"The document could not be parsed: {first.Title}",
                SourceName: "<ton>",
                SourceText: text,
                Span: first.Span,
                Label: first.Label));
        }

        // Before anything is built. A construct outside the notation is refused here rather than
        // caught partway through doing whatever it does.
        TonValidator.Validate(parsed.Statement, context.ShellTypes);

        if (context.LanguageRuntime.Evaluator is not { } evaluator)
        {
            throw new InvalidOperationException(
                "'from ton' has no evaluator to rebuild the document's values with.");
        }

        await foreach (var value in evaluator.EvaluateAsync(text, "<ton>", context.CancellationToken))
        {
            yield return value;
        }
    }
}
