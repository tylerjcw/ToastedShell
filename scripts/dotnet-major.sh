#!/usr/bin/env bash
# Prints the .NET major version this repository builds against.
#
# The answer is resolved exactly as Directory.Build.props resolves ToshDotNetMajor:
# the third field of .tosh-publish-state ("YY.M:N:D") when it is there, otherwise the
# <ToshDotNetMajor> default in Directory.Build.props. Anything that installs an SDK
# before `dotnet` exists — CI, the Claude Code session hook — asks this script, so
# the framework is still chosen in one place and a bump moves every installer with it.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
major=""

if [[ -f "$repo_root/.tosh-publish-state" ]]; then
  # MSBuild trims the file before splitting it, so whitespace does not count here either.
  major="$(tr -d '[:space:]' < "$repo_root/.tosh-publish-state" | cut -s -d: -f3)"
fi

if [[ -z "$major" ]]; then
  major="$(sed -n 's:.*<ToshDotNetMajor>\([0-9][0-9]*\)</ToshDotNetMajor>.*:\1:p' \
    "$repo_root/Directory.Build.props" | head -n 1)"
fi

if [[ ! "$major" =~ ^[0-9]+$ ]]; then
  echo "dotnet-major: cannot determine the .NET major version from .tosh-publish-state or Directory.Build.props" >&2
  exit 1
fi

echo "$major"
