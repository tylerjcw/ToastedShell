namespace Tosh.Language;

/// <summary>
/// Built-in rune definitions that are loaded when the engine initializes.
/// These provide common macros like assert, dbg, unless, benchmark, and with-retry.
/// </summary>
internal static class BuiltinRunes
{
    public const string Source = """
        # dbg: print the source expression and its value, then pass through the value
        rune dbg(expr) {
            var __dbg_val = $expr
            var __dbg_src = (quote { $expr })
            echo $"[dbg] {$__dbg_src} = {$__dbg_val}" | to stderr
            $__dbg_val
        }

        # unless: execute the body only if the condition is false
        rune unless(condition, body) {
            if (not $condition) {
                $body
            }
        }

        # benchmark: time the execution of a block and print the duration
        rune benchmark(label, body) {
            var __bench_start = (date now)
            $body
            var __bench_end = (date now)
            var __bench_duration = ($__bench_end - $__bench_start)
            echo $"[benchmark] {$label}: {$__bench_duration}" | to stderr
        }

        # with-retry: retry a block up to N times on failure
        rune with-retry(attempts, body) {
            var __retry_remaining = $attempts
            var __retry_done = false
            while (not $__retry_done) {
                try {
                    $body
                    $__retry_done = true
                } catch (err) {
                    $__retry_remaining = ($__retry_remaining - 1)
                    if ($__retry_remaining <= 0) {
                        throw $err
                    }
                    echo $"[retry] attempt failed, {$__retry_remaining} remaining: {$err}" | to stderr
                }
            }
        }
        """;
}
