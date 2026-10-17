#Requires -Version 7.0
[CmdletBinding()]
param(
  [ValidateRange(1024, 65535)][int]$PythonPort = 8501,
  [ValidateRange(1024, 65535)][int]$DotnetPort = 8503
)
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot '../python/scripts/start.ps1') `
  -ProjectRoot (Split-Path -Parent $PSScriptRoot) -IncludeDotnet `
  -PythonPort $PythonPort -DotnetPort $DotnetPort
