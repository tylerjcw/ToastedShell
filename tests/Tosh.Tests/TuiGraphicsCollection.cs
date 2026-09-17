namespace Tosh.Tests;

/// <summary>
/// The tests that touch the terminal's graphics protocol, kept out of each other's way.
/// </summary>
/// <remarks>
/// <para>
/// <c>TuiGraphics.Protocol</c> is static because a process is attached to one terminal and
/// it speaks one protocol. That is right for the program and hostile to a test runner: xUnit
/// runs test classes in parallel, so one class setting Kitty for a moment made another draw
/// placements where it expected half blocks — and a *different* test failed on each run,
/// which is how a race announces itself.
/// </para>
/// <para>
/// Serialising the classes that read or write it is the fix that keeps the production shape
/// honest. Making the protocol an instance would mean threading it through every widget to
/// answer a question that has one answer per process.
/// </para>
/// </remarks>
[CollectionDefinition(Name)]
public sealed class TuiGraphicsCollection
{
    public const string Name = "tui-graphics-protocol";
}
