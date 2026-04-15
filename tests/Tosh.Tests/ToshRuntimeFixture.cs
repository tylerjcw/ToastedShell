using Tosh.Core;
using Tosh.Language;

namespace Tosh.Tests;

public sealed class ToshRuntimeFixture
{
    public ToshRuntimeFixture()
    {
        Runtime = ToshRuntime.CreateDefault();
        Engine = new ToshEngine(Runtime);
    }

    public ToshRuntime Runtime { get; }
    public ToshEngine Engine { get; }
}
