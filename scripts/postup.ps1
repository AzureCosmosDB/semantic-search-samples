#Requires -Version 7.0
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot '../python/scripts/postup.ps1') -ProjectRoot (Split-Path -Parent $PSScriptRoot) -IncludeDotnet
