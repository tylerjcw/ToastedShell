using System.ComponentModel;

namespace Tosh.Runtime;

/// <summary>
/// Raised when a native call fails its declared success contract
/// (<c>-&gt; ok</c>, <c>-&gt; count</c>, or <c>-&gt; … where (…)</c>).
///
/// The property names here are not arbitrary: the engine's user-error
/// diagnostic contract is duck-typed, probing for <c>DiagnosticTitle</c>,
/// <c>Message</c>, <c>Title</c>, <c>Code</c> and <c>Label</c>. Exposing those
/// makes a native failure render exactly like a hand-written TōSh error class
/// with no engine changes at all.
/// </summary>
public class NativeError : ToshError
{
    public NativeError(
        string library,
        string symbol,
        object? result,
        int errno)
        : base(BuildMessage(symbol, errno))
    {
        Library = library;
        Symbol = symbol;
        Result = result;
        Errno = errno;
        ErrnoName = LookupErrnoName(errno);
    }

    /// <summary>
    /// Diagnostic code. Constant, because a code is an identifier and not a message.
    /// </summary>
    /// <remarks>
    /// This built the code from the library path and symbol, so a failure in
    /// <c>/home/…/lib/libprobe.so</c> reported
    /// <c>tosh.native./home/…/lib/libprobe.so.may_fail</c>. A code is what
    /// <c>hush</c> matches on and what the diagnostic reference documents, so one
    /// containing an absolute path could not be hushed portably, could not be
    /// documented, and differed between machines for the same failure
    /// (<c>TS-P3-25</c>).
    ///
    /// The engine prefixes <c>tosh.native.</c>, so the rendered code is
    /// <c>tosh.native.call_failed</c>. Nothing is lost: the library and symbol are
    /// already in <see cref="ToshError.Message"/> and <see cref="Help"/>, which is
    /// where a reader looks for them.
    /// </remarks>
    public string Code => "call_failed";

    public string DiagnosticTitle => Message;

    public string Label => Result is null
        ? "native call failed"
        : $"native call returned {Result}";

    public string Help => Errno == 0
        ? $"{Symbol} in {Library} reported failure without setting errno"
        : $"errno {Errno} ({ErrnoName}) from {Symbol} in {Library}";

    /// <summary>The C <c>errno</c> captured immediately after the call.</summary>
    public int Errno { get; }

    /// <summary>Symbolic name, e.g. <c>ENOMEM</c>. <c>"0"</c> when unset.</summary>
    public string ErrnoName { get; }

    /// <summary>The exported symbol that failed.</summary>
    public string Symbol { get; }

    /// <summary>The library it was bound from.</summary>
    public string Library { get; }

    /// <summary>The raw value the call returned, before the success contract rejected it.</summary>
    public object? Result { get; }

    private static string BuildMessage(string symbol, int errno)
    {
        if (errno == 0)
        {
            return $"{symbol} failed";
        }

        // Win32Exception maps to strerror on Unix, so there is no reason to bind
        // libc's own strerror — and doing so would mean re-entering the native
        // boundary from inside a failure path.
        var detail = new Win32Exception(errno).Message;
        return $"{symbol} failed: {detail}";
    }

    /// <summary>
    /// The symbolic name is what makes the diagnostic readable — "errno 12" says
    /// much less than "ENOMEM". Linux/POSIX values; the numeric fallback keeps
    /// anything unlisted honest rather than guessing.
    /// </summary>
    private static string LookupErrnoName(int errno) => errno switch
    {
        0 => "0",
        1 => "EPERM",
        2 => "ENOENT",
        3 => "ESRCH",
        4 => "EINTR",
        5 => "EIO",
        6 => "ENXIO",
        7 => "E2BIG",
        8 => "ENOEXEC",
        9 => "EBADF",
        10 => "ECHILD",
        11 => "EAGAIN",
        12 => "ENOMEM",
        13 => "EACCES",
        14 => "EFAULT",
        15 => "ENOTBLK",
        16 => "EBUSY",
        17 => "EEXIST",
        18 => "EXDEV",
        19 => "ENODEV",
        20 => "ENOTDIR",
        21 => "EISDIR",
        22 => "EINVAL",
        23 => "ENFILE",
        24 => "EMFILE",
        25 => "ENOTTY",
        26 => "ETXTBSY",
        27 => "EFBIG",
        28 => "ENOSPC",
        29 => "ESPIPE",
        30 => "EROFS",
        31 => "EMLINK",
        32 => "EPIPE",
        33 => "EDOM",
        34 => "ERANGE",
        36 => "ENAMETOOLONG",
        38 => "ENOSYS",
        39 => "ENOTEMPTY",
        40 => "ELOOP",
        62 => "ETIME",
        75 => "EOVERFLOW",
        84 => "EILSEQ",
        88 => "ENOTSOCK",
        95 => "EOPNOTSUPP",
        98 => "EADDRINUSE",
        104 => "ECONNRESET",
        110 => "ETIMEDOUT",
        111 => "ECONNREFUSED",
        _ => errno.ToString(),
    };
}
