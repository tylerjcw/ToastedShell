#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat <<'EOF'
Usage: scripts/install-linux-desktop.sh [--user|--system]

Install Linux desktop integration for TōSh:
  - text/x-tosh MIME type for *.tosh and common tosh shebangs
  - hicolor MIME and app icons
  - TōSh app launcher
  - "Run with TōSh" Open With entry
  - GtkSourceView language specs for GTK editors

Options:
  --user      Install under ~/.local/share (default)
  --system    Install under /usr/share using sudo when needed
  --help      Show this help text

This script avoids named icon themes. It installs only into hicolor.
EOF
}

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
mode="user"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --user)
      mode="user"
      shift
      ;;
    --system)
      mode="system"
      shift
      ;;
    --help|-h)
      usage
      exit 0
      ;;
    *)
      echo "Unknown option: $1" >&2
      usage >&2
      exit 1
      ;;
  esac
done

if [[ ! -f "$repo_root/editor/gtksourceview/tosh.lang" ]]; then
  echo "Missing GtkSourceView spec: editor/gtksourceview/tosh.lang" >&2
  exit 1
fi

file_icon="$repo_root/editor/vscode/tosh.tosh-lang/icons/tosh-dark.svg"
app_icon="$repo_root/editor/vscode/tosh.tosh-lang/icons/tosh-dark.svg"

if [[ ! -f "$file_icon" ]]; then
  echo "Missing icon: $file_icon" >&2
  exit 1
fi

run=()
if [[ "$mode" == "system" ]]; then
  data_root="/usr/share"
  if [[ "${EUID:-$(id -u)}" -ne 0 ]]; then
    run=(sudo)
  fi
else
  data_root="${XDG_DATA_HOME:-$HOME/.local/share}"
fi

applications_dir="$data_root/applications"
mime_dir="$data_root/mime"
icons_dir="$data_root/icons/hicolor"

current_default=""
if [[ "$mode" == "user" ]] && command -v xdg-mime >/dev/null 2>&1; then
  current_default="$(xdg-mime query default text/x-tosh 2>/dev/null || true)"
fi

tmp_dir="$(mktemp -d)"
trap 'rm -rf "$tmp_dir"' EXIT

cat > "$tmp_dir/tosh.xml" <<'XML'
<?xml version="1.0" encoding="UTF-8"?>
<mime-info xmlns="http://www.freedesktop.org/standards/shared-mime-info">
  <mime-type type="text/x-tosh">
    <comment>TōSh script</comment>
    <icon name="text-x-tosh"/>
    <sub-class-of type="text/plain"/>
    <glob pattern="*.tosh" weight="100"/>
    <magic priority="80">
      <match type="string" value="#!/usr/bin/tosh" offset="0"/>
      <match type="string" value="#!/bin/tosh" offset="0"/>
      <match type="string" value="#!/bin/env tosh" offset="0"/>
      <match type="string" value="#!/usr/bin/env tosh" offset="0"/>
    </magic>
  </mime-type>
</mime-info>
XML

cat > "$tmp_dir/tosh.desktop" <<'EOF'
[Desktop Entry]
Type=Application
Name=TōSh
GenericName=Shell
Comment=ToastedShell interactive shell
Exec=tosh
TryExec=tosh
Icon=tosh
Terminal=true
Categories=System;TerminalEmulator;
StartupNotify=false
EOF

cat > "$tmp_dir/tosh-open.desktop" <<'EOF'
[Desktop Entry]
Type=Application
Name=Run with TōSh
GenericName=Shell
Comment=Run a TōSh script
Exec=tosh %f
TryExec=tosh
Icon=tosh
Terminal=true
NoDisplay=true
MimeType=text/x-tosh;
Categories=System;TerminalEmulator;
StartupNotify=false
EOF

if command -v xmllint >/dev/null 2>&1; then
  xmllint --noout "$tmp_dir/tosh.xml"
  xmllint --noout "$repo_root/editor/gtksourceview/tosh.lang"
fi

if command -v desktop-file-validate >/dev/null 2>&1; then
  desktop-file-validate "$tmp_dir/tosh.desktop" "$tmp_dir/tosh-open.desktop"
fi

echo "Installing TōSh desktop integration ($mode)..."

"${run[@]}" install -Dm644 "$tmp_dir/tosh.xml" "$mime_dir/packages/tosh.xml"
"${run[@]}" install -Dm644 "$file_icon" "$icons_dir/scalable/mimetypes/text-x-tosh.svg"
"${run[@]}" install -Dm644 "$app_icon" "$icons_dir/scalable/apps/tosh.svg"
"${run[@]}" install -Dm644 "$tmp_dir/tosh.desktop" "$applications_dir/tosh.desktop"
"${run[@]}" install -Dm644 "$tmp_dir/tosh-open.desktop" "$applications_dir/tosh-open.desktop"

for version in 2.0 3.0 4 5; do
  "${run[@]}" install -Dm644 "$repo_root/editor/gtksourceview/tosh.lang" \
    "$data_root/gtksourceview-$version/language-specs/tosh.lang"
done

if command -v update-mime-database >/dev/null 2>&1; then
  "${run[@]}" update-mime-database "$mime_dir"
fi

if command -v gtk-update-icon-cache >/dev/null 2>&1; then
  "${run[@]}" gtk-update-icon-cache -f -t "$icons_dir" >/dev/null 2>&1 || true
fi

if command -v update-desktop-database >/dev/null 2>&1; then
  "${run[@]}" update-desktop-database "$applications_dir" >/dev/null 2>&1 || true
fi

if [[ "$mode" == "user" && -n "$current_default" && "$current_default" != "tosh-open.desktop" ]] && command -v xdg-mime >/dev/null 2>&1; then
  xdg-mime default "$current_default" text/x-tosh
fi

echo "Installed:"
echo "  MIME:           $mime_dir/packages/tosh.xml"
echo "  MIME icon:      $icons_dir/scalable/mimetypes/text-x-tosh.svg"
echo "  App icon:       $icons_dir/scalable/apps/tosh.svg"
echo "  App launcher:   $applications_dir/tosh.desktop"
echo "  Open With:      $applications_dir/tosh-open.desktop"
echo "  GtkSourceView:  $data_root/gtksourceview-{2.0,3.0,4,5}/language-specs/tosh.lang"

if [[ "$mode" == "user" ]] && command -v xdg-mime >/dev/null 2>&1; then
  echo "  Default opener: $(xdg-mime query default text/x-tosh 2>/dev/null || true)"
fi
