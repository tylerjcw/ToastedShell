# TōSh GtkSourceView Support

This directory contains the GtkSourceView language specification for TōSh.
GtkSourceView-based editors can use it for syntax highlighting in editors such
as gedit, Mousepad, Xed, and other GTK text editors.

User-local install:

```bash
for version in 2.0 3.0 4 5; do
  install -Dm644 editor/gtksourceview/tosh.lang \
    "$HOME/.local/share/gtksourceview-$version/language-specs/tosh.lang"
done
```

The spec matches `*.tosh` and `text/x-tosh`. Extensionless shebang scripts are
recognized when the desktop MIME database maps them to `text/x-tosh`.
