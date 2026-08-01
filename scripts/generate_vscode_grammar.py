#!/usr/bin/env python3
"""Regenerate the VS Code TextMate grammar from the live command metadata.

Usage:
    python3 scripts/generate_vscode_grammar.py [output.tmLanguage.json]

The built-in command alternation is derived by asking the CLI for its command
metadata, so the grammar cannot drift from the commands the runtime actually
ships. Everything else -- keywords, modifiers, type aliases, doc-comment tags --
is declared below and should be kept in step with docs/spec/toastscript-spec.tex.
"""
import json
import shutil
import subprocess
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
DEFAULT_OUTPUT = (REPO_ROOT / "editor" / "vscode" / "tosh.tosh-lang"
                  / "syntaxes" / "tosh.tmLanguage.json")


def load_builtin_names() -> str:
    """Ordered alternation of every built-in command name and alias.

    Sorted longest-first so an ordered regex alternation cannot let `read`
    shadow `read-file`.
    """
    candidates = [
        REPO_ROOT / "src" / "Tosh.Cli" / "bin" / "Debug" / "net10.0" / "Tosh.Cli",
        REPO_ROOT / "src" / "Tosh.Cli" / "bin" / "Release" / "net10.0" / "Tosh.Cli",
    ]
    exe = next((str(c) for c in candidates if c.exists()), shutil.which("tosh"))
    if exe is None:
        raise SystemExit(
            "No tosh binary found. Build src/Tosh.Cli or put tosh on PATH.")

    raw = subprocess.run([exe, "--export-command-metadata"],
                         capture_output=True, text=True, check=True).stdout
    names = set()
    for entry in json.loads(raw):
        names.add(entry["name"])
        names.update(entry.get("aliases") or [])
    return "|".join(sorted(names, key=lambda s: (-len(s), s)))


BUILTINS = load_builtin_names()

# Contextual + hard keywords, from docs/spec §Keywords and the parser.
CONTROL = (
    "if|else|unless|for|in|while|until|break|continue|return|yield|throw|try|catch|"
    "finally|defer|switch|case|default|match|when|handles|priority|once|func|var|const|"
    "alloc|class|interface|struct|trait|union|module|enum|record|rune|event|prop|type|"
    "coerce|subcommand|using|require|native|bind|from|as|out|ref|callconv|global|export|"
    "new|nameof|name-of|let|pick|quote|get|set|and|or|not|is|is-not|not-in|is-in|"
    "is-not-in|contains|starts-with|ends-with"
)

MODIFIERS = (
    "shy|private|proud|public|guarded|protected|local|sealed|hollow|abstract|partial|"
    "overrule|override|static|shared|hermit|strict|fluid|leaky|lazy|fixed|readonly|"
    "vital|required|fading|obsolete|raw|eager|hidden"
)

# Built-in type aliases (docs/spec §Built-in Type Aliases). Longest-first.
TYPE_ALIASES = sorted(
    ["string", "int", "long", "float", "double", "decimal", "bool", "byte", "sbyte",
     "short", "ushort", "uint", "ulong", "char", "object", "guid", "uri", "datetime",
     "datetimeoffset", "dateonly", "timeonly", "timespan", "regex", "list", "array",
     "dict", "hashtable", "set", "table", "dynamicrecord", "tuple", "file", "dir",
     "directory", "ptr", "ip", "ipaddress", "version", "Vector", "vec", "Matrix",
     "matrix", "mat", "Complex", "complex", "void", "any", "dynamic"],
    key=lambda s: (-len(s), s))
TYPE_ALIAS_RE = "|".join(TYPE_ALIASES)

DOC_TAGS = ("summary|remarks|returns|value|example|deprecated|seealso|see|since|"
            "exception|throws|para")

IDENT = "[A-Za-z_][A-Za-z0-9_-]*"
TYPE_REF = "[A-Za-z_][A-Za-z0-9_.<>?,-]*"

# Guard for command-position rules, whose matches start on the leading
# whitespace and would otherwise outrank a keyword match on the word itself.
NOT_KEYWORD = f"(?!(?:{CONTROL}|{MODIFIERS}|flag|arg|true|false|null|_)\\b)"

# Escapes accepted inside double-quoted / interpolated strings.
ESCAPE = "\\\\\\\\(?:[\\\\\\\\\"'nrtabefv0]|x[0-9a-fA-F]{1,2}|u[0-9a-fA-F]{1,4})"
ANSI_ESCAPE = ("\\\\\\\\(?:[\\\\\\\\\"'abefnrtv0]|[eE]|x[0-9a-fA-F]{1,2}|"
               "u[0-9a-fA-F]{1,4}|0[0-7]{1,3})")


def type_ref_captures(name="entity.name.type.tosh"):
    """Colour a captured type reference, preferring the built-in alias scope."""
    return {"patterns": [
        {"match": f"\\b(?:{TYPE_ALIAS_RE})\\b", "name": "support.type.builtin.tosh"},
        {"match": "[A-Za-z_][A-Za-z0-9_.]*", "name": name},
        {"match": "[<>]", "name": "punctuation.definition.generic.tosh"},
        {"match": "[,?]", "name": "punctuation.separator.tosh"},
    ]}


def definition(keyword, scope, modifiers=MODIFIERS):
    return {
        "match": f"\\b((?:(?:{modifiers}|global|export)\\s+)*)({keyword})\\s+({IDENT})",
        "captures": {
            "1": {"patterns": [{"include": "#declaration-modifiers"}]},
            "2": {"name": "keyword.control.tosh"},
            "3": {"name": scope},
        },
    }


def interpolation_patterns():
    return [
        {"name": "constant.character.escape.tosh", "match": "\\{\\{|\\}\\}"},
        {
            "comment": "Dollar-brace interpolation: ${expr}",
            "name": "meta.interpolation.tosh",
            "begin": "\\$\\{", "end": "\\}",
            "beginCaptures": {"0": {"name": "punctuation.section.interpolation.begin.tosh"}},
            "endCaptures": {"0": {"name": "punctuation.section.interpolation.end.tosh"}},
            "contentName": "source.tosh.embedded",
            "patterns": [{"include": "source.tosh"}],
        },
        {
            "comment": "Bare brace interpolation: {expr}",
            "name": "meta.interpolation.tosh",
            "begin": "(?<!\\{)\\{(?!\\{)", "end": "\\}",
            "beginCaptures": {"0": {"name": "punctuation.section.interpolation.begin.tosh"}},
            "endCaptures": {"0": {"name": "punctuation.section.interpolation.end.tosh"}},
            "contentName": "source.tosh.embedded",
            "patterns": [{"include": "source.tosh"}],
        },
        {
            "comment": "Bare variable interpolation: $name or $env.HOME",
            "match": "\\$[a-zA-Z_][a-zA-Z0-9_]*(?:\\.[a-zA-Z_][a-zA-Z0-9_]*)*",
            "name": "variable.other.readwrite.tosh",
        },
    ]


grammar = {
    "$schema": "https://raw.githubusercontent.com/martinring/tmlanguage/master/tmlanguage.json",
    "name": "TōSh",
    "scopeName": "source.tosh",
    "patterns": [{"include": f"#{n}"} for n in [
        "shebang", "block-comment", "doc-comment", "comment",
        "interpolated-triple-string", "interpolated-ansi-triple-string",
        "raw-triple-string", "ansi-triple-string",
        "interpolated-string", "ansi-c-string", "double-quoted-string", "single-quoted-string",
        "command-substitution", "process-substitution",
        "class-definition", "interface-definition", "struct-definition", "trait-definition",
        "union-definition", "rune-definition", "event-definition", "module-definition",
        "enum-definition", "record-definition", "type-definition", "subcommand-definition",
        "operator-definition", "function-definition", "subcommand-member",
        "type-clause", "redirection", "variable-declaration",
        # cast/new must precede control-flow: `new` and `is` are keywords too,
        # and control-flow would consume them and leave the type name bare.
        "cast-expression", "new-expression",
        "storage-modifier", "control-flow", "constants",
        "generic-arguments", "type-annotations", "parameter-name", "member-declaration",
        "spread-operator", "caret-escape", "function-reference",
        "special-variable", "variable-reference", "pipeline-underscore",
        "builtin-commands", "command-position", "collection-delimiters",
        "operators", "pipe", "numeric", "member-access", "flags", "punctuation",
    ]],
    "repository": {
        # ── Comments ──────────────────────────────────────────────────────
        "shebang": {
            "comment": "Only meaningful at byte offset zero.",
            "match": "\\A#!.*$",
            "name": "comment.line.shebang.tosh",
        },
        "block-comment": {
            "comment": "##{ ... }## block comment. Must precede #doc-comment.",
            "name": "comment.block.tosh",
            "begin": "##\\{", "end": "\\}##",
            "beginCaptures": {"0": {"name": "punctuation.definition.comment.begin.tosh"}},
            "endCaptures": {"0": {"name": "punctuation.definition.comment.end.tosh"}},
        },
        "doc-comment": {
            "patterns": [
                {
                    "comment": "Doc comment with a named tag: @param=name / @typeparam=name",
                    "name": "comment.line.documentation.tosh",
                    "match": f"(##)\\s*(@(?:param|typeparam)=)({IDENT})(.*)$",
                    "captures": {
                        "1": {"name": "punctuation.definition.comment.documentation.tosh"},
                        "2": {"name": "keyword.other.documentation.tosh"},
                        "3": {"name": "variable.parameter.tosh"},
                    },
                },
                {
                    "comment": "Doc comment with a block tag",
                    "name": "comment.line.documentation.tosh",
                    "match": f"(##)\\s*(@(?:{DOC_TAGS}))\\b(.*)$",
                    "captures": {
                        "1": {"name": "punctuation.definition.comment.documentation.tosh"},
                        "2": {"name": "keyword.other.documentation.tosh"},
                    },
                },
                {
                    "comment": "Plain doc comment",
                    "name": "comment.line.documentation.tosh",
                    "match": "(##).*$",
                    "captures": {
                        "1": {"name": "punctuation.definition.comment.documentation.tosh"},
                    },
                },
            ],
        },
        "comment": {
            "comment": (
                "A lone '#' opens a comment only when it stands alone as a word: at "
                "the start of a word and followed by whitespace or end of line. That "
                "is what leaves #ff0000, issue#42 and C# as ordinary barewords."
            ),
            "match": "(?<![^\\s])(#)(?=\\s|$).*$",
            "name": "comment.line.number-sign.tosh",
            "captures": {"1": {"name": "punctuation.definition.comment.tosh"}},
        },

        # ── Strings ───────────────────────────────────────────────────────
        "interpolated-triple-string": {
            "comment": '$"""..."""  — interpolated, multi-line, no escapes',
            "name": "string.quoted.triple.interpolated.tosh",
            "begin": '\\$"""', "end": '"""',
            "beginCaptures": {"0": {"name": "punctuation.definition.string.begin.tosh"}},
            "endCaptures": {"0": {"name": "punctuation.definition.string.end.tosh"}},
            "patterns": interpolation_patterns(),
        },
        "interpolated-ansi-triple-string": {
            "comment": "$'''...'''  — interpolated ANSI-C, multi-line",
            "name": "string.quoted.triple.ansi-c.interpolated.tosh",
            "begin": "\\$'''", "end": "'''",
            "beginCaptures": {"0": {"name": "punctuation.definition.string.begin.tosh"}},
            "endCaptures": {"0": {"name": "punctuation.definition.string.end.tosh"}},
            "patterns": [{"name": "constant.character.escape.tosh", "match": ANSI_ESCAPE}]
            + interpolation_patterns(),
        },
        "raw-triple-string": {
            "comment": '"""..."""  — raw, multi-line, no escapes or interpolation',
            "name": "string.quoted.triple.tosh",
            "begin": '"""', "end": '"""',
            "beginCaptures": {"0": {"name": "punctuation.definition.string.begin.tosh"}},
            "endCaptures": {"0": {"name": "punctuation.definition.string.end.tosh"}},
        },
        "ansi-triple-string": {
            "comment": "'''...'''  — ANSI-C escapes, multi-line, no interpolation",
            "name": "string.quoted.triple.ansi-c.tosh",
            "begin": "'''", "end": "'''",
            "beginCaptures": {"0": {"name": "punctuation.definition.string.begin.tosh"}},
            "endCaptures": {"0": {"name": "punctuation.definition.string.end.tosh"}},
            "patterns": [{"name": "constant.character.escape.tosh", "match": ANSI_ESCAPE}],
        },
        "interpolated-string": {
            "name": "string.quoted.double.interpolated.tosh",
            "begin": '\\$"', "end": '"',
            "beginCaptures": {"0": {"name": "punctuation.definition.string.begin.tosh"}},
            "endCaptures": {"0": {"name": "punctuation.definition.string.end.tosh"}},
            "patterns": [{"name": "constant.character.escape.tosh", "match": ESCAPE}]
            + interpolation_patterns(),
        },
        "ansi-c-string": {
            "name": "string.quoted.single.ansi-c.tosh",
            "begin": "\\$'", "end": "'",
            "beginCaptures": {"0": {"name": "punctuation.definition.string.begin.tosh"}},
            "endCaptures": {"0": {"name": "punctuation.definition.string.end.tosh"}},
            "patterns": [{"name": "constant.character.escape.tosh", "match": ANSI_ESCAPE}],
        },
        "double-quoted-string": {
            "name": "string.quoted.double.tosh",
            "begin": '"', "end": '"',
            "beginCaptures": {"0": {"name": "punctuation.definition.string.begin.tosh"}},
            "endCaptures": {"0": {"name": "punctuation.definition.string.end.tosh"}},
            "patterns": [{"name": "constant.character.escape.tosh", "match": ESCAPE}],
        },
        "single-quoted-string": {
            "comment": "Single-quoted strings are literal — no escapes or interpolation",
            "name": "string.quoted.single.tosh",
            "begin": "'", "end": "'",
            "beginCaptures": {"0": {"name": "punctuation.definition.string.begin.tosh"}},
            "endCaptures": {"0": {"name": "punctuation.definition.string.end.tosh"}},
        },

        # ── Embedded ──────────────────────────────────────────────────────
        "command-substitution": {
            "name": "meta.command-substitution.tosh",
            "begin": "\\$\\(", "end": "\\)",
            "beginCaptures": {"0": {"name": "punctuation.section.embedded.begin.tosh"}},
            "endCaptures": {"0": {"name": "punctuation.section.embedded.end.tosh"}},
            "patterns": [{"include": "source.tosh"}],
        },
        "process-substitution": {
            "comment": "Input <(...) and output >(...) process substitution",
            "name": "meta.process-substitution.tosh",
            "begin": "[<>]\\(", "end": "\\)",
            "beginCaptures": {"0": {"name": "punctuation.section.embedded.begin.tosh"}},
            "endCaptures": {"0": {"name": "punctuation.section.embedded.end.tosh"}},
            "patterns": [{"include": "source.tosh"}],
        },

        # ── Declarations ──────────────────────────────────────────────────
        "class-definition": definition("class", "entity.name.type.class.tosh"),
        "interface-definition": definition("interface", "entity.name.type.interface.tosh"),
        "struct-definition": definition("struct", "entity.name.type.struct.tosh"),
        "trait-definition": definition("trait", "entity.name.type.trait.tosh"),
        "union-definition": definition("union", "entity.name.type.union.tosh"),
        "rune-definition": definition("rune", "entity.name.type.rune.tosh"),
        "event-definition": definition("event", "entity.name.type.event.tosh"),
        "module-definition": definition("module", "entity.name.namespace.tosh"),
        "enum-definition": definition("enum", "entity.name.type.enum.tosh"),
        "record-definition": definition("record", "entity.name.type.record.tosh"),
        "type-definition": {
            "comment": "Refinement type alias:  type Port = int where (...)",
            "match": f"\\b((?:(?:{MODIFIERS}|global|export)\\s+)*)(type)\\s+({IDENT})\\s*(?==)",
            "captures": {
                "1": {"patterns": [{"include": "#declaration-modifiers"}]},
                "2": {"name": "keyword.control.tosh"},
                "3": {"name": "entity.name.type.tosh"},
            },
        },
        "subcommand-definition": {
            "comment": "subcommand blocks turn a script into a structured CLI",
            "match": f"\\b((?:(?:eager|hidden|hollow|vital)\\s+)*)(subcommand)\\s+({IDENT})",
            "captures": {
                "1": {"name": "storage.modifier.tosh"},
                "2": {"name": "keyword.control.tosh"},
                "3": {"name": "entity.name.function.subcommand.tosh"},
            },
        },
        "subcommand-member": {
            "comment": "flag / arg declarations inside a subcommand body",
            "match": f"^\\s*(flag|arg)\\s+({IDENT})",
            "captures": {
                "1": {"name": "keyword.control.tosh"},
                "2": {"name": "variable.parameter.tosh"},
            },
        },
        "operator-definition": {
            "comment": "Operator overloads: func +(o), func ==(o), func !=(o). "
                       "The name is punctuation, so #function-definition's "
                       "identifier pattern never matches these.",
            "match": f"\\b((?:(?:{MODIFIERS}|global|export)\\s+)*)(func)\\s*"
                     f"(\\*\\*|==|!=|<=|>=|=~|!~|//|\\+|-|\\*|/|%|<|>)\\s*(?=\\()",
            "captures": {
                "1": {"patterns": [{"include": "#declaration-modifiers"}]},
                "2": {"name": "keyword.control.tosh"},
                "3": {"name": "entity.name.function.operator.tosh"},
            },
        },
        "function-definition": {
            "match": f"\\b((?:(?:{MODIFIERS}|global|export)\\s+)*)(func)\\s+({IDENT})\\s*(?=<|\\(|=>|$)",
            "captures": {
                "1": {"patterns": [{"include": "#declaration-modifiers"}]},
                "2": {"name": "keyword.control.tosh"},
                "3": {"name": "entity.name.function.tosh"},
            },
        },
        "declaration-modifiers": {
            "comment": "Modifier run before a declaration keyword. `global` and "
                       "`export` are visibility keywords rather than storage "
                       "modifiers, so they need their own scope here.",
            "patterns": [
                {"include": "#storage-modifier"},
                {"match": "\\b(global|export)\\b", "name": "keyword.control.tosh"},
            ],
        },
        "generic-arguments": {
            "comment": "Type arguments glued to a name: Vector2D<T>, dict<string, int>. "
                       "The no-space lookbehind keeps `a < b` a comparison.",
            "match": "(?<=[A-Za-z0-9_])(<)([A-Za-z_][A-Za-z0-9_.,?\\s]*)(>)",
            "captures": {
                "1": {"name": "punctuation.definition.generic.begin.tosh"},
                "2": type_ref_captures(),
                "3": {"name": "punctuation.definition.generic.end.tosh"},
            },
        },
        "member-declaration": {
            "comment": "Field names in raw struct bodies and native bind signatures: "
                       "`uptime: long`, `sysname: cstring[65]`.",
            "match": f"^\\s*({NOT_KEYWORD}{IDENT})\\s*(?=:(?![:=]))",
            "captures": {"1": {"name": "variable.other.member.declaration.tosh"}},
        },
        "member-access": {
            "comment": "Dotted member access on a bareword receiver: "
                       "Info.raw_info.uptime. `$`-prefixed paths are already "
                       "consumed whole by #variable-reference.",
            "patterns": [
                {
                    "comment": "Capitalised receiver reads as a type or module",
                    "match": "\\b([A-Z][A-Za-z0-9_]*)(?=\\.[A-Za-z_])",
                    "name": "entity.name.type.tosh",
                },
                {
                    "match": "(?<=\\.)([A-Za-z_][A-Za-z0-9_]*)",
                    "name": "variable.other.member.tosh",
                },
            ],
        },
        "storage-modifier": {
            "match": f"\\b({MODIFIERS})\\b",
            "name": "storage.modifier.tosh",
        },
        "type-clause": {
            "comment": "extends, fulfills, implements, uses, where",
            "patterns": [
                {
                    "match": f"\\b(extends|fulfills|implements|uses)\\b\\s*({TYPE_REF})?",
                    "captures": {
                        "1": {"name": "keyword.control.inheritance.tosh"},
                        "2": {"name": "entity.other.inherited-class.tosh"},
                    },
                },
                {
                    "comment": "Generic constraint: where T: Numeric. The optional "
                               "group keeps a bare `where` (pipeline filter, "
                               "refinement clause) matching too.",
                    "match": "\\b(where)\\b(?:\\s+([A-Za-z_][A-Za-z0-9_]*)\\s*(?=:(?![:=])))?",
                    "captures": {
                        "1": {"name": "keyword.control.constraint.tosh"},
                        "2": {"name": "entity.name.type.parameter.tosh"},
                    },
                },
            ],
        },
        "control-flow": {
            "match": f"\\b({CONTROL})\\b",
            "name": "keyword.control.tosh",
        },
        "constants": {
            "match": "\\b(true|false|null)\\b",
            "name": "constant.language.tosh",
        },

        # ── Types ─────────────────────────────────────────────────────────
        "cast-expression": {
            "comment": "Type-test and cast operators:  $x as int,  $x is not string",
            "match": f"\\b(as|is-not|is)\\s+(not\\s+)?({TYPE_REF})",
            "captures": {
                "1": {"name": "keyword.operator.type.tosh"},
                "2": {"name": "keyword.operator.type.tosh"},
                "3": type_ref_captures(),
            },
        },
        "new-expression": {
            "match": f"\\b(new)\\s+({TYPE_REF})",
            "captures": {
                "1": {"name": "keyword.control.tosh"},
                "2": type_ref_captures(),
            },
        },
        "type-annotations": {
            "patterns": [
                {
                    "comment": "Parameter / property type annotation:  name: Type",
                    "match": "(?<=[a-zA-Z0-9_?])\\s*(:)\\s*([A-Za-z_][a-zA-Z0-9_.<>?]*)",
                    "captures": {
                        "1": {"name": "punctuation.separator.type.tosh"},
                        "2": type_ref_captures(),
                    },
                },
                {
                    "comment": "Return type annotation:  -> Type",
                    "match": "(->)\\s*([A-Za-z_][a-zA-Z0-9_.<>?]*)",
                    "captures": {
                        "1": {"name": "punctuation.separator.return-type.tosh"},
                        "2": type_ref_captures(),
                    },
                },
                {
                    "comment": "Static member access on a CLR type:  String.Join(...)",
                    "match": "\\b([A-Z][A-Za-z0-9_]*(?:\\.[A-Z][A-Za-z0-9_]*)*)(\\.)([A-Za-z_][A-Za-z0-9_]*)\\s*(?=\\()",
                    "captures": {
                        "1": {"name": "entity.name.type.tosh"},
                        "2": {"name": "punctuation.accessor.tosh"},
                        "3": {"name": "entity.name.function.member.tosh"},
                    },
                },
                {
                    "comment": "Typed variable declaration:  Type name = ...",
                    "match": "^\\s*([A-Z][a-zA-Z0-9_.<>]+)\\s+([a-zA-Z_][a-zA-Z0-9_]*)\\s*(?==)",
                    "captures": {
                        "1": {"name": "entity.name.type.tosh"},
                        "2": {"name": "variable.other.tosh"},
                    },
                },
            ],
        },
        "variable-declaration": {
            "match": f"\\b((?:(?:shy|private|proud|public|local|global|export)\\s+)*)"
                     f"(var|const|alloc|prop)\\s+([a-zA-Z_][a-zA-Z0-9_-]*)\\s*(?=:|=|$|\\s)",
            "captures": {
                "1": {"patterns": [{"include": "#declaration-modifiers"}]},
                "2": {"name": "keyword.control.tosh"},
                "3": {"name": "variable.other.tosh"},
            },
        },

        # ── Sigils ────────────────────────────────────────────────────────
        "spread-operator": {
            "comment": "Spread / splat:  ...$args",
            "match": "(\\.\\.\\.)(\\$[a-zA-Z_][a-zA-Z0-9_]*(?:\\.[a-zA-Z_][a-zA-Z0-9_]*)*)?",
            "captures": {
                "1": {"name": "keyword.operator.spread.tosh"},
                "2": {"name": "variable.other.readwrite.tosh"},
            },
        },
        "caret-escape": {
            "comment": "Force external command:  ^command",
            "match": "(\\^)(?=[a-zA-Z_$/])",
            "captures": {"1": {"name": "keyword.operator.caret-escape.tosh"}},
        },
        "function-reference": {
            "comment": "&name is a function reference at the start of an expression; a "
                       "trailing & backgrounds the pipeline.",
            "patterns": [
                {
                    "match": f"(?<![A-Za-z0-9_])(&)({IDENT})",
                    "captures": {
                        "1": {"name": "keyword.operator.function-reference.tosh"},
                        "2": {"name": "entity.name.function.tosh"},
                    },
                },
                {"match": "&\\s*$", "name": "keyword.operator.background.tosh"},
            ],
        },
        "special-variable": {
            "patterns": [
                {
                    "comment": "$tosh runtime namespace:  $tosh.Last.Result",
                    "match": "\\$tosh(?:\\.[A-Za-z_][A-Za-z0-9_]*)*",
                    "name": "support.variable.tosh",
                },
                {
                    "comment": "$env.NAME environment access",
                    "match": "(\\$env)(\\.)([A-Za-z_][A-Za-z0-9_]*)",
                    "captures": {
                        "1": {"name": "support.variable.tosh"},
                        "2": {"name": "punctuation.accessor.tosh"},
                        "3": {"name": "variable.other.environment.tosh"},
                    },
                },
                {"match": "\\$this\\b", "name": "variable.language.this.tosh"},
                {"match": "\\$value\\b", "name": "variable.language.tosh"},
                {
                    "comment": "Positional parameters in arrow functions:  $1, $2",
                    "match": "\\$[0-9]+",
                    "name": "variable.parameter.positional.tosh",
                },
            ],
        },
        "variable-reference": {
            "match": "\\$[a-zA-Z_][a-zA-Z0-9_-]*(?:\\.[a-zA-Z_][a-zA-Z0-9_]*)*",
            "name": "variable.other.readwrite.tosh",
        },
        "pipeline-underscore": {
            "patterns": [
                {
                    "comment": "_.Member access on the current pipeline item",
                    "match": "\\b(_)(\\.)([a-zA-Z_][a-zA-Z0-9_.()]*)",
                    "captures": {
                        "1": {"name": "variable.language.pipeline-item.tosh"},
                        "2": {"name": "punctuation.accessor.tosh"},
                        "3": {"name": "variable.other.member.tosh"},
                    },
                },
                {"match": "\\b_\\b", "name": "variable.language.pipeline-item.tosh"},
            ],
        },

        # ── Commands ──────────────────────────────────────────────────────
        "builtin-commands": {
            "comment": "Built-in commands, highlighted in command position. "
                       "Alternation is longest-first so 'read' cannot shadow 'read-file'.",
            "match": f"(?:^|(?<=[;{{(])|(?<=\\|)(?<!\\{{\\|)|(?<==>))"
                     f"\\s*{NOT_KEYWORD}\\b({BUILTINS})\\b",
            "captures": {"1": {"name": "support.function.builtin.tosh"}},
        },
        "command-position": {
            "comment": (
                "These matches begin at the whitespace before the word, so they start "
                "earlier than a keyword match on the word itself and would otherwise "
                "win the tie. NOT_KEYWORD keeps `{ return ...` and `for x in ...` from "
                "being scoped as command calls."
            ),
            "patterns": [
                {
                    "comment": "Command position: start of line, after ; { ( or =>, "
                               "or after a pipe that is not a {| record opener.",
                    "match": f"(?:^|(?<=[;{{(])|(?<=\\|)(?<!\\{{\\|)|(?<==>))"
                             f"\\s*({NOT_KEYWORD}{IDENT})(?=\\s|$|\\()",
                    "captures": {"1": {"name": "entity.name.function.call.tosh"}},
                },
            ],
        },
        "parameter-name": {
            "comment": "Parameter names in signatures — after '(' or ',', and after "
                       "the native binding modes 'out' and 'ref'.",
            "match": f"(?:(?<=[(,])|(?<=\\bout\\s)|(?<=\\bref\\s))"
                     f"\\s*({NOT_KEYWORD}{IDENT})\\s*(?=:(?![:=]))",
            "captures": {"1": {"name": "variable.parameter.tosh"}},
        },
        "collection-delimiters": {
            "comment": "Record {| |}, dictionary {% %} and set {: :} delimiters",
            "match": "\\{[|:%]|[|:%]\\}",
            "name": "punctuation.section.collection.tosh",
        },

        # ── Operators ─────────────────────────────────────────────────────
        "redirection": {
            "comment": "Named-stream redirection: out> o>> err> e> o+e> err+out>> and <<<",
            "patterns": [
                {
                    "match": "(?<![A-Za-z0-9_-])(?:out\\+err|err\\+out|o\\+e|e\\+o|out|err|o|e)>>?",
                    "name": "keyword.operator.redirect.tosh",
                },
                {"match": "<<<", "name": "keyword.operator.redirect.here-string.tosh"},
            ],
        },
        "operators": {
            "patterns": [
                {"match": "=>", "name": "keyword.operator.arrow.tosh"},
                {
                    "comment": "Comprehension separator: [body <| for x in ...]",
                    "match": "<\\|",
                    "name": "keyword.operator.comprehension.tosh",
                },
                {
                    "match": "\\*\\*=|//=|\\?\\?=|\\+=|-=|\\*=|/=|%=",
                    "name": "keyword.operator.assignment.compound.tosh",
                },
                {"match": "\\?\\?|\\?\\.", "name": "keyword.operator.null-coalescing.tosh"},
                {"match": "&&|\\|\\|", "name": "keyword.operator.logical.tosh"},
                {"match": "\\*\\*", "name": "keyword.operator.arithmetic.tosh"},
                {
                    "comment": "Floor division. Spaces required so paths keep their slashes.",
                    "match": "(?<=\\s)//(?=\\s)",
                    "name": "keyword.operator.arithmetic.tosh",
                },
                {"match": "\\.\\.", "name": "keyword.operator.range.tosh"},
                {"match": "!~|=~|!=|>=|<=|==", "name": "keyword.operator.comparison.tosh"},
                {"match": "[<>]", "name": "keyword.operator.comparison.tosh"},
                {"match": "(?<=\\s)[-+*/%](?=\\s)", "name": "keyword.operator.arithmetic.tosh"},
                {
                    "match": "(?<=\\s)\\?(?=\\s)|(?<=\\s):(?=\\s)",
                    "name": "keyword.operator.conditional.tosh",
                },
                {"match": "=(?!=|>)", "name": "keyword.operator.assignment.tosh"},
            ],
        },
        "pipe": {"match": "\\|(?!\\|)", "name": "keyword.operator.pipe.tosh"},

        # ── Literals ──────────────────────────────────────────────────────
        "numeric": {
            "patterns": [
                {
                    "comment": "Quantity with a unit: 100`m, 9.8`m/s^2, 5`kg",
                    "match": "\\b([0-9][0-9_]*(?:\\.[0-9][0-9_]*)?)(`)([A-Za-z][A-Za-z0-9^/*·-]*)",
                    "captures": {
                        "1": {"name": "constant.numeric.tosh"},
                        "2": {"name": "punctuation.definition.unit.tosh"},
                        "3": {"name": "keyword.other.unit.tosh"},
                    },
                },
                {
                    "comment": "Imaginary literal: 4i, 2.5i",
                    "match": "\\b[0-9][0-9_]*(?:\\.[0-9][0-9_]*)?i\\b",
                    "name": "constant.numeric.imaginary.tosh",
                },
                {
                    "comment": "Storage sizes: 1kb, 512mb",
                    "match": "\\b[0-9][0-9_]*(?:\\.[0-9][0-9_]*)?(?:b|kb|mb|gb|tb|pb)\\b",
                    "name": "constant.numeric.size.tosh",
                },
                {
                    "comment": "ISO temporal instants — must precede the duration rule",
                    "match": "\\b[0-9]{4}-[0-9]{2}-[0-9]{2}(?:T[0-9]{2}:[0-9]{2}(?::[0-9]{2}(?:\\.[0-9]{1,7})?)?(?:Z|[+-][0-9]{2}:[0-9]{2})?)?\\b",
                    "name": "constant.numeric.datetime.tosh",
                },
                {
                    "comment": "Temporal shorthand: 7d, 30m, 1w2d4h",
                    "match": "\\b(?:[0-9][0-9_]*(?:\\.[0-9][0-9_]*)?(?:ns|us|ms|mo|ka|Ma|Ga|Ta|da|s|m|h|d|w|y|c))+\\b",
                    "name": "constant.numeric.duration.tosh",
                },
                {
                    "comment": "IPv4 literal — four explicit octets, no leading zeroes",
                    "match": "\\b(?:25[0-5]|2[0-4][0-9]|1[0-9]{2}|[1-9]?[0-9])(?:\\.(?:25[0-5]|2[0-4][0-9]|1[0-9]{2}|[1-9]?[0-9])){3}\\b",
                    "name": "constant.numeric.ip.tosh",
                },
                {"match": "\\b0[xX][0-9a-fA-F][0-9a-fA-F_]*\\b", "name": "constant.numeric.hex.tosh"},
                {"match": "\\b0[bB][01][01_]*\\b", "name": "constant.numeric.binary.tosh"},
                {"match": "\\b0[oO][0-7][0-7_]*\\b", "name": "constant.numeric.octal.tosh"},
                {"match": "\\b[0-9][0-9_]*\\.[0-9][0-9_]*\\b", "name": "constant.numeric.float.tosh"},
                {"match": "\\b[0-9][0-9_]*\\b", "name": "constant.numeric.integer.tosh"},
            ],
        },
        "flags": {
            "patterns": [
                {
                    "match": "\\s(--[a-zA-Z][a-zA-Z0-9-]*)\\b",
                    "captures": {"1": {"name": "entity.other.attribute-name.flag.long.tosh"}},
                },
                {
                    "match": "\\s(-[a-zA-Z][a-zA-Z0-9]*)\\b",
                    "captures": {"1": {"name": "entity.other.attribute-name.flag.short.tosh"}},
                },
            ],
        },
        "punctuation": {
            "patterns": [
                {"match": "[{}]", "name": "punctuation.section.block.tosh"},
                {"match": "[\\[\\]]", "name": "punctuation.section.brackets.tosh"},
                {"match": "[()]", "name": "punctuation.section.parens.tosh"},
                {"match": ";", "name": "punctuation.terminator.tosh"},
                {"match": ",", "name": "punctuation.separator.tosh"},
                {"match": "\\.", "name": "punctuation.accessor.tosh"},
            ],
        },
    },
}

out = Path(sys.argv[1]) if len(sys.argv) > 1 else DEFAULT_OUTPUT
with out.open("w", encoding="utf-8") as handle:
    json.dump(grammar, handle, indent=2, ensure_ascii=False)
    handle.write("\n")
print(f"wrote {out} ({BUILTINS.count('|') + 1} built-in names)")
