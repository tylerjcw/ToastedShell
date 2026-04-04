#!/usr/bin/env bash
set -euo pipefail

# Clean build artifacts, test results, and published binaries from the tosh repo.
# Usage: scripts/clean.sh [--all]
#
#   --all   Also remove the VS Code extension node_modules

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
all=false

for arg in "$@"; do
  case "$arg" in
    --all) all=true ;;
    --help|-h)
      echo "Usage: scripts/clean.sh [--all]"
      echo "  --all  Also remove editor/vscode node_modules"
      exit 0
      ;;
    *)
      echo "Unknown option: $arg" >&2
      exit 1
      ;;
  esac
done

# Use dotnet clean to remove bin/obj and custom artifact targets
if command -v dotnet &>/dev/null && [[ -f "$repo_root/Tosh.slnx" ]]; then
  echo "Running dotnet clean..."
  dotnet clean "$repo_root/Tosh.slnx" --verbosity quiet
fi

removed=0

clean_dir() {
  if [[ -d "$1" ]]; then
    rm -rf "$1"
    echo "  removed $1"
    removed=$((removed + 1))
  fi
}

# Catch anything dotnet clean missed (stale TestResults in old location, etc.)
while IFS= read -r -d '' dir; do
  clean_dir "$dir"
done < <(find "$repo_root/src" "$repo_root/tests" -type d \( -name bin -o -name obj -o -name TestResults \) -print0 2>/dev/null)

clean_dir "$repo_root/artifacts/test-results"
clean_dir "$repo_root/artifacts/publish"

if $all; then
  clean_dir "$repo_root/editor/vscode/tosh.tosh-lang/node_modules"
fi

if [[ $removed -eq 0 ]]; then
  echo "Already clean."
else
  echo "Removed $removed directories."
fi
