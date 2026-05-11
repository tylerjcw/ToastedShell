using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using Tosh.Compiler.IR;

namespace Tosh.Language.Binding;

/// <summary>
/// Front-door for executing a lowered <see cref="BoundUnit"/>.
/// In v1 this is a thin facade over <see cref="ToshEngine"/>: it runs
/// the unit's underlying parse result. The public surface area is
/// stable so subsequent commits can replace individual carved-out
/// shapes (literals, variable refs, ranges, …) with bound-IR fast
/// paths without changing callers.
/// </summary>
public static class BoundEvaluator
{
    /// <summary>
    /// Lower the supplied source through <see cref="Lowerer"/> and
    /// evaluate the resulting <see cref="BoundUnit"/>.
    /// </summary>
    public static IAsyncEnumerable<object?> EvaluateAsync(
        ToshEngine engine,
        string source,
        string sourceName = "<input>",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(source);

        var parseResult = engine.Parse(source, sourceName);
        var unit = Lowerer.Lower(parseResult, engine.Runtime.Commands);
        return engine.EvaluateAsync(unit, cancellationToken);
    }

    /// <summary>Evaluate an already-lowered unit.</summary>
    public static IAsyncEnumerable<object?> EvaluateAsync(
        ToshEngine engine,
        BoundUnit unit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(unit);
        return engine.EvaluateAsync(unit, cancellationToken);
    }

    /// <summary>Convenience helper: evaluate to a list.</summary>
    public static async Task<IReadOnlyList<object?>> EvaluateToListAsync(
        ToshEngine engine,
        string source,
        string sourceName = "<input>",
        CancellationToken cancellationToken = default)
    {
        var results = new List<object?>();
        await foreach (var value in EvaluateAsync(engine, source, sourceName, cancellationToken)
            .WithCancellation(cancellationToken))
        {
            results.Add(value);
        }
        return results;
    }
}
