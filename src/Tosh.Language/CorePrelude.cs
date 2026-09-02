namespace Tosh.Language;

/// <summary>
/// Core type declarations loaded when the engine initializes — <c>TOAST-0083</c>.
/// </summary>
/// <remarks>
/// <para>
/// These are ordinary ToastScript unions rather than CLR types registered beside <c>Error</c>,
/// which was the decision taken on 2026-08-29. As unions they inherit pattern matching,
/// exhaustiveness checking, the <c>::</c> path operator and serialisation with no special case
/// anywhere in the evaluator; a CLR implementation would have needed
/// <c>TryDescribePatternSubject</c> and the exhaustiveness checker taught a second shape.
/// </para>
/// <para>
/// Loaded exactly as <see cref="BuiltinRunes"/> is, and for the same reason: this is what a
/// prelude is, and the engine already had one. A user declaration of the same name shadows
/// these and is warned about — resolution follows the rule the parser already documents, that a
/// bare name is where a declaration should win.
/// </para>
/// <para>
/// <b>The failure variant is <c>Err</c>, not <c>Error</c>.</b> The item's acceptance text says
/// <c>Error</c>, but <c>Error</c> is already a core type name — the base class the resolver
/// maps for <c>extends Error</c> — so <c>Result.Error(new Error(…))</c> would put two unrelated
/// meanings of one word in a single expression. The item's surface was explicitly marked as
/// illustrative rather than binding.
/// </para>
/// </remarks>
internal static class CorePrelude
{
    /// <summary>
    /// The type names this prelude declares, so a user declaration of one can be reported as
    /// shadowing it. Kept beside the source rather than derived from it: the set is small and a
    /// parse at startup to recover what is written three lines below would be work for nothing.
    /// </summary>
    public static readonly IReadOnlySet<string> TypeNames =
        new HashSet<string>(StringComparer.Ordinal) { "Option", "Result" };

    public const string Source = """
        # Option<T>: a value that may be absent.
        #
        # For absence the API expects its caller to handle. Unexpected failure stays with
        # exceptions, which remain the shell-friendly path through a pipeline.
        union Option<T> {
            Some(T)
            None()
        }

        # Result<T, E>: a value, or the reason there isn't one.
        #
        # `Err` rather than `Error`: `Error` already names the base class user error types
        # extend, and one word should not mean two things in `Result.Err(new Error("x"))`.
        union Result<T, E> {
            Ok(T)
            Err(E)
        }

        # Combinators live in `extend` blocks rather than the union bodies: a union body takes
        # variants only, and this is the form a user has for adding to any union, so the core
        # types are built out of the same material as everything else.
        extend Option {
            func is-some() {
                return (match ($this) {
                    Some(v) => true
                    default => false
                })
            }

            func is-none() {
                return (not ($this.is-some()))
            }

            # The value, or `fallback` when there is none.
            func unwrap-or(fallback) {
                return (match ($this) {
                    Some(v) => $v
                    default => $fallback
                })
            }

            # `f` over the value, leaving absence alone.
            func map(f) {
                return (match ($this) {
                    Some(v) => Option::Some($f($v))
                    default => $this
                })
            }

            # `f` returns an Option of its own, so chains do not nest.
            func and-then(f) {
                return (match ($this) {
                    Some(v) => $f($v)
                    default => $this
                })
            }

            # Runs `f` for its effect and returns the receiver unchanged, for a look inside a
            # chain without breaking it.
            func inspect(f) {
                match ($this) {
                    Some(v) => $f($v)
                    default => null
                }
                return $this
            }

            # The value, or `null` when there is none.
            #
            # `T?` and `Option<T>` are separate on purpose: `T?` says a slot may hold nothing,
            # `Option<T>` says absence is part of the domain. Neither converts into the other on
            # its own, so crossing between them is written where it happens.
            func or-null() {
                return (match ($this) {
                    Some(v) => $v
                    default => null
                })
            }
        }

        extend Result {
            func is-ok() {
                return (match ($this) {
                    Ok(v) => true
                    default => false
                })
            }

            func is-err() {
                return (not ($this.is-ok()))
            }

            func unwrap-or(fallback) {
                return (match ($this) {
                    Ok(v) => $v
                    default => $fallback
                })
            }

            # Maps the success and leaves the failure, which is the asymmetry that makes
            # `Result` worth having over a pair.
            #
            # `<dynamic, dynamic>` because a combinator cannot name the receiver's own type arguments:
            # `Ok(f(v))` fixes `T` and says nothing about `E`, and there is no spelling for "the
            # `E` this value already had". The alternative was refusing to construct at all.
            func map(f) {
                return (match ($this) {
                    Ok(v) => Result::Ok<dynamic, dynamic>($f($v))
                    default => $this
                })
            }

            func map-err(f) {
                return (match ($this) {
                    Err(e) => Result::Err<dynamic, dynamic>($f($e))
                    default => $this
                })
            }

            func and-then(f) {
                return (match ($this) {
                    Ok(v) => $f($v)
                    default => $this
                })
            }

            func inspect(f) {
                match ($this) {
                    Ok(v) => $f($v)
                    default => null
                }
                return $this
            }

            # Absence and failure are different things, so the conversion between them is
            # written rather than implied.
            func ok() {
                return (match ($this) {
                    Ok(v) => Option::Some($v)
                    default => Option::None<dynamic>()
                })
            }
        }

        # option-from: a nullable value as an Option.
        #
        # The other half of `or-null`. Spelled as a function rather than `Option::from` because
        # a union body takes variants only and `extend` adds instance methods, so there is no
        # place to hang a static on a union today.
        func option-from(value) {
            if ($value is null) {
                return Option::None<dynamic>()
            }

            return Option::Some($value)
        }

        # attempt: run a block and report its outcome as a Result rather than raising.
        #
        # The conversion at a deliberate boundary. Exceptions remain the shell-friendly path
        # for unexpected failure and for pipelines; this is for the failure a caller is
        # expected to handle, at the point the author decided to handle it.
        #
        # It does not consume control flow. `return`, `break`, `continue` and cancellation are
        # not exceptions this catches — they travel as `ShellControlFlowException`, which
        # `catch` already declines — so `attempt { return x }` returns from the enclosing
        # function rather than yielding `Ok`. That is verified rather than assumed; see
        # `CorePreludeTests`.
        rune attempt(body) {
            try {
                Result::Ok<dynamic, dynamic>($body)
            } catch (e) {
                Result::Err<dynamic, dynamic>($e)
            }
        }
        """;
}
