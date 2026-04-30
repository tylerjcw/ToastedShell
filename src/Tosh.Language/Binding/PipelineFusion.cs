using Tosh.Language.Parsing;

namespace Tosh.Language.Binding;

/// <summary>
/// Marks a parse-tree <see cref="PipelineSyntax"/> with a fused-execution
/// strategy that lets the evaluator skip generic stage-by-stage dispatch
/// for recognised patterns. The lowerer attaches one of these via the
/// <c>Fusion</c> body property; the evaluator consults it before
/// iterating <c>pipeline.Stages</c>.
///
/// Side-table only — does not affect record equality.
/// </summary>
public abstract record PipelineFusion;

/// <summary>
/// Pattern: <c>... | sort | first N</c> with no sort selector and no
/// uniqueness/numeric flags (only an optional reverse). The evaluator
/// runs the upstream stages normally and replaces sort+first with a
/// bounded priority-queue routine that retains O(N) state instead of
/// O(M) (where M = upstream count).
/// </summary>
/// <param name="StagesConsumed">Number of trailing stages this fusion replaces (always 2: sort and first).</param>
/// <param name="Count">N — items to retain.</param>
/// <param name="Reverse">If true, take the N largest; otherwise the N smallest.</param>
public sealed record SortFirstFusion(int StagesConsumed, int Count, bool Reverse) : PipelineFusion;
