using System.Security.Cryptography;
using System.Text;

namespace Tosh.Core.Commands;

[CommandCategory("Data")]
public sealed class HashCommand : ShellCommand
{
    public HashCommand()
        : base("hash", "Computes hashes for text input or files.", "hash [algorithm] [path ...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var algorithmName = context.Arguments.Count > 0 && !LooksLikePath(context.Arguments[0]?.ToString())
            ? context.Arguments[0]?.ToString() ?? "sha256"
            : "sha256";
        var startIndex = context.Arguments.Count > 0 && !LooksLikePath(context.Arguments[0]?.ToString()) ? 1 : 0;
        var explicitPaths = ShellPathArguments.ExpandMany(context.Runtime.CurrentDirectory, context.Arguments.Skip(startIndex).ToArray());

        if (explicitPaths.Count > 0)
        {
            foreach (var path in explicitPaths)
            {
                if (!File.Exists(path))
                {
                    throw new InvalidOperationException($"File '{path}' does not exist.");
                }

                await using var stream = File.OpenRead(path);
                yield return CreateProjection(Path.GetFileName(path), path, algorithmName, await ComputeHashAsync(stream, algorithmName, context.CancellationToken));
            }

            yield break;
        }

        await foreach (var item in context.Input.WithCancellation(context.CancellationToken))
        {
            var text = item is ShellTextLine line ? line.Text : ExternalTextSerializer.Serialize(item);
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
            yield return CreateProjection(null, null, algorithmName, await ComputeHashAsync(stream, algorithmName, context.CancellationToken), item);
        }
    }

    private static System.Dynamic.ExpandoObject CreateProjection(string? name, string? path, string algorithm, string hash, object? value = null)
    {
        var fields = new List<KeyValuePair<string, object?>>
        {
            new("Algorithm", algorithm.ToUpperInvariant()),
            new("Hash", hash),
        };

        if (name is not null)
        {
            fields.Add(new KeyValuePair<string, object?>("Name", name));
        }

        if (path is not null)
        {
            fields.Add(new KeyValuePair<string, object?>("Path", path));
        }

        if (value is not null)
        {
            fields.Add(new KeyValuePair<string, object?>("Value", value));
        }

        return ShellRecordUtilities.CreateExpando(fields);
    }

    private static async Task<string> ComputeHashAsync(Stream stream, string algorithmName, CancellationToken cancellationToken)
    {
        using var algorithm = CreateAlgorithm(algorithmName);
        var hashBytes = await algorithm.ComputeHashAsync(stream, cancellationToken);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    private static HashAlgorithm CreateAlgorithm(string name)
    {
        return name.ToLowerInvariant() switch
        {
            "md5" => MD5.Create(),
            "sha1" => SHA1.Create(),
            "sha256" => SHA256.Create(),
            "sha384" => SHA384.Create(),
            "sha512" => SHA512.Create(),
            _ => throw new InvalidOperationException($"Unsupported hash algorithm '{name}'."),
        };
    }

    private static bool LooksLikePath(string? value)
    {
        return !string.IsNullOrWhiteSpace(value) &&
               (value.Contains(Path.DirectorySeparatorChar) || value.Contains(Path.AltDirectorySeparatorChar) || File.Exists(value));
    }
}
