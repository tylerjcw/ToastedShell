#!/usr/bin/env bash
# SessionStart hook for Claude Code on the web. It makes a fresh cloud session able to build
# and test TōSh, and to use the dev companion, from its first turn:
#
#  1. Installs the .NET SDK the build targets (scripts/dotnet-major.sh) unless it is there.
#     Cloud images carry no .NET, and what a hook installs is not kept between sessions, so
#     putting the same install in the environment's setup script makes startup faster.
#  2. Makes that SDK the one every process finds — on PATH, and registered for app hosts,
#     which do not look in ~/.dotnet by themselves.
#  3. Builds the dev companion that .mcp.json starts, and restores Tosh.slnx.
#
# Local sessions are left alone: there, all of this is the developer's own setup.
set -euo pipefail

if [[ "${CLAUDE_CODE_REMOTE:-}" != "true" ]]; then
  exit 0
fi

repo_root="${CLAUDE_PROJECT_DIR:-$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)}"
major="$("$repo_root/scripts/dotnet-major.sh")"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

log() { echo "session-start: $*" >&2; }
has_sdk() { "$1" --list-sdks 2>/dev/null | grep -q "^$major\."; }

# ── 1. The SDK ────────────────────────────────────────────────────────────────

dotnet_bin=""
if command -v dotnet >/dev/null 2>&1 && has_sdk dotnet; then
  dotnet_bin="$(command -v dotnet)"
elif [[ -x "$HOME/.dotnet/dotnet" ]] && has_sdk "$HOME/.dotnet/dotnet"; then
  dotnet_bin="$HOME/.dotnet/dotnet"
else
  log "installing the .NET $major SDK into $HOME/.dotnet"
  installer="$(mktemp)"
  trap 'rm -f "$installer"' EXIT
  curl -fsSL --retry 3 https://dot.net/v1/dotnet-install.sh -o "$installer"
  # The channel's newest build, previews and release candidates included, as CI installs it.
  bash "$installer" --channel "$major.0" --install-dir "$HOME/.dotnet" --no-path >&2
  dotnet_bin="$HOME/.dotnet/dotnet"
  has_sdk "$dotnet_bin" || { log "the installer finished but no .NET $major SDK is there"; exit 1; }
fi

# ── 2. Where everything finds it ──────────────────────────────────────────────

dotnet_root="$(dirname "$(readlink -f "$dotnet_bin")")"

# The MCP launcher and anything else Claude Code starts outside this session's shell.
if [[ -w /usr/local/bin ]]; then
  ln -sf "$dotnet_root/dotnet" /usr/local/bin/dotnet
fi

# App hosts — the one Tosh.Cli's build runs, published binaries — look for the runtime in
# DOTNET_ROOT, then in this file, then in /usr/share/dotnet. Without it the post-build step
# of Tosh.Cli exits 131 in any shell that did not export DOTNET_ROOT.
if [[ -w /etc ]]; then
  mkdir -p /etc/dotnet
  echo "$dotnet_root" > /etc/dotnet/install_location
fi

if [[ -n "${CLAUDE_ENV_FILE:-}" ]]; then
  {
    echo "export DOTNET_ROOT=\"$dotnet_root\""
    echo "export PATH=\"$dotnet_root:\$PATH\""
    echo "export DOTNET_CLI_TELEMETRY_OPTOUT=1"
    echo "export DOTNET_NOLOGO=1"
  } >> "$CLAUDE_ENV_FILE"
fi

export DOTNET_ROOT="$dotnet_root" PATH="$dotnet_root:$PATH"

# ── 3. The companion and the solution ─────────────────────────────────────────

# The companion first: the MCP host starts it alongside this hook and may be waiting on it.
"$repo_root/scripts/devcompanion.sh" --build

log "restoring Tosh.slnx"
dotnet restore "$repo_root/Tosh.slnx" -nologo -v q >&2

echo "Session setup: .NET SDK $(dotnet --version) is installed and Tosh.slnx is restored." \
  "The dev companion is built; if its MCP tools (tosh-devcompanion) are missing," \
  "use scripts/devcompanion.sh recall|list|store|forget from Bash instead."
