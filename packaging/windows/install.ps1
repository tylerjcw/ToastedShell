<#
.SYNOPSIS
    Installs TōSh for the current user.

.DESCRIPTION
    Copies the bundle beside this script into %LOCALAPPDATA%\Programs\Tosh and puts that
    directory on the user PATH.

    Deliberately per-user: the Arch package installs to /usr/bin because a system package
    manager is already running as root, and nothing here needs to be. Per-user means no
    elevation prompt, no shared state, and an uninstall that cannot affect anyone else.

.PARAMETER InstallRoot
    Where to install. Defaults to %LOCALAPPDATA%\Programs\Tosh.

.PARAMETER NoPath
    Copy the files but leave PATH alone.
#>
[CmdletBinding()]
param(
    [string] $InstallRoot = (Join-Path $env:LOCALAPPDATA 'Programs\Tosh'),
    [switch] $NoPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$source = Split-Path -Parent $MyInvocation.MyCommand.Path

if (-not (Test-Path (Join-Path $source 'tosh.exe'))) {
    throw "No tosh.exe beside this script. Run install.ps1 from inside the unpacked bundle."
}

Write-Host "Installing TōSh to $InstallRoot"

# A previous install is replaced wholesale rather than merged. A self-contained bundle is a
# matched set of ~200 files; copying a newer one over an older one leaves whatever the new
# version stopped shipping, and those stale assemblies still load.
if (Test-Path $InstallRoot) {
    Write-Host '  removing the previous install'
    Remove-Item -Recurse -Force $InstallRoot
}

New-Item -ItemType Directory -Force -Path $InstallRoot | Out-Null

# The bundle carries the publish names; the Arch package renames two of them on the way to
# /usr/bin, and the same names should work here. Renaming on copy keeps the bundle a faithful
# publish output while what lands on PATH matches Linux.
$rename = @{
    'Tosh.Lsp.exe' = 'tosh-lsp.exe'
    'Tosh.Mcp.exe' = 'tosh-mcp.exe'
}

Get-ChildItem -Path $source -File |
    Where-Object { $_.Name -notin @('install.ps1', 'uninstall.ps1') } |
    ForEach-Object {
        $target = if ($rename.ContainsKey($_.Name)) { $rename[$_.Name] } else { $_.Name }
        Copy-Item $_.FullName -Destination (Join-Path $InstallRoot $target) -Force
    }

$installed = (Get-ChildItem -Path $InstallRoot -File).Count
Write-Host "  $installed files"

if ($NoPath) {
    Write-Host 'Skipping PATH (--NoPath).'
}
else {
    # User scope, so no administrator is required. Read the stored value rather than
    # $env:PATH: the process copy is the machine and user values already joined, and writing
    # that back would copy every machine entry into the user's own PATH.
    $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
    if ($null -eq $userPath) { $userPath = '' }

    $entries = $userPath -split ';' | Where-Object { $_ -ne '' }
    $already = $entries | Where-Object { $_.TrimEnd('\') -ieq $InstallRoot.TrimEnd('\') }

    if ($already) {
        Write-Host 'PATH already contains the install directory.'
    }
    else {
        $updated = (@($entries) + $InstallRoot) -join ';'
        [Environment]::SetEnvironmentVariable('Path', $updated, 'User')
        Write-Host 'Added the install directory to your PATH.'
        Write-Host 'Open a new terminal for it to take effect.'
    }
}

Write-Host ''
Write-Host 'Installed:'
foreach ($exe in @('tosh.exe', 'tosh-lsp.exe', 'tosh-mcp.exe', 'tome.exe', 'crumb.exe')) {
    $path = Join-Path $InstallRoot $exe
    if (Test-Path $path) { Write-Host "  $exe" }
}
Write-Host ''
Write-Host "Run 'tosh --version' in a new terminal to check."
