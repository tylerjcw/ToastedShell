#!/usr/bin/env python3

from __future__ import annotations

import argparse
import json
import os
import shutil
import subprocess
import time
from pathlib import Path


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Sync the repo-owned ToSh VS Code extension into the local VS Code extensions directory.")
    parser.add_argument("--source-dir", type=Path, default=None, help="Extension source directory inside the repo.")
    parser.add_argument("--extensions-dir", type=Path, default=None, help="VS Code extensions directory to sync into.")
    parser.add_argument("--quiet", action="store_true", help="Suppress normal status output.")
    parser.add_argument("--regenerate", action="store_true", help="Regenerate language-data.json from command metadata before syncing.")
    return parser.parse_args()


def repo_root_from_script() -> Path:
    return Path(__file__).resolve().parent.parent


def load_package_metadata(source_dir: Path) -> dict[str, object]:
    package_path = source_dir / "package.json"
    with package_path.open("r", encoding="utf-8") as handle:
        return json.load(handle)


def determine_extensions_dir(cli_value: Path | None) -> Path:
    if cli_value is not None:
        return cli_value.expanduser().resolve()

    env_value = os.environ.get("VSCODE_EXTENSIONS_DIR")
    if env_value:
        return Path(env_value).expanduser().resolve()

    return (Path.home() / ".vscode" / "extensions").resolve()


def sync_tree(source_dir: Path, target_dir: Path) -> None:
    if target_dir.exists():
        try:
            shutil.rmtree(target_dir)
        except OSError:
            time.sleep(0.2)
            try:
                shutil.rmtree(target_dir)
            except OSError:
                pass

    target_dir.mkdir(parents=True, exist_ok=True)

    for source_path in source_dir.rglob("*"):
        relative = source_path.relative_to(source_dir)
        destination = target_dir / relative

        if source_path.is_dir():
            destination.mkdir(parents=True, exist_ok=True)
            continue

        destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(source_path, destination)


def ensure_extension_dependencies(source_dir: Path, quiet: bool) -> None:
    package_json_path = source_dir / "package.json"
    package_lock_path = source_dir / "package-lock.json"
    dependency_marker = source_dir / "node_modules" / "vscode-languageclient" / "package.json"

    latest_input_mtime = package_json_path.stat().st_mtime
    if package_lock_path.exists():
        latest_input_mtime = max(latest_input_mtime, package_lock_path.stat().st_mtime)

    if dependency_marker.exists() and dependency_marker.stat().st_mtime >= latest_input_mtime:
        return

    command = ["npm", "install", "--no-audit", "--no-fund"]
    stdout = subprocess.DEVNULL if quiet else None
    stderr = subprocess.STDOUT if quiet else None
    subprocess.run(command, cwd=source_dir, check=True, stdout=stdout, stderr=stderr)


def regenerate_language_data(repo_root: Path, source_dir: Path, quiet: bool) -> None:
    cli_project = repo_root / "src" / "Tosh.Cli" / "Tosh.Cli.csproj"
    output_path = source_dir / "language-data.json"
    command = [
        "dotnet", "run", "--project", str(cli_project),
        "--", "--export-command-metadata", "--vscode",
        "-o", str(output_path),
    ]
    stdout = subprocess.DEVNULL if quiet else None
    stderr = subprocess.STDOUT if quiet else None
    subprocess.run(command, cwd=repo_root, check=True, stdout=stdout, stderr=stderr)
    if not quiet:
        print(f"Regenerated: {output_path}")


def main() -> int:
    args = parse_args()
    repo_root = repo_root_from_script()
    source_dir = (args.source_dir or (repo_root / "editor" / "vscode" / "tosh.tosh-lang")).resolve()

    if not source_dir.exists():
        raise SystemExit(f"Extension source directory was not found: {source_dir}")

    if args.regenerate:
        regenerate_language_data(repo_root, source_dir, args.quiet)

    ensure_extension_dependencies(source_dir, args.quiet)

    package = load_package_metadata(source_dir)
    publisher = str(package["publisher"])
    name = str(package["name"])
    version = str(package["version"])

    extensions_dir = determine_extensions_dir(args.extensions_dir)
    target_dir = extensions_dir / f"{publisher}.{name}-{version}"

    sync_tree(source_dir, target_dir)

    if not args.quiet:
        print(f"Synced ToSh VS Code extension:")
        print(f"  source: {source_dir}")
        print(f"  target: {target_dir}")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
