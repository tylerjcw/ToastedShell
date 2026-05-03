# Writing Scripts

Create a `.tosh` file anywhere in your workspace. The extension provides:

- **Syntax highlighting** — via TextMate grammar + semantic tokens from the LSP
- **Hover documentation** — hover any built-in command to see its signature and examples
- **Completions** — `$` triggers variable completions; bare words trigger command completions
- **▶ Run / Debug** — buttons appear in the editor title bar and as CodeLens above the file

To run the current file, click **▶** in the top-right of the editor, or use **TōSh: Run TōSh Script** from the Command Palette.

```tosh
# greet.tosh
fn greet [name: string] {
    echo $"Hello, ($name)!"
}

greet "world"
```
