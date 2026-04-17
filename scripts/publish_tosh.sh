#!/usr/bin/bash
set -euo pipefail

usage() {
  cat <<'EOF'
Usage: scripts/publish_tosh.sh [options]

Build a single-file ToSh executable.

Options:
  --rid <RID>            Runtime identifier to publish for.
                         Default: current host RID from `dotnet --info`.
  --output <DIR>         Output directory.
                         Default: artifacts/publish/<rid>/(single-file|single-file-trimmed)
  --trimmed              Enable trimmed single-file publish.
                         This is experimental for ToSh because the runtime uses
                         extensive reflection and dynamic loading.
  --framework-dependent  Publish framework-dependent instead of self-contained.
  --help                 Show this help text.
EOF
}

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

current_rid="$(dotnet --info | awk -F: '/RID:/{gsub(/^[[:space:]]+/, "", $2); print $2; exit}')"
rid="${current_rid:-linux-x64}"
trimmed=false
self_contained=true
output_dir=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --rid)
      rid="${2:?missing RID value}"
      shift 2
      ;;
    --output)
      output_dir="${2:?missing output directory}"
      shift 2
      ;;
    --trimmed)
      trimmed=true
      shift
      ;;
    --framework-dependent)
      self_contained=false
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

mode_dir="single-file"
if [[ "$trimmed" == true ]]; then
  mode_dir="single-file-trimmed"
fi

if [[ -z "$output_dir" ]]; then
  output_dir="$repo_root/artifacts/publish/$rid/$mode_dir"
fi

mkdir -p "$output_dir"

publish_args=(
  publish
  "$repo_root/src/Tosh.Cli/Tosh.Cli.csproj"
  -c Release
  -r "$rid"
  -o "$output_dir"
  -p:PublishSingleFile=true
  -p:PublishReadyToRun=true
  -p:EnableCompressionInSingleFile=false
  -p:DebugType=None
  -p:DebugSymbols=false
  -p:DisableToshVsCodeExtensionSync=true
)

if [[ "$self_contained" == true ]]; then
  publish_args+=(-p:SelfContained=true)
else
  publish_args+=(-p:SelfContained=false)
fi

if [[ "$trimmed" == true ]]; then
  publish_args+=(
    -p:PublishTrimmed=true
    -p:TrimMode=partial
  )
fi

echo "Publishing ToSh:"
echo "  RID:            $rid"
echo "  Output:         $output_dir"
echo "  Self-contained: $self_contained"
echo "  Trimmed:        $trimmed"
echo

dotnet "${publish_args[@]}"

published_binary=""
for candidate in "$output_dir/Tosh.Cli" "$output_dir/Tosh.Cli.exe" "$output_dir/tosh" "$output_dir/tosh.exe"; do
  if [[ -f "$candidate" ]]; then
    published_binary="$candidate"
    break
  fi
done

if [[ -z "$published_binary" ]]; then
  echo "Publish completed, but no executable was found in $output_dir" >&2
  exit 1
fi

target_name="tosh"
if [[ "$published_binary" == *.exe ]] || [[ "$rid" == win-* ]]; then
  target_name="tosh.exe"
fi

target_path="$output_dir/$target_name"

if [[ "$published_binary" != "$target_path" ]]; then
  rm -f "$target_path"
  mv "$published_binary" "$target_path"
  published_binary="$target_path"
fi

echo
ls -lh "$published_binary"
echo "Published executable: $published_binary"

# Install to ~/.local/bin if requested or by default for single-file builds.
install_dir="${HOME}/.local/bin"
install_path="$install_dir/$target_name"

if [[ -d "$install_dir" ]]; then
  # Remove first in case the binary is running (Text file busy).
  rm -f "$install_path"
  cp "$published_binary" "$install_path"
  chmod +x "$install_path"
  echo "Installed:  $install_path"
fi
