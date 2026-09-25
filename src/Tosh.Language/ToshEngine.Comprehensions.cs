using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text;
using System.Text.RegularExpressions;
using Tosh.Runtime;
using Tosh.Language.Binding;
using Tosh.Language.Bridge;
using Tosh.Language.Debugging;
using Tosh.Language.Parsing;

namespace Tosh.Language;

public sealed partial class ToshEngine
{
    private async Task EvaluateComprehensionClauseAsync(
        string sourceName,
        string sourceText,
        ComprehensionClauseSyntax clause,
        Func<CancellationToken, Task> bodyAction,
        CancellationToken cancellationToken)
    {
        var sourceValue = await EvaluateArgumentAsync(sourceName, sourceText, clause.Source, cancellationToken);

        // Eager comprehensions (list/set/dict) cannot iterate infinite sources
        if (IsInfiniteSource(sourceValue))
        {
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh.runtime.infinite_eager_comprehension",
                Title: "Cannot use an infinite source in a list, set, or dict comprehension. Use a generator comprehension (...) instead of [...] and pipe to '| first N'.",
                SourceName: sourceName,
                SourceText: sourceText,
                Span: clause.Source.Span,
                Label: "this source is infinite"));
        }

        // Parallel/zip: bind both clause variables per step; terminate when either source ends
        if (clause.InnerClause is not null && clause.InnerIsParallel)
        {
            var innerSourceValue = await EvaluateArgumentAsync(sourceName, sourceText, clause.InnerClause.Source, cancellationToken);
            using var outerEnum = ShellIterationUtilities.ExpandIterationItems(sourceValue).GetEnumerator();
            using var innerEnum = ShellIterationUtilities.ExpandIterationItems(innerSourceValue).GetEnumerator();

            while (outerEnum.MoveNext() && innerEnum.MoveNext())
            {
                cancellationToken.ThrowIfCancellationRequested();

                var vars = new Dictionary<string, object?>(StringComparer.Ordinal) { ["_"] = outerEnum.Current };
                AddClauseBindings(vars, clause, outerEnum.Current);
                AddClauseBindings(vars, clause.InnerClause, innerEnum.Current);
                using var _ = PushScope(vars);

                var skip = false;
                foreach (var modifier in clause.Modifiers)
                {
                    switch (modifier)
                    {
                        case ComprehensionWhereSyntax where:
                            if (!await EvaluateConditionAsync(sourceName, sourceText, where.Condition, cancellationToken))
                                skip = true;
                            break;
                        case ComprehensionLetSyntax let:
                            var letValue = await EvaluateArgumentAsync(sourceName, sourceText, let.Value, cancellationToken);
                            _scopes.Peek().Variables[let.VariableName] = ToVariableBinding(letValue);
                            break;
                    }
                    if (skip) break;
                }
                if (!skip)
                {
                    foreach (var modifier in clause.InnerClause.Modifiers)
                    {
                        switch (modifier)
                        {
                            case ComprehensionWhereSyntax where:
                                if (!await EvaluateConditionAsync(sourceName, sourceText, where.Condition, cancellationToken))
                                    skip = true;
                                break;
                            case ComprehensionLetSyntax let:
                                var letValue = await EvaluateArgumentAsync(sourceName, sourceText, let.Value, cancellationToken);
                                _scopes.Peek().Variables[let.VariableName] = ToVariableBinding(letValue);
                                break;
                        }
                        if (skip) break;
                    }
                }
                if (skip) continue;

                if (clause.InnerClause.InnerClause is not null)
                    await EvaluateComprehensionClauseAsync(sourceName, sourceText, clause.InnerClause.InnerClause, bodyAction, cancellationToken);
                else
                    await bodyAction(cancellationToken);
            }
            return;
        }

        foreach (var current in ShellIterationUtilities.ExpandIterationItems(sourceValue))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var vars = new Dictionary<string, object?>(StringComparer.Ordinal) { ["_"] = current };
            AddClauseBindings(vars, clause, current);
            using var _ = PushScope(vars);

            // Walk where/let modifiers in declared order: later clauses observe earlier lets.
            var skip = false;
            foreach (var modifier in clause.Modifiers)
            {
                switch (modifier)
                {
                    case ComprehensionWhereSyntax where:
                        if (!await EvaluateConditionAsync(sourceName, sourceText, where.Condition, cancellationToken))
                        {
                            skip = true;
                        }
                        break;
                    case ComprehensionLetSyntax let:
                        var letValue = await EvaluateArgumentAsync(sourceName, sourceText, let.Value, cancellationToken);
                        _scopes.Peek().Variables[let.VariableName] = ToVariableBinding(letValue);
                        break;
                }
                if (skip) break;
            }
            if (skip) continue;

            // Recurse into inner clause or execute body
            if (clause.InnerClause is not null)
            {
                await EvaluateComprehensionClauseAsync(sourceName, sourceText, clause.InnerClause, bodyAction, cancellationToken);
            }
            else
            {
                await bodyAction(cancellationToken);
            }
        }
    }

    /// Walks comprehension where/let modifiers in declared order.
    /// Returns true if iteration should be skipped (a where returned false).
    private bool ApplyComprehensionModifiersSync(
        string sourceName,
        string sourceText,
        IReadOnlyList<ComprehensionModifierSyntax> modifiers)
    {
        foreach (var modifier in modifiers)
        {
            switch (modifier)
            {
                case ComprehensionWhereSyntax where:
                    if (!EvaluateConditionAsync(sourceName, sourceText, where.Condition, CancellationToken.None)
                        .GetAwaiter().GetResult())
                    {
                        return true;
                    }
                    break;
                case ComprehensionLetSyntax let:
                    var letValue = EvaluateArgumentAsync(sourceName, sourceText, let.Value, CancellationToken.None)
                        .GetAwaiter().GetResult();
                    _scopes.Peek().Variables[let.VariableName] = ToVariableBinding(letValue);
                    break;
            }
        }
        return false;
    }

    private static void AddClauseBindings(Dictionary<string, object?> vars, ComprehensionClauseSyntax clause, object? current)
    {
        if (clause.DestructureNames is { } names)
        {
            var elements = ExtractDestructureElements(current);
            for (var i = 0; i < names.Count; i++)
                vars[names[i]] = elements is not null && i < elements.Count ? elements[i] : null;
        }
        else
        {
            vars[clause.VariableName] = current;
        }
    }

    private static IReadOnlyList<object?>? ExtractDestructureElements(object? value) =>
        value switch
        {
            IReadOnlyList<object?> list => list,
            KeyValuePair<object, object?> kvp => [kvp.Key, kvp.Value],
            KeyValuePair<string, object?> kvp => [kvp.Key, kvp.Value],
            _ => null,
        };

    /// <summary>
    /// Produces a lazy IEnumerable that evaluates comprehension body items on demand.
    /// The source is already evaluated; body evaluation blocks on async via GetAwaiter().GetResult().
    /// </summary>
    private IEnumerable<object?> EnumerateComprehensionLazily(
        string sourceName,
        string sourceText,
        ComprehensionClauseSyntax clause,
        ArgumentSyntax body,
        object? sourceValue)
    {
        // If there's a non-parallel inner clause and the outer source is infinite, use diagonal enumeration
        if (clause.InnerClause is not null && !clause.InnerIsParallel && IsInfiniteSource(sourceValue))
        {
            foreach (var item in EnumerateComprehensionDiagonal(sourceName, sourceText, clause, clause.InnerClause, body, sourceValue))
            {
                yield return item;
            }
            yield break;
        }

        // Parallel/zip: bind both clause variables per step; terminate when either source ends
        if (clause.InnerClause is not null && clause.InnerIsParallel)
        {
            var innerSource = EvaluateArgumentAsync(sourceName, sourceText, clause.InnerClause.Source, CancellationToken.None)
                .GetAwaiter().GetResult();
            using var outerEnum = ShellIterationUtilities.ExpandIterationItems(sourceValue).GetEnumerator();
            using var innerEnum = ShellIterationUtilities.ExpandIterationItems(innerSource).GetEnumerator();

            while (outerEnum.MoveNext() && innerEnum.MoveNext())
            {
                var vars = new Dictionary<string, object?>(StringComparer.Ordinal) { ["_"] = outerEnum.Current };
                AddClauseBindings(vars, clause, outerEnum.Current);
                AddClauseBindings(vars, clause.InnerClause, innerEnum.Current);
                using var scope = PushScope(vars);

                var skip = false;
                foreach (var modifier in clause.Modifiers)
                {
                    switch (modifier)
                    {
                        case ComprehensionWhereSyntax where:
                            if (!EvaluateConditionAsync(sourceName, sourceText, where.Condition, CancellationToken.None)
                                .GetAwaiter().GetResult())
                                skip = true;
                            break;
                        case ComprehensionLetSyntax let:
                            var letValue = EvaluateArgumentAsync(sourceName, sourceText, let.Value, CancellationToken.None)
                                .GetAwaiter().GetResult();
                            _scopes.Peek().Variables[let.VariableName] = ToVariableBinding(letValue);
                            break;
                    }
                    if (skip) break;
                }
                if (!skip)
                {
                    foreach (var modifier in clause.InnerClause.Modifiers)
                    {
                        switch (modifier)
                        {
                            case ComprehensionWhereSyntax where:
                                if (!EvaluateConditionAsync(sourceName, sourceText, where.Condition, CancellationToken.None)
                                    .GetAwaiter().GetResult())
                                    skip = true;
                                break;
                            case ComprehensionLetSyntax let:
                                var letValue = EvaluateArgumentAsync(sourceName, sourceText, let.Value, CancellationToken.None)
                                    .GetAwaiter().GetResult();
                                _scopes.Peek().Variables[let.VariableName] = ToVariableBinding(letValue);
                                break;
                        }
                        if (skip) break;
                    }
                }
                if (skip) continue;

                if (clause.InnerClause.InnerClause is not null)
                {
                    var deepSource = EvaluateArgumentAsync(sourceName, sourceText, clause.InnerClause.InnerClause.Source, CancellationToken.None)
                        .GetAwaiter().GetResult();
                    foreach (var item in EnumerateComprehensionLazily(sourceName, sourceText, clause.InnerClause.InnerClause, body, deepSource))
                    {
                        yield return item;
                    }
                }
                else
                {
                    yield return EvaluateArgumentAsync(sourceName, sourceText, body, CancellationToken.None)
                        .GetAwaiter().GetResult();
                }
            }
            yield break;
        }

        foreach (var current in ShellIterationUtilities.ExpandIterationItems(sourceValue))
        {
            var vars = new Dictionary<string, object?>(StringComparer.Ordinal) { ["_"] = current };
            AddClauseBindings(vars, clause, current);
            using var scope = PushScope(vars);

            // Walk modifiers in declared order (blocking on async each time)
            var skip = false;
            foreach (var modifier in clause.Modifiers)
            {
                switch (modifier)
                {
                    case ComprehensionWhereSyntax where:
                        if (!EvaluateConditionAsync(sourceName, sourceText, where.Condition, CancellationToken.None)
                            .GetAwaiter().GetResult())
                        {
                            skip = true;
                        }
                        break;
                    case ComprehensionLetSyntax let:
                        var letValue = EvaluateArgumentAsync(sourceName, sourceText, let.Value, CancellationToken.None)
                            .GetAwaiter().GetResult();
                        _scopes.Peek().Variables[let.VariableName] = ToVariableBinding(letValue);
                        break;
                }
                if (skip) break;
            }
            if (skip) continue;

            // Recurse into inner clause or yield body result
            if (clause.InnerClause is not null)
            {
                // Evaluate inner source eagerly for this iteration
                var innerSource = EvaluateArgumentAsync(sourceName, sourceText, clause.InnerClause.Source, CancellationToken.None)
                    .GetAwaiter().GetResult();

                foreach (var item in EnumerateComprehensionLazily(sourceName, sourceText, clause.InnerClause, body, innerSource))
                {
                    yield return item;
                }
            }
            else
            {
                yield return EvaluateArgumentAsync(sourceName, sourceText, body, CancellationToken.None)
                    .GetAwaiter().GetResult();
            }
        }
    }

    /// <summary>
    /// Returns true if the given value represents a known-infinite source
    /// (e.g. an infinite ToshRange or an unevaluated LazySequence).
    /// </summary>
    private static bool IsInfiniteSource(object? value) =>
        value is ToshRange { IsInfinite: true } or LazySequence { IsFiniteKnown: false };

    /// <summary>
    /// Produces lazy diagonal (Cantor) enumeration for nested comprehension clauses
    /// where at least one source is infinite. Walks anti-diagonals so that every
    /// (outer, inner) pair is reached in finite time.
    /// </summary>
    private IEnumerable<object?> EnumerateComprehensionDiagonal(
        string sourceName,
        string sourceText,
        ComprehensionClauseSyntax outerClause,
        ComprehensionClauseSyntax innerClause,
        ArgumentSyntax body,
        object? outerSourceValue)
    {
        // We cache outer/inner items as we advance through diagonals
        var outerCache = new List<object?>();
        using var outerEnum = ShellIterationUtilities.ExpandIterationItems(outerSourceValue).GetEnumerator();
        var outerDone = false;

        // For each outer item, we'll lazily evaluate the inner source and cache its items
        // But the inner source depends on the outer variable, so we need to cache the
        // (outerValue, innerCache, innerEnumerator) triple per outer index.
        var innerCaches = new List<(object? OuterValue, List<object?> Cache, IEnumerator<object?> Enum, bool Done)>();

        for (var diagonal = 0; ; diagonal++)
        {
            // Expand outer cache to cover this diagonal if possible
            while (!outerDone && outerCache.Count <= diagonal)
            {
                if (outerEnum.MoveNext())
                {
                    var outerVal = outerEnum.Current;
                    outerCache.Add(outerVal);

                    // Evaluate the inner source in the scope of this outer value
                    using var scope = PushScope(new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        [outerClause.VariableName] = outerVal,
                        ["_"] = outerVal,
                    });

                    // Apply outer modifiers (where/let in declared order)
                    if (ApplyComprehensionModifiersSync(sourceName, sourceText, outerClause.Modifiers))
                    {
                        // Mark as skipped with empty inner
                        innerCaches.Add((outerVal, new List<object?>(), EmptyEnumerator(), true));
                        continue;
                    }

                    var innerSource = EvaluateArgumentAsync(sourceName, sourceText, innerClause.Source, CancellationToken.None)
                        .GetAwaiter().GetResult();
                    var innerEnum = ShellIterationUtilities.ExpandIterationItems(innerSource).GetEnumerator();
                    innerCaches.Add((outerVal, new List<object?>(), innerEnum, false));
                }
                else
                {
                    outerDone = true;
                }
            }

            // Expand inner caches to cover this diagonal where needed
            for (var i = 0; i < innerCaches.Count && i <= diagonal; i++)
            {
                var j = diagonal - i;
                var entry = innerCaches[i];
                while (!entry.Done && entry.Cache.Count <= j)
                {
                    if (entry.Enum.MoveNext())
                    {
                        entry.Cache.Add(entry.Enum.Current);
                    }
                    else
                    {
                        innerCaches[i] = (entry.OuterValue, entry.Cache, entry.Enum, true);
                        entry = innerCaches[i];
                    }
                }
            }

            // Check termination: both outer and all inners exhausted
            if (outerDone)
            {
                var allInnersDone = true;
                var maxReachable = -1;
                for (var i = 0; i < innerCaches.Count; i++)
                {
                    if (!innerCaches[i].Done) allInnersDone = false;
                    var reach = i + innerCaches[i].Cache.Count - 1;
                    if (reach > maxReachable) maxReachable = reach;
                }
                if (allInnersDone && diagonal > maxReachable)
                    yield break;
            }

            // Walk anti-diagonal: i + j == diagonal
            var iMax = Math.Min(diagonal, outerCache.Count - 1);
            for (var i = 0; i <= iMax; i++)
            {
                var j = diagonal - i;
                if (i >= innerCaches.Count) continue;
                var entry = innerCaches[i];
                if (j >= entry.Cache.Count) continue;

                var outerVal = outerCache[i];
                var innerVal = entry.Cache[j];

                // Evaluate body in scope of both variables
                using var bodyScope = PushScope(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    [outerClause.VariableName] = outerVal,
                    ["_"] = outerVal,
                });

                // Re-apply outer modifiers in the body scope so body expressions can see outer lets.
                // Skip-logic is irrelevant here because the outer where was already checked above.
                ApplyComprehensionModifiersSync(sourceName, sourceText, outerClause.Modifiers);

                // Push inner scope
                using var innerScope = PushScope(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    [innerClause.VariableName] = innerVal,
                    ["_"] = innerVal,
                });

                // Apply inner modifiers in declared order
                if (ApplyComprehensionModifiersSync(sourceName, sourceText, innerClause.Modifiers))
                {
                    continue;
                }

                if (innerClause.InnerClause is not null)
                {
                    // Three+ levels of nesting: recurse normally for the deeper levels
                    var deepSource = EvaluateArgumentAsync(sourceName, sourceText, innerClause.InnerClause.Source, CancellationToken.None)
                        .GetAwaiter().GetResult();
                    foreach (var item in EnumerateComprehensionLazily(sourceName, sourceText, innerClause.InnerClause, body, deepSource))
                    {
                        yield return item;
                    }
                }
                else
                {
                    yield return EvaluateArgumentAsync(sourceName, sourceText, body, CancellationToken.None)
                        .GetAwaiter().GetResult();
                }
            }
        }
    }

    private static IEnumerator<object?> EmptyEnumerator()
    {
        yield break;
    }

}
