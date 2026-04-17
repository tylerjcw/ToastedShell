# ToSh Cheat Sheets

Two-page LaTeX cheat sheets for the ToSh language and shell, designed so each topic prints cleanly front/back in landscape format.

These sheets were derived from the language spec in `docs/spec/`, then cross-checked against current examples, parser behavior, and tests so the syntax matches the implementation that ships today. Each topic aims to use both sides well without turning the layout into a wall of text.

Topics:

- `basics/` - REPL usage, help, capture forms, redirection, jobs, and shell workflows
- `syntax/` - expressions, variables, functions, control flow, and pattern matching
- `control-flow/` - branching, loops, errors, cleanup, and `match` / `switch`
- `collections/` - arrays, sets, tuples, records, typed collections, and pipeline transforms
- `filesystem/` - navigation, discovery, file mutation, and managed file handles
- `units/` - storage sizes, dates, durations, temporal amounts, and the quantity system
- `interop/` - modules, records, enums, classes, CLR interop, reflection, and native interop basics

Build everything:

```bash
make -C docs/cheatsheets
```

Clean auxiliary files:

```bash
make -C docs/cheatsheets clean
```
