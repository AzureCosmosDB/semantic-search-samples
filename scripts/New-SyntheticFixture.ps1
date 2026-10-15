[CmdletBinding()]
param(
  [string]$OutputPath = (Join-Path $PSScriptRoot '../.local/fixtures/movies.dotnet.preembedded.json')
)
$ErrorActionPreference = 'Stop'
$manifestPath = Join-Path $PSScriptRoot '../fixtures/dotnet/movies.synthetic.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$items = @(
  foreach ($item in $manifest) {
    $vector = [float[]]::new(1536)
    $axis = [int]$item.embeddingAxis
    if ($axis -lt 0 -or $axis -ge 1536) { throw 'Invalid fixture axis.' }
    $vector[$axis] = 1
    [ordered]@{
      id = $item.id
      title = $item.title
      text = $item.text
      embedding = $vector
    }
  }
)
$fullPath = [System.IO.Path]::GetFullPath($OutputPath)
$privateRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../.local')) + [System.IO.Path]::DirectorySeparatorChar
if (-not $fullPath.StartsWith($privateRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
  throw 'Generated fixtures must be written under this repository .local directory.'
}
[System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($fullPath)) | Out-Null
[System.IO.File]::WriteAllText($fullPath, (ConvertTo-Json -InputObject $items -Depth 6), [System.Text.UTF8Encoding]::new($false))
Write-Output 'Materialized three synthetic preembedded documents under .local. No Azure operation was performed.'
