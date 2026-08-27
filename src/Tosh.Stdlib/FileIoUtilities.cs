using System.Collections;
using System.Text;
using Tosh.Runtime;

namespace Tosh.Stdlib;

internal static class FileIoUtilities
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static string ResolveRequiredPath(CommandContext context, int argumentIndex, string label = "path")
    {
        ArgumentNullException.ThrowIfNull(context);

        if (argumentIndex >= context.Arguments.Count)
        {
            throw new InvalidOperationException($"Missing required argument: {label}.");
        }

        return ShellPathArguments.Resolve(context.Shell().CurrentDirectory, context.Arguments[argumentIndex]);
    }

    public static void EnsureReadableFile(string path, string commandName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(commandName);

        if (Directory.Exists(path))
        {
            throw new InvalidOperationException($"'{commandName}' expects a file path, but '{path}' is a directory.");
        }

        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"File '{path}' does not exist.");
        }
    }

    public static async Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken = default)
    {
        EnsureReadableFile(path, "read-file");

        await using var stream = File.OpenRead(path);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    public static async Task<string> RenderTextPayloadAsync(CommandContext context, IReadOnlyList<object?> explicitValues)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(explicitValues);

        return await Text.WriteCommand.RenderAsync(context with { Arguments = explicitValues });
    }

    public static async Task<byte[]> ReadBytePayloadAsync(CommandContext context, IReadOnlyList<object?> explicitValues)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(explicitValues);

        var values = explicitValues.Count > 0
            ? explicitValues
            : await AsyncEnumerableExtensions.ToListAsync(context.Input, context.CancellationToken);

        if (values.Count == 0)
        {
            return Array.Empty<byte>();
        }

        var bytes = new List<byte>();

        foreach (var value in values)
        {
            AppendBytes(bytes, value);
        }

        return bytes.ToArray();
    }

    public static async Task WriteAllTextAsync(string path, string text, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        await File.WriteAllTextAsync(path, text, Utf8NoBom, cancellationToken);
    }

    public static async Task AppendAllTextAsync(string path, string text, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        await File.AppendAllTextAsync(path, text, Utf8NoBom, cancellationToken);
    }

    public static async Task WriteAllBytesAsync(string path, byte[] bytes, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(bytes);

        await File.WriteAllBytesAsync(path, bytes, cancellationToken);
    }

    private static void AppendBytes(List<byte> buffer, object? value)
    {
        switch (value)
        {
            case null:
                return;
            case byte[] bytes:
                buffer.AddRange(bytes);
                return;
            case Memory<byte> memory:
                buffer.AddRange(memory.ToArray());
                return;
            case ReadOnlyMemory<byte> memory:
                buffer.AddRange(memory.ToArray());
                return;
            case string text:
                buffer.AddRange(Utf8NoBom.GetBytes(text));
                return;
            case IEnumerable<byte> byteSequence:
                buffer.AddRange(byteSequence);
                return;
            case IEnumerable enumerable:
                foreach (var item in enumerable)
                {
                    if (TypeConversion.TryConvert(item, typeof(byte), out var converted) && converted is byte byteValue)
                    {
                        buffer.Add(byteValue);
                        continue;
                    }

                    throw new InvalidOperationException($"Value '{item}' could not be converted to a byte.");
                }

                return;
        }

        if (TypeConversion.TryConvert(value, typeof(byte), out var scalar) && scalar is byte byteScalar)
        {
            buffer.Add(byteScalar);
            return;
        }

        throw new InvalidOperationException($"Value of type '{value.GetType().FullName}' cannot be written as bytes.");
    }
}
