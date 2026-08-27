<#
.SYNOPSIS
    Removes a per-user TōSh install.

.DESCRIPTION
    Deletes the install directory and takes it back off the user PATH. Both are undone even
    if the other was never done, so a partial install still cleans up.
#>
[CmdletBinding()]
param(
    [string] $InstallRoot = (Join-Path $env:LOCALAPPDATA 'Programs\Tosh')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (Test-Path $InstallRoot) {
    Remove-Item -Recurse -Force $InstallRoot
    Write-Host "Removed $InstallRoot"
}
else {
    Write-Host "Nothing installed at $InstallRoot"
}

$userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
if ($null -eq $userPath) { $userPath = '' }

$entries = $userPath -split ';' | Where-Object { $_ -ne '' }
$kept = $entries | Where-Object { $_.TrimEnd('\') -ine $InstallRoot.TrimEnd('\') }

if (@($kept).Count -ne @($entries).Count) {
    [Environment]::SetEnvironmentVariable('Path', ($kept -join ';'), 'User')
    Write-Host 'Removed the install directory from your PATH.'
}
else {
    Write-Host 'PATH did not contain the install directory.'
}
