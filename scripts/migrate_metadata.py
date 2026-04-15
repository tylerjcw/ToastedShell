#!/usr/bin/env python3
"""
Migrate builtin command metadata from HelpCatalog.CommandDetailsByName
to [CommandArgument], [CommandOption], [CommandExample], [CommandOutput],
and [PipelineInput] attributes on command classes.

Also migrates ExamplesByName entries for commands without CommandDetailsByName examples.

PipelineInput mapping:
  HelpPipelineInputInfo(Object, Scalar, PathLike, Collection, Notes)
  →
  [PipelineInput(AcceptsRecord=Object, AcceptsScalar=Scalar, AcceptsList=PathLike, AcceptsTable=Collection, Description=Notes)]
"""

import re
import os
import sys

TOSH_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CATALOG_PATH = os.path.join(TOSH_ROOT, "src/Tosh.Core/HelpCatalog.cs")
CMD_DIR = os.path.join(TOSH_ROOT, "src/Tosh.Core/Commands")

RICH_ATTRS = ['CommandArgument', 'CommandOption', 'CommandExample', 'CommandNote', 'CommandOutput', 'PipelineInput']

# Commands that share one class for multiple names (multi-instance).
# These need GetMetadata() overrides instead of class-level attributes.
MULTI_INSTANCE_NAMES = {
    "any", "all", "none",     # QuantifierCommand
    "foreach",                 # EachCommand (alias of each)
    "summary",                 # SummarizeCommand (alias of summarize)
}


def read_file(path):
    with open(path) as f:
        return f.read()


def write_file(path, content):
    with open(path, "w") as f:
        f.write(content)


def escape_cs_string(s):
    """Escape a string for C# string literal."""
    return s.replace('\\', '\\\\').replace('"', '\\"')


def find_matching_bracket(text, start, open_ch='(', close_ch=')'):
    """Find position of matching closing bracket from start position."""
    depth = 0
    in_string = False
    escape_next = False
    i = start
    while i < len(text):
        c = text[i]
        if escape_next:
            escape_next = False
            i += 1
            continue
        if c == '\\' and in_string:
            escape_next = True
            i += 1
            continue
        if c == '"' and not in_string:
            in_string = True
            i += 1
            continue
        if c == '"' and in_string:
            in_string = False
            i += 1
            continue
        if in_string:
            i += 1
            continue
        if c == open_ch:
            depth += 1
        elif c == close_ch:
            depth -= 1
            if depth == 0:
                return i
        i += 1
    return -1


def parse_command_details(catalog):
    """Parse CommandDetailsByName entries using bracket-aware parsing."""
    m = re.search(r'CommandDetailsByName\s*=\s*new Dictionary', catalog)
    if not m:
        print("ERROR: Could not find CommandDetailsByName")
        return {}

    # Find the opening { of the dictionary initializer
    brace_start = catalog.index('{', m.end())
    brace_end = find_matching_bracket(catalog, brace_start, '{', '}')

    section = catalog[brace_start+1:brace_end]

    entries = {}
    pos = 0
    while True:
        # Find next entry: ["name"] = new(
        entry_match = re.search(r'\["([^"]+)"\]\s*=\s*new\(', section[pos:])
        if not entry_match:
            break

        cmd_name = entry_match.group(1)
        # Position of the opening ( of new(
        paren_start = pos + entry_match.end() - 1
        paren_end = find_matching_bracket(section, paren_start, '(', ')')
        if paren_end == -1:
            print(f"  WARNING: unmatched paren for {cmd_name}")
            pos = pos + entry_match.end()
            continue

        block = section[paren_start:paren_end+1]
        entries[cmd_name] = block
        pos = paren_end + 1

    return entries


def parse_arguments(block):
    """Extract argument definitions from a detail block."""
    args = []
    m = re.search(r'Arguments:\s*\[', block)
    if not m:
        return args

    bracket_start = block.index('[', m.start())
    bracket_end = find_matching_bracket(block, bracket_start, '[', ']')
    if bracket_end == -1:
        return args

    arg_section = block[bracket_start+1:bracket_end]

    # Parse new("name", "desc", Required: bool, TypeName: "type")
    # Handle all parameter combinations
    for am in re.finditer(r'new\(', arg_section):
        paren_start = am.start() + 3
        paren_end = find_matching_bracket(arg_section, paren_start, '(', ')')
        if paren_end == -1:
            continue

        inner = arg_section[paren_start+1:paren_end].strip()
        # Extract strings and named params
        strings = []
        named = {}
        # Simple state machine to parse the inner content
        i = 0
        while i < len(inner):
            if inner[i] == '"':
                # Extract string
                j = i + 1
                s = []
                while j < len(inner):
                    if inner[j] == '\\' and j + 1 < len(inner):
                        s.append(inner[j:j+2])
                        j += 2
                    elif inner[j] == '"':
                        break
                    else:
                        s.append(inner[j])
                        j += 1
                strings.append(''.join(s))
                i = j + 1
            elif inner[i:].startswith('Required:'):
                val = inner[i+len('Required:'):].strip()
                if val.startswith('true'):
                    named['Required'] = True
                else:
                    named['Required'] = False
                i += len('Required:') + 5
            elif inner[i:].startswith('TypeName:'):
                # Find the string value
                j = inner.index('"', i)
                k = j + 1
                s = []
                while k < len(inner):
                    if inner[k] == '\\' and k + 1 < len(inner):
                        s.append(inner[k:k+2])
                        k += 2
                    elif inner[k] == '"':
                        break
                    else:
                        s.append(inner[k])
                        k += 1
                named['TypeName'] = ''.join(s)
                i = k + 1
            else:
                i += 1

        name = strings[0] if len(strings) > 0 else ""
        desc = strings[1] if len(strings) > 1 else ""
        required = named.get('Required', True)
        type_name = named.get('TypeName')
        args.append((name, desc, required, type_name))

    return args


def parse_options(block):
    """Extract option definitions."""
    opts = []
    m = re.search(r'Options:\s*\[', block)
    if not m:
        return opts

    bracket_start = block.index('[', m.start())
    bracket_end = find_matching_bracket(block, bracket_start, '[', ']')
    if bracket_end == -1:
        return opts

    opt_section = block[bracket_start+1:bracket_end]

    for om in re.finditer(r'new\(', opt_section):
        paren_start = om.start() + 3
        paren_end = find_matching_bracket(opt_section, paren_start, '(', ')')
        if paren_end == -1:
            continue

        inner = opt_section[paren_start+1:paren_end].strip()
        strings = extract_strings(inner)

        syntax = strings[0] if len(strings) > 0 else ""
        desc = strings[1] if len(strings) > 1 else ""
        opts.append((syntax, desc))

    return opts


def extract_strings(text):
    """Extract all string literals from C# source text, unescaping C# escape sequences."""
    strings = []
    i = 0
    while i < len(text):
        if text[i] == '$' and i + 1 < len(text) and text[i+1] == '"':
            # Interpolated string - extract as-is (keep interpolation braces)
            j = i + 2
            s = []
            brace_depth = 0
            while j < len(text):
                if text[j] == '{':
                    brace_depth += 1
                    s.append(text[j])
                    j += 1
                elif text[j] == '}':
                    brace_depth -= 1
                    s.append(text[j])
                    j += 1
                elif text[j] == '\\' and j + 1 < len(text):
                    # Unescape C# escape sequences
                    next_ch = text[j+1]
                    if next_ch == '"':
                        s.append('"')
                    elif next_ch == '\\':
                        s.append('\\')
                    elif next_ch == 'n':
                        s.append('\n')
                    elif next_ch == 't':
                        s.append('\t')
                    elif next_ch == 'r':
                        s.append('\r')
                    else:
                        s.append(text[j:j+2])
                    j += 2
                elif text[j] == '"' and brace_depth == 0:
                    break
                else:
                    s.append(text[j])
                    j += 1
            strings.append(''.join(s))
            i = j + 1
        elif text[i] == '"':
            j = i + 1
            s = []
            while j < len(text):
                if text[j] == '\\' and j + 1 < len(text):
                    # Unescape C# escape sequences
                    next_ch = text[j+1]
                    if next_ch == '"':
                        s.append('"')
                    elif next_ch == '\\':
                        s.append('\\')
                    elif next_ch == 'n':
                        s.append('\n')
                    elif next_ch == 't':
                        s.append('\t')
                    elif next_ch == 'r':
                        s.append('\r')
                    else:
                        s.append(text[j:j+2])
                    j += 2
                elif text[j] == '"':
                    break
                else:
                    s.append(text[j])
                    j += 1
            strings.append(''.join(s))
            i = j + 1
        else:
            i += 1
    return strings


def parse_examples(block):
    """Extract examples from a detail block."""
    examples = []
    m = re.search(r'Examples:\s*\[', block)
    if not m:
        return examples

    bracket_start = block.index('[', m.start())
    bracket_end = find_matching_bracket(block, bracket_start, '[', ']')
    if bracket_end == -1:
        return examples

    ex_section = block[bracket_start+1:bracket_end]

    for em in re.finditer(r'new\(', ex_section):
        paren_start = em.start() + 3
        paren_end = find_matching_bracket(ex_section, paren_start, '(', ')')
        if paren_end == -1:
            continue

        inner = ex_section[paren_start+1:paren_end].strip()
        strings = extract_strings(inner)

        code = strings[0] if len(strings) > 0 else ""
        title = strings[1] if len(strings) > 1 else None
        examples.append((code, title))

    return examples


def parse_output(block):
    """Extract Output field."""
    m = re.search(r'Output:\s*"', block)
    if not m:
        return None
    # Extract the string starting at the quote
    start = m.end() - 1
    strings = extract_strings(block[start:start+5000])
    return strings[0] if strings else None


def parse_pipeline_input(block):
    """Extract PipelineInput: new(Object, Scalar, PathLike, Collection, Notes)."""
    m = re.search(r'PipelineInput:\s*new\(', block)
    if not m:
        return None

    paren_start = m.end() - 1
    paren_end = find_matching_bracket(block, paren_start, '(', ')')
    if paren_end == -1:
        return None

    inner = block[paren_start+1:paren_end].strip()
    # Extract booleans and optional string
    bools = re.findall(r'\b(true|false)\b', inner)
    strings = extract_strings(inner)

    if len(bools) < 4:
        return None

    return {
        'object': bools[0] == 'true',      # HelpPipelineInputInfo.Object → AcceptsRecord
        'scalar': bools[1] == 'true',      # HelpPipelineInputInfo.Scalar → AcceptsScalar
        'path_like': bools[2] == 'true',   # HelpPipelineInputInfo.PathLike → AcceptsList
        'collection': bools[3] == 'true',  # HelpPipelineInputInfo.Collection → AcceptsTable
        'notes': strings[0] if strings else None
    }


def parse_examples_by_name(catalog):
    """Parse ExamplesByName entries."""
    m = re.search(r'ExamplesByName\s*=\s*new Dictionary', catalog)
    if not m:
        return {}

    brace_start = catalog.index('{', m.end())
    brace_end = find_matching_bracket(catalog, brace_start, '{', '}')

    section = catalog[brace_start+1:brace_end]
    examples = {}

    pos = 0
    while True:
        entry_match = re.search(r'\["([^"]+)"\]\s*=\s*\[', section[pos:])
        if not entry_match:
            break

        cmd = entry_match.group(1)
        bracket_start = pos + entry_match.end() - 1
        bracket_end = find_matching_bracket(section, bracket_start, '[', ']')
        if bracket_end == -1:
            break

        ex_block = section[bracket_start+1:bracket_end]
        exs = extract_strings(ex_block)
        examples[cmd] = exs
        pos = bracket_end + 1

    return examples


def parse_command_notes(catalog):
    """Parse GetCommandNotes switch entries."""
    m = re.search(r'GetCommandNotes\(string\s+\w+\)', catalog)
    if not m:
        return {}

    # Find the body
    brace_start = catalog.index('{', m.end())
    brace_end = find_matching_bracket(catalog, brace_start, '{', '}')
    section = catalog[brace_start+1:brace_end]

    notes = {}
    for entry in re.finditer(r'"([^"]+)"\s*=>\s*"', section):
        cmd = entry.group(1)
        str_start = entry.end() - 1
        strings = extract_strings(section[str_start:str_start+2000])
        if strings:
            notes[cmd] = strings[0]

    return notes


def generate_attributes(cmd_name, args, opts, examples, output, pipeline_input, notes_text=None, simple_examples=None):
    """Generate C# attribute annotation lines for a command."""
    lines = []

    for name, desc, required, type_name in args:
        parts = [f'"{escape_cs_string(name)}"', f'"{escape_cs_string(desc)}"']
        if not required:
            parts.append("Required = false")
        if type_name:
            parts.append(f'TypeName = "{escape_cs_string(type_name)}"')
        lines.append(f'[CommandArgument({", ".join(parts)})]')

    for syntax, desc in opts:
        lines.append(f'[CommandOption("{escape_cs_string(syntax)}", "{escape_cs_string(desc)}")]')

    # Prefer detailed examples over simple ones
    if examples:
        for code, title in examples:
            if title:
                lines.append(f'[CommandExample("{escape_cs_string(code)}", Title = "{escape_cs_string(title)}")]')
            else:
                lines.append(f'[CommandExample("{escape_cs_string(code)}")]')
    elif simple_examples:
        for ex in simple_examples:
            lines.append(f'[CommandExample("{escape_cs_string(ex)}")]')

    if notes_text:
        lines.append(f'[CommandNote("{escape_cs_string(notes_text)}")]')

    if output:
        lines.append(f'[CommandOutput("{escape_cs_string(output)}")]')

    if pipeline_input:
        pi = pipeline_input
        pi_parts = []
        # Correct mapping: Object→AcceptsRecord, Scalar→AcceptsScalar, PathLike→AcceptsList, Collection→AcceptsTable
        if pi['scalar']:
            pi_parts.append("AcceptsScalar = true")
        if pi['object']:
            pi_parts.append("AcceptsRecord = true")
        if pi['path_like']:
            pi_parts.append("AcceptsList = true")
        if pi['collection']:
            pi_parts.append("AcceptsTable = true")
        if pi['notes']:
            pi_parts.append(f'Description = "{escape_cs_string(pi["notes"])}"')
        lines.append(f'[PipelineInput({", ".join(pi_parts)})]')

    return lines


def find_command_file(cmd_name, cmd_dir):
    """Find the .cs file for a command, handling both direct base("name") and parameterized constructors."""
    for f in sorted(os.listdir(cmd_dir)):
        if not f.endswith('.cs'):
            continue
        path = os.path.join(cmd_dir, f)
        content = read_file(path)

        # Direct base("name" pattern (same line or multi-line)
        if f'base("{cmd_name}"' in content:
            return path, content
        # Multi-line: base(\n            "name"
        if re.search(r'base\(\s*\n\s*"' + re.escape(cmd_name) + r'"', content):
            return path, content

        # Parameterized: name = "cmd_name" as default parameter
        if f'name = "{cmd_name}"' in content and 'base(name,' in content:
            return path, content

    return None, None


def command_has_rich_attrs(content):
    """Check if a command file already has rich metadata attributes."""
    return any(f'[{attr}' in content for attr in RICH_ATTRS)


def insert_attributes(content, attr_lines):
    """Insert attribute lines before the class declaration."""
    # Match class declaration patterns
    m = re.search(r'^(\s*)(public\s+(?:sealed\s+)?class\s+\w+(?:\(.*?\))?\s*:\s*ShellCommand)', content, re.MULTILINE | re.DOTALL)
    if not m:
        # Also try abstract class
        m = re.search(r'^(\s*)((?:public|internal)\s+(?:abstract\s+)?class\s+\w+\s*:\s*ShellCommand)', content, re.MULTILINE)
    if not m:
        return None

    indent = m.group(1)

    # Build the attribute block
    attr_block = '\n'.join(f'{indent}{line}' for line in attr_lines)

    # Insert before the class line
    insertion_point = m.start(2)
    return content[:insertion_point] + attr_block + '\n' + content[insertion_point:]


def main():
    catalog = read_file(CATALOG_PATH)

    # Parse all data sources
    details = parse_command_details(catalog)
    examples_by_name = parse_examples_by_name(catalog)
    notes = parse_command_notes(catalog)

    print(f"Parsed: {len(details)} CommandDetailsByName, {len(examples_by_name)} ExamplesByName, {len(notes)} notes")
    print()

    migrated = 0
    skipped_multi = 0
    skipped = 0
    not_found = 0
    already_done = 0

    # Process each command that has details
    for cmd_name, block in sorted(details.items()):
        if cmd_name in MULTI_INSTANCE_NAMES:
            print(f"  DEFER (multi-instance): {cmd_name}")
            skipped_multi += 1
            continue

        file_path, content = find_command_file(cmd_name, CMD_DIR)

        if file_path is None:
            print(f"  NOT FOUND: {cmd_name}")
            not_found += 1
            continue

        if command_has_rich_attrs(content):
            already_done += 1
            continue

        # Parse the details
        args = parse_arguments(block)
        opts = parse_options(block)
        exs = parse_examples(block)
        output = parse_output(block)
        pi = parse_pipeline_input(block)
        note = notes.get(cmd_name)
        simple_exs = examples_by_name.get(cmd_name) if not exs else None

        # Generate attributes
        attr_lines = generate_attributes(cmd_name, args, opts, exs, output, pi, note, simple_exs)

        if not attr_lines:
            print(f"  SKIP (no attrs): {cmd_name}")
            skipped += 1
            continue

        # Insert into file
        new_content = insert_attributes(content, attr_lines)
        if new_content is None:
            print(f"  SKIP (can't insert): {cmd_name} ({os.path.basename(file_path)})")
            skipped += 1
            continue

        write_file(file_path, new_content)
        migrated += 1
        fname = os.path.basename(file_path)
        print(f"  MIGRATED: {cmd_name} -> {fname} ({len(attr_lines)} attrs)")

    print()

    # Handle commands that only have ExamplesByName (not in CommandDetailsByName)
    for cmd_name, exs in sorted(examples_by_name.items()):
        if cmd_name in details:
            continue  # Already handled above
        if cmd_name in MULTI_INSTANCE_NAMES:
            continue

        file_path, content = find_command_file(cmd_name, CMD_DIR)
        if file_path is None:
            continue

        if command_has_rich_attrs(content):
            continue

        note = notes.get(cmd_name)
        attr_lines = generate_attributes(cmd_name, [], [], [], None, None, note, exs)

        if not attr_lines:
            continue

        new_content = insert_attributes(content, attr_lines)
        if new_content is None:
            continue

        write_file(file_path, new_content)
        migrated += 1
        fname = os.path.basename(file_path)
        print(f"  MIGRATED (examples only): {cmd_name} -> {fname} ({len(attr_lines)} attrs)")

    # Handle commands that only have notes (not in details or examples)
    for cmd_name, note in sorted(notes.items()):
        if cmd_name in details or cmd_name in examples_by_name:
            continue
        if cmd_name in MULTI_INSTANCE_NAMES:
            continue

        file_path, content = find_command_file(cmd_name, CMD_DIR)
        if file_path is None:
            continue

        if command_has_rich_attrs(content):
            continue

        attr_lines = [f'[CommandNote("{escape_cs_string(note)}")]']
        new_content = insert_attributes(content, attr_lines)
        if new_content is None:
            continue

        write_file(file_path, new_content)
        migrated += 1
        fname = os.path.basename(file_path)
        print(f"  MIGRATED (note only): {cmd_name} -> {fname}")

    print(f"\nResults: {migrated} migrated, {already_done} already done, {skipped_multi} deferred (multi-instance), {skipped} skipped, {not_found} not found")


if __name__ == "__main__":
    main()
