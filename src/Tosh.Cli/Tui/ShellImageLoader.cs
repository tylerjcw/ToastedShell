using System.Diagnostics;
using Tosh.Tui.Rendering;

namespace Tosh.Cli.Tui;

/// <summary>
/// Turns a path into pixels by asking whatever the reader already has installed.
/// </summary>
/// <remarks>
/// <para>
/// A video frame, a page of a PDF and a PNG are the same problem, and the tools that solve
/// it are already on the machine of anyone who wants the feature. Shelling out to them is
/// how <c>yazi</c> does it, costs the toolkit no image dependency, and means a format
/// nobody here has heard of works the day the reader installs something that reads it
/// (<c>TUI-0025</c>).
/// </para>
/// <para>
/// Everything comes back as binary PPM, which is a five-token header and then the bytes —
/// so the parser is forty lines instead of an image library.
/// </para>
/// </remarks>
internal static class ShellImageLoader
{
    /// <summary>How long a preview may take before it is not a preview any more.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    /// <summary>How many previews are remembered before the oldest is let go.</summary>
    /// <remarks>
    /// A file browser walked down a directory of photographs would otherwise hold every
    /// one it passed. Twenty-four is enough that arrowing back up a list is instant and
    /// small enough that a pane of 4K frames is tens of megabytes rather than gigabytes.
    /// </remarks>
    private const int Remembered = 24;

    private static readonly Lock Sync = new();

    /// <summary>
    /// What has already been produced, keyed by the file as it was when it was read.
    /// </summary>
    /// <remarks>
    /// The size and the write time are part of the key, so a file that changed under a
    /// preview is produced again rather than shown as it used to be.
    /// </remarks>
    private static readonly Dictionary<(string Path, long Length, long Written), TuiPixels?> Cache = [];

    private static readonly LinkedList<(string Path, long Length, long Written)> Order = new();

    /// <summary>
    /// What produces pixels for a file, in the order they are tried.
    /// </summary>
    /// <remarks>
    /// <c>-[1]</c> and <c>[0]</c> say "the first page" and "the first frame": without them
    /// a multi-page PDF writes one file per page and a video writes every frame it has.
    /// </remarks>
    private static readonly (string[] Extensions, string Tool, string[] Arguments)[] Tools =
    [
        ([".pdf"], "pdftoppm", ["-f", "1", "-l", "1", "-r", "96", "-", "-"]),
        ([".mp4", ".mkv", ".webm", ".mov", ".avi", ".m4v"],
            "ffmpeg", ["-loglevel", "quiet", "-i", "{path}", "-frames:v", "1", "-f", "image2pipe", "-vcodec", "ppm", "-"]),
        ([], "magick", ["{path}[0]", "-auto-orient", "ppm:-"]),
        ([], "convert", ["{path}[0]", "-auto-orient", "ppm:-"]),
        ([], "ffmpeg", ["-loglevel", "quiet", "-i", "{path}", "-frames:v", "1", "-f", "image2pipe", "-vcodec", "ppm", "-"]),
    ];

    /// <summary>Reads a file as pixels, or answers null if nothing here can.</summary>
    /// <remarks>
    /// Null rather than an exception for every kind of failure — no tool installed, a
    /// format none of them know, a file that is not an image at all. A preview pane asked
    /// to preview a binary should say it cannot, not end the screen.
    /// </remarks>
    public static TuiPixels? Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        FileInfo file;

        try
        {
            file = new FileInfo(path);

            if (!file.Exists)
            {
                return null;
            }
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException)
        {
            return null;
        }

        var key = (file.FullName, file.Length, file.LastWriteTimeUtc.Ticks);

        lock (Sync)
        {
            if (Cache.TryGetValue(key, out var remembered))
            {
                // Remembered even when it was null: a file nothing here can read is worth
                // not trying five tools on again every time the reader arrows past it.
                Touch(key);
                return remembered;
            }
        }

        var produced = Produce(path);

        lock (Sync)
        {
            Cache[key] = produced;
            Touch(key);

            while (Order.Count > Remembered && Order.Last is { } oldest)
            {
                Cache.Remove(oldest.Value);
                Order.RemoveLast();
            }
        }

        return produced;
    }

    /// <summary>Moves a key to the front of the queue of what to keep.</summary>
    private static void Touch((string Path, long Length, long Written) key)
    {
        for (var node = Order.First; node is not null; node = node.Next)
        {
            if (node.Value == key)
            {
                Order.Remove(node);
                break;
            }
        }

        Order.AddFirst(key);
    }

    private static TuiPixels? Produce(string path)
    {
        var extension = Path.GetExtension(path);

        foreach (var (extensions, tool, arguments) in Tools)
        {
            var matches = extensions.Length == 0 ||
                extensions.Contains(extension, StringComparer.OrdinalIgnoreCase);

            if (matches && Run(tool, arguments, path) is { } pixels)
            {
                return pixels;
            }
        }

        return null;
    }

    private static TuiPixels? Run(string tool, string[] arguments, string path)
    {
        var start = new ProcessStartInfo(tool)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };

        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument.Replace("{path}", path, StringComparison.Ordinal));
        }

        try
        {
            using var process = Process.Start(start);

            if (process is null)
            {
                return null;
            }

            // `pdftoppm` reads the document from its input rather than from a name, which
            // is what the lone `-` in its arguments means.
            using (var input = process.StandardInput.BaseStream)
            {
                if (arguments.Contains("-") && !arguments.Any(argument => argument.Contains("{path}", StringComparison.Ordinal)))
                {
                    using var file = File.OpenRead(path);
                    file.CopyTo(input);
                }
            }

            using var buffered = new MemoryStream();

            process.StandardOutput.BaseStream.CopyTo(buffered);

            if (!process.WaitForExit(Patience))
            {
                process.Kill(entireProcessTree: true);
                return null;
            }

            buffered.Position = 0;

            return process.ExitCode == 0 ? TuiPixels.FromPpm(buffered) : null;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException
                                              and not StackOverflowException)
        {
            // A tool that is not installed is the ordinary case, not a fault.
            return null;
        }
    }
}
