namespace Tosh.Runtime.Formats;

public interface IDataFormat
{
    string Name { get; }
    IReadOnlyList<string> Aliases { get; }
    string Description { get; }

    IAsyncEnumerable<object?> DeserializeAsync(string text, IReadOnlyList<object?> arguments);
    IAsyncEnumerable<object?> SerializeAsync(IReadOnlyList<object?> values, IReadOnlyList<object?> arguments);
}
