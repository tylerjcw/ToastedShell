using System.Security.Cryptography;
using System.Text;

namespace Tosh.Core.Commands;

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
        var explicitPaths = context.Arguments.Skip(startIndex).Select(argument => argument?.ToString()).Where(path => !string.IsNullOrWhiteSpace(path)).Cast<string>().ToArray();

        if (explicitPaths.Length > 0)
        {
            foreach (var rawPath in explicitPaths)
            {
                var path = PathUtilities.ResolvePath(context.Runtime.CurrentDirectory, rawPath);
                if (!File.Exists(path))
                {
                    throw new InvalidOperationException($"File '{path}' does not exist.");
                }

                await using var stream = File.OpenRead(path);
                yield return CreateProjection(rawPath, path, algorithmName, await ComputeHashAsync(stream, algorithmName, context.CancellationToken));
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

    private static ProjectedObject CreateProjection(string? name, string? path, string algorithm, string hash, object? value = null)
    {
        var fields = new List<ProjectedField>
        {
            new("Algorithm", "Algorithm", algorithm.ToUpperInvariant()),
            new("Hash", "Hash", hash),
        };

        if (name is not null)
        {
            fields.Add(new ProjectedField("Name", "Name", name));
        }

        if (path is not null)
        {
            fields.Add(new ProjectedField("Path", "Path", path));
        }

        if (value is not null)
        {
            fields.Add(new ProjectedField("Value", "Value", value));
        }

        return new ProjectedObject(fields);
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
