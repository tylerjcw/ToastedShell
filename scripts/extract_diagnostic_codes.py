#!/usr/bin/env python3
"""
Extract diagnostic codes from the TōSh source tree.

Scans every `*.cs` file under `src/` for `"tosh::..."` string literals and the
adjacent `Title`/`title`/`Help`/`help` fields, then emits:

    docs/diagnostic-codes.md         — human-readable Markdown reference
    docs/spec/diagnostic-codes.tex   — LaTeX appendix for the ToastScript spec

Run from the repo root:

    python3 scripts/extract_diagnostic_codes.py

The output files are regenerated in place. To customise paths, pass
`--src`, `--md`, `--tex` (see --help).
"""

from __future__ import annotations

import argparse
import re
import sys
from collections import defaultdict
from dataclasses import dataclass, field
from pathlib import Path

CODE_RE = re.compile(r'"(tosh\.[a-z_]+(?:\.[a-z_]+)+)"')
# Match Title/title/Help/help: "..." or = "..."
FIELD_RE = re.compile(
    r'(?P<key>Code|code|Title|title|Help|help|Message|message|Label|label|Severity|severity)'
    r'\s*[:=]\s*'
    r'(?P<value>(?:\$?"(?:[^"\\]|\\.)*")|(?:\w+))'
)
# Detect when a code's containing call uses interpolated/identifier values
# (we only render plain string literals; interpolations get a generic placeholder).
INTERPOLATED_RE = re.compile(r'^\$"')


@dataclass
class Site:
    file: str
    line: int


@dataclass
class CodeEntry:
    code: str
    namespace: str
    name: str
    sites: list[Site] = field(default_factory=list)
    title: str | None = None
    help: str | None = None
    severity: str | None = None

    def add_site(self, site: Site) -> None:
        self.sites.append(site)


def unquote(literal: str) -> str | None:
    """Return the raw content of a C# string literal, or None if interpolated/non-literal."""
    if INTERPOLATED_RE.match(literal):
        # Interpolated string — strip leading $ and quotes, but caller may want to mark it.
        literal = literal[1:]
    if not (literal.startswith('"') and literal.endswith('"')):
        return None
    body = literal[1:-1]
    # Decode the common escapes; we don't need full C# unescape fidelity.
    return (body
            .replace(r'\"', '"')
            .replace(r'\\', '\\')
            .replace(r'\n', ' ')
            .replace(r'\t', ' ')
            .replace(r'\r', ' '))


def extract_from_file(path: Path, entries: dict[str, CodeEntry]) -> None:
    text = path.read_text(encoding="utf-8", errors="replace")
    lines = text.splitlines()
    # Walk every code occurrence.
    for m in CODE_RE.finditer(text):
        code = m.group(1)
        # Locate line number (1-based).
        line_no = text.count("\n", 0, m.start()) + 1
        ns_parts = code.split(".")
        # tosh.<ns>.<name...>  ⇒ ns is index 1, name is the rest joined.
        ns = ns_parts[1] if len(ns_parts) >= 3 else "?"
        name = ".".join(ns_parts[2:]) if len(ns_parts) >= 3 else ns_parts[-1]
        entry = entries.get(code)
        if entry is None:
            entry = CodeEntry(code=code, namespace=ns, name=name)
            entries[code] = entry
        entry.add_site(Site(file=str(path), line=line_no))

        # Look at the next ~20 lines for adjacent Title/Help/Severity fields.
        # We only fill these in once (first encounter).
        if entry.title and entry.help and entry.severity:
            continue
        window_start = m.end()
        window_end = min(len(text), window_start + 1200)
        window = text[window_start:window_end]
        # Stop at the diagnostic constructor's closing `));` or `}` to avoid
        # leaking into adjacent code.
        terminators = [window.find("));"), window.find(")\n;"), window.find("\n        }\n")]
        terminators = [t for t in terminators if t >= 0]
        if terminators:
            window = window[: min(terminators) + 3]
        for fm in FIELD_RE.finditer(window):
            key = fm.group("key").lower()
            value = fm.group("value")
            if key in ("title", "message") and not entry.title:
                entry.title = unquote(value) or _interpolated_summary(value)
            elif key == "help" and not entry.help:
                entry.help = unquote(value) or _interpolated_summary(value)
            elif key == "severity" and not entry.severity:
                # Severity is usually an enum identifier (e.g., DiagnosticSeverity.Error).
                entry.severity = value if not value.startswith('"') else unquote(value)


def _interpolated_summary(literal: str) -> str | None:
    """For interpolated strings, strip $ and return the body as a hint."""
    if literal.startswith('$"') and literal.endswith('"'):
        return "(interpolated) " + literal[2:-1]
    return None


def collect_entries(src_root: Path) -> dict[str, CodeEntry]:
    entries: dict[str, CodeEntry] = {}
    for cs in sorted(src_root.rglob("*.cs")):
        # Skip build output and any auto-generated files. The C# manifest
        # we emit lives under `Generated/` and contains every diagnostic
        # code as a string literal in a constructor argument, which would
        # otherwise be re-scanned and overwrite real source-file/line
        # metadata on a second run. Skipping by directory name and by the
        # `<auto-generated />` marker covers both Roslyn-emitted files and
        # this script's own output.
        if any(part in ("obj", "bin", "Generated") for part in cs.parts):
            continue
        try:
            head = cs.read_text(encoding="utf-8", errors="replace")[:200]
        except OSError:
            head = ""
        if "<auto-generated" in head:
            continue
        extract_from_file(cs, entries)
    return entries


# ─── Emitters ──────────────────────────────────────────────────────────────

NAMESPACE_BLURBS = {
    "parser": "Raised by the lexer or parser before any code runs. Indicates malformed source text.",
    "runtime": "Raised by the engine while evaluating a script. The bulk of TōSh diagnostics live here.",
    "tui": "Raised by the `tui` subsystem (terminal UI widgets, screens, providers).",
    "config": "Raised by the configuration loader or by the `config` command.",
    "history": "Raised by the history subsystem (for example, replaying entries when no host is attached).",
    "help": "Raised by the `help` command.",
    "get": "Raised by the `get` command and friends.",
}


def _md_escape(s: str) -> str:
    return s.replace("|", "\\|").replace("\n", " ")


def emit_markdown(entries: dict[str, CodeEntry], repo_root: Path) -> str:
    by_ns: dict[str, list[CodeEntry]] = defaultdict(list)
    for entry in entries.values():
        by_ns[entry.namespace].append(entry)
    for lst in by_ns.values():
        lst.sort(key=lambda e: e.name)

    out: list[str] = []
    out.append("# TōSh Diagnostic Code Reference")
    out.append("")
    out.append("> Auto-generated by `scripts/extract_diagnostic_codes.py`.")
    out.append("> Re-run after adding new diagnostics to the engine, parser, or commands.")
    out.append("")
    out.append("Every diagnostic raised by TōSh carries a stable `Code:` string of the form")
    out.append("`tosh.<namespace>.<name>`. User code can match on these in `try`/`catch`")
    out.append("blocks and tooling (LSP, MCP, editor extensions) keys off them. Suppress")
    out.append("a non-error diagnostic with `hush <code>` (scope-local) or by adding it to")
    out.append("`$tosh.Config.Diagnostics.Hushed` from `profile.tosh`. The tables below")
    out.append("enumerate every code currently emitted by the implementation.")
    out.append("")
    total = sum(len(lst) for lst in by_ns.values())
    out.append(f"**Total diagnostic codes:** {total}")
    out.append("")
    out.append("## Namespace summary")
    out.append("")
    out.append("| Namespace | Count | Purpose |")
    out.append("|---|---:|---|")
    for ns in sorted(by_ns):
        blurb = NAMESPACE_BLURBS.get(ns, "—")
        out.append(f"| `tosh.{ns}.*` | {len(by_ns[ns])} | {blurb} |")
    out.append("")

    for ns in sorted(by_ns):
        out.append(f"## `tosh.{ns}.*`")
        out.append("")
        if ns in NAMESPACE_BLURBS:
            out.append(NAMESPACE_BLURBS[ns])
            out.append("")
        out.append("| Code | Title | First emit site |")
        out.append("|---|---|---|")
        for entry in by_ns[ns]:
            title = _md_escape(entry.title) if entry.title else "_(see source)_"
            site = entry.sites[0]
            rel = Path(site.file).resolve().relative_to(repo_root)
            site_link = f"[{rel}:{site.line}]({rel}#L{site.line})"
            out.append(f"| `{entry.code}` | {title} | {site_link} |")
        out.append("")

    return "\n".join(out) + "\n"


# LaTeX emit ─────────────────────────────────────────────────────────────────

def _tex_escape(s: str) -> str:
    return (s
            .replace("\\", r"\textbackslash{}")
            .replace("&", r"\&")
            .replace("%", r"\%")
            .replace("$", r"\$")
            .replace("#", r"\#")
            .replace("_", r"\_")
            .replace("{", r"\{")
            .replace("}", r"\}")
            .replace("~", r"\textasciitilde{}")
            .replace("^", r"\textasciicircum{}"))


def emit_latex(entries: dict[str, CodeEntry]) -> str:
    by_ns: dict[str, list[CodeEntry]] = defaultdict(list)
    for entry in entries.values():
        by_ns[entry.namespace].append(entry)
    for lst in by_ns.values():
        lst.sort(key=lambda e: e.name)

    out: list[str] = []
    out.append("% Auto-generated by scripts/extract_diagnostic_codes.py — do not edit by hand.")
    out.append("\\chapter{Diagnostic Code Reference}")
    out.append("\\label{ch:diagnostic-codes}")
    out.append("")
    out.append("Every diagnostic raised by TōSh carries a stable identifier of the")
    out.append("form \\texttt{tosh.namespace.name}. User code may pattern-match on these")
    out.append("identifiers in \\textbf{try}/\\textbf{catch} blocks; tooling such as the")
    out.append("language server, the MCP server, and editor extensions key off them.")
    out.append("Non-error diagnostics can be suppressed with the \\texttt{hush} builtin")
    out.append("(scope-local) or by adding their codes to")
    out.append("\\texttt{\\$tosh.Config.Diagnostics.Hushed}.")
    out.append("")
    total = sum(len(lst) for lst in by_ns.values())
    out.append(f"This chapter lists all {total} diagnostic codes currently emitted by the")
    out.append("implementation, grouped by namespace. It is generated from the source tree")
    out.append("by \\texttt{scripts/extract\\_diagnostic\\_codes.py} and should be")
    out.append("regenerated whenever new diagnostics are added.")
    out.append("")
    out.append("\\section*{Namespace summary}")
    out.append("\\begin{tabular}{l r p{0.55\\linewidth}}")
    out.append("\\toprule")
    out.append("\\textbf{Namespace} & \\textbf{Count} & \\textbf{Purpose} \\\\")
    out.append("\\midrule")
    for ns in sorted(by_ns):
        blurb = NAMESPACE_BLURBS.get(ns, "---")
        out.append(f"\\texttt{{tosh.{_tex_escape(ns)}.*}} & {len(by_ns[ns])} & {_tex_escape(blurb)} \\\\")
    out.append("\\bottomrule")
    out.append("\\end{tabular}")
    out.append("")

    for ns in sorted(by_ns):
        out.append(f"\\section{{\\texttt{{tosh.{_tex_escape(ns)}.*}}}}")
        if ns in NAMESPACE_BLURBS:
            out.append(_tex_escape(NAMESPACE_BLURBS[ns]))
            out.append("")
        # One "card" per diagnostic: code on its own line, title beneath. Avoids the
        # column-overlap that long codes + long titles caused in the old longtable
        # layout. `style=nextline` (enumitem) places the description on the line
        # following the label rather than wrapping awkwardly to the right of it.
        out.append("\\begin{description}[style=nextline,leftmargin=0pt,labelindent=0pt,"
                   "labelsep=0pt,labelwidth=0pt,"
                   "itemsep=0.35em,parsep=0pt,topsep=0.4em]")
        for entry in by_ns[ns]:
            code = _tex_escape(entry.code)
            title = _tex_escape(entry.title) if entry.title else "\\textit{(see source)}"
            # Allow long codes to break sensibly inside \texttt by wrapping in a
            # \sloppy-friendly group.
            out.append(f"\\item[\\texttt{{\\small {code}}}] {title}")
        out.append("\\end{description}")
        out.append("")

    return "\n".join(out) + "\n"


# ─── Main ──────────────────────────────────────────────────────────────────

def _csharp_escape(s: str) -> str:
    return (s.replace("\\", "\\\\")
             .replace("\"", "\\\""))


def emit_csharp(entries: dict[str, CodeEntry], repo_root: Path) -> str:
    """Emit a static C# manifest under `Tosh.Runtime.Generated.DiagnosticCodeManifest`."""
    out: list[str] = []
    out.append("// <auto-generated />")
    out.append("// Generated by scripts/extract_diagnostic_codes.py — do not edit by hand.")
    out.append("// Re-run after adding diagnostics:")
    out.append("//     python3 scripts/extract_diagnostic_codes.py")
    out.append("#nullable enable")
    out.append("")
    out.append("using System.Collections.Generic;")
    out.append("")
    out.append("namespace Tosh.Runtime.Generated;")
    out.append("")
    out.append("/// <summary>Single diagnostic code entry, generated from source scan.</summary>")
    out.append("public sealed record DiagnosticCodeInfo(")
    out.append("    string Code,")
    out.append("    string Namespace,")
    out.append("    string Name,")
    out.append("    string Title,")
    out.append("    string? Help,")
    out.append("    string SourceFile,")
    out.append("    int SourceLine);")
    out.append("")
    out.append("/// <summary>Static index of every diagnostic code emitted by TōSh.</summary>")
    out.append("public static class DiagnosticCodeManifest")
    out.append("{")
    out.append("    private static readonly Dictionary<string, DiagnosticCodeInfo> _byCode =")
    out.append("        new(System.StringComparer.OrdinalIgnoreCase)")
    out.append("    {")
    for entry in sorted(entries.values(), key=lambda e: e.code):
        site = entry.sites[0]
        try:
            rel = Path(site.file).resolve().relative_to(repo_root)
        except ValueError:
            rel = Path(site.file)
        rel_str = str(rel).replace("\\", "/")
        title = entry.title or ""
        help_text = entry.help
        title_lit = f"\"{_csharp_escape(title)}\""
        help_lit = "null" if help_text is None else f"\"{_csharp_escape(help_text)}\""
        out.append(f"        [\"{entry.code}\"] = new DiagnosticCodeInfo(")
        out.append(f"            Code: \"{entry.code}\",")
        out.append(f"            Namespace: \"{entry.namespace}\",")
        out.append(f"            Name: \"{_csharp_escape(entry.name)}\",")
        out.append(f"            Title: {title_lit},")
        out.append(f"            Help: {help_lit},")
        out.append(f"            SourceFile: \"{_csharp_escape(rel_str)}\",")
        out.append(f"            SourceLine: {site.line}),")
    out.append("    };")
    out.append("")
    out.append("    /// <summary>Total number of diagnostic codes in the manifest.</summary>")
    out.append(f"    public const int Count = {len(entries)};")
    out.append("")
    out.append("    /// <summary>Lookup metadata for a single code (case-insensitive).</summary>")
    out.append("    public static DiagnosticCodeInfo? TryGet(string code)")
    out.append("        => _byCode.TryGetValue(code, out var info) ? info : null;")
    out.append("")
    out.append("    /// <summary>All diagnostic codes ordered by code.</summary>")
    out.append("    public static IReadOnlyCollection<DiagnosticCodeInfo> All => _byCode.Values;")
    out.append("}")
    return "\n".join(out) + "\n"


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--src", default="src", help="Source root (default: src)")
    parser.add_argument("--md", default="docs/diagnostic-codes.md",
                        help="Output path for Markdown reference")
    parser.add_argument("--tex", default="docs/spec/diagnostic-codes.tex",
                        help="Output path for LaTeX appendix")
    parser.add_argument("--json", default=None,
                        help="Optional JSON manifest output path")
    parser.add_argument("--csharp",
                        default="src/Tosh.Runtime/Generated/DiagnosticCodeManifest.g.cs",
                        help="Output path for the C# manifest "
                             "(a static class with a code → metadata dictionary)")
    parser.add_argument("--quiet", action="store_true")
    args = parser.parse_args(argv)

    repo_root = Path.cwd().resolve()
    src_root = (repo_root / args.src).resolve()
    if not src_root.is_dir():
        print(f"error: source root not found: {src_root}", file=sys.stderr)
        return 2

    entries = collect_entries(src_root)
    if not entries:
        print("error: no diagnostic codes found", file=sys.stderr)
        return 1

    md_path = (repo_root / args.md).resolve()
    md_path.parent.mkdir(parents=True, exist_ok=True)
    md_path.write_text(emit_markdown(entries, repo_root), encoding="utf-8")

    tex_path = (repo_root / args.tex).resolve()
    tex_path.parent.mkdir(parents=True, exist_ok=True)
    tex_path.write_text(emit_latex(entries), encoding="utf-8")

    if args.json:
        import json
        json_path = (repo_root / args.json).resolve()
        json_path.parent.mkdir(parents=True, exist_ok=True)
        manifest = {
            "total": len(entries),
            "codes": [
                {
                    "code": e.code,
                    "namespace": e.namespace,
                    "name": e.name,
                    "title": e.title,
                    "help": e.help,
                    "severity": e.severity,
                    "sites": [{"file": s.file, "line": s.line} for s in e.sites],
                }
                for e in sorted(entries.values(), key=lambda x: x.code)
            ],
        }
        json_path.write_text(json.dumps(manifest, indent=2), encoding="utf-8")

    if args.csharp:
        cs_path = (repo_root / args.csharp).resolve()
        cs_path.parent.mkdir(parents=True, exist_ok=True)
        cs_path.write_text(emit_csharp(entries, repo_root), encoding="utf-8")

    if not args.quiet:
        print(f"Extracted {len(entries)} diagnostic codes")
        print(f"  Markdown: {md_path}")
        print(f"  LaTeX:    {tex_path}")
        if args.json:
            print(f"  JSON:     {(repo_root / args.json).resolve()}")
        if args.csharp:
            print(f"  C#:       {(repo_root / args.csharp).resolve()}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
