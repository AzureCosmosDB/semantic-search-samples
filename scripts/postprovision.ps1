#Requires -Version 7.0
[CmdletBinding()]
param(
  [switch]$Prepare,
  [switch]$RegenerateInvalidName
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent $PSScriptRoot
& (Join-Path $root 'python/scripts/postprovision.ps1') -ProjectRoot $root -IncludeDotnet `
  -Prepare:$Prepare -RegenerateInvalidName:$RegenerateInvalidName
