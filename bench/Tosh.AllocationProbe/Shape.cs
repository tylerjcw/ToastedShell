namespace Tosh.AllocationProbe;

/// <summary>One expression shape to measure, as the body of a loop.</summary>
/// <param name="Name">What appears in the output's first column.</param>
/// <param name="Body">Loop body source, run once per iteration. Empty is the baseline.</param>
public readonly record struct Shape(string Name, string Body);
