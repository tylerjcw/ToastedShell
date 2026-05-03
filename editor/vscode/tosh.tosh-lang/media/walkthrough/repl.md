# Interactive REPL

The TōSh REPL panel gives you an interactive scratchpad directly in the editor area.

- **Enter** — run the current expression
- **Shift+Enter** — insert a newline (multi-line input)
- **↑ / ↓** — navigate command history
- **Clear** — wipe the output
- **Restart** — start a fresh TōSh process

The REPL starts with `--no-profile` so your `profile.tosh` is not loaded — commands run in a clean environment.

```tosh
> 1 + 2
3
> "hello" | str upcase
HELLO
> [1, 2, 3] | each { |x| $x * $x }
[1, 4, 9]
```
