#!/usr/bin/env bash
# Runs the Tōsh Dev Companion (tools/Tosh.DevCompanion) on this repository's memory store.
#
#   scripts/devcompanion.sh                    MCP server on stdio — what .mcp.json starts
#   scripts/devcompanion.sh recall <query>     CLI: recall, list, store, forget
#   scripts/devcompanion.sh --build            build if needed, then exit
#
# Whatever directory it is started from, the database is .tosh/memory.db (unless TOSH_MEMORY_DB
# names another) and shared memories are mirrored to .tosh/memories/, where git picks them up.
#
# The companion is built into tools/Tosh.DevCompanion/bin/mcp/ on first use and again when
# anything it is built from changes. Build output goes to stderr: in MCP mode stdout is the
# protocol channel, and one stray line on it breaks the connection.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
project="$repo_root/tools/Tosh.DevCompanion"
out="$project/bin/mcp"
dll="$out/Tosh.DevCompanion.dll"

export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

# An MCP host does not start this from a login shell, so an SDK that dotnet-install.sh put
# in ~/.dotnet (where the Claude Code session hook puts it) may not be on PATH.
if ! command -v dotnet >/dev/null 2>&1 && [[ -x "${DOTNET_ROOT:-$HOME/.dotnet}/dotnet" ]]; then
  export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
  export PATH="$DOTNET_ROOT:$PATH"
fi

if ! command -v dotnet >/dev/null 2>&1; then
  echo "devcompanion: dotnet is not on PATH; install the .NET $("$repo_root/scripts/dotnet-major.sh") SDK" >&2
  exit 1
fi

# True when there is no build, or something it is built from is newer than it.
needs_build() {
  [[ -f "$dll" ]] || return 0
  [[ -n "$(find "$project" \( -path "$project/bin" -o -path "$project/obj" \) -prune \
             -o -type f -newer "$dll" -print -quit)" ]] && return 0

  local input
  for input in Directory.Build.props Directory.Build.targets .tosh-publish-state; do
    [[ "$repo_root/$input" -nt "$dll" ]] && return 0
  done
  return 1
}

if needs_build; then
  mkdir -p "$out"

  # The session hook and an MCP host can both get here at once; the second waits and then
  # finds the build done.
  exec 9>"$out/.build.lock"
  if command -v flock >/dev/null 2>&1; then flock 9; fi

  if needs_build; then
    echo "devcompanion: building $project" >&2
    dotnet build "$project" -c Release -o "$out" -nologo -v q >&2
    # MSBuild leaves an up-to-date output alone; without this a change that did not alter
    # the build would send every later start through a no-op build.
    touch "$dll"
  fi

  exec 9>&-
fi

if [[ "${1:-}" == "--build" ]]; then exit 0; fi

cd "$repo_root"
export TOSH_MEMORY_DB="${TOSH_MEMORY_DB:-$repo_root/.tosh/memory.db}"
exec dotnet "$dll" "$@"
