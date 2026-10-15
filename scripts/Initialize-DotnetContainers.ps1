[CmdletBinding()]
param(
  [Parameter(Mandatory)][string]$SubscriptionId,
  [Parameter(Mandatory)][string]$ResourceGroup,
  [Parameter(Mandatory)][string]$AccountName,
  [Parameter(Mandatory)][string]$DatabaseName,
  [switch]$ConfirmCreate
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Invoke-AzureJson {
  param([string[]]$Arguments)
  $result = & az @Arguments --only-show-errors --output json
  if ($LASTEXITCODE -ne 0) { throw 'Azure CLI operation failed. No fallback or destructive repair will be attempted.' }
  return (($result -join "`n") | ConvertFrom-Json)
}

function Assert-CompatiblePolicy {
  param($Resource, [string]$IndexType)
  if (@($Resource.partitionKey.paths).Count -ne 1 -or $Resource.partitionKey.paths[0] -ne '/id') {
    throw 'Existing partition key is incompatible. No resource was changed.'
  }
  $vector = @($Resource.vectorEmbeddingPolicy.vectorEmbeddings | Where-Object {
    $_.path -eq '/embedding' -and $_.dimensions -eq 1536 -and
    $_.dataType -eq 'float32' -and $_.distanceFunction -eq 'cosine'
  })
  $vectorIndex = @($Resource.indexingPolicy.vectorIndexes | Where-Object {
    $_.path -eq '/embedding' -and $_.type -eq $IndexType
  })
  $text = @($Resource.fullTextPolicy.fullTextPaths | Where-Object {
    $_.path -eq '/text' -and $_.language -eq 'en-US'
  })
  $textIndex = @($Resource.indexingPolicy.fullTextIndexes | Where-Object { $_.path -eq '/text' })
  if ($vector.Count -ne 1 -or $vectorIndex.Count -ne 1 -or $text.Count -ne 1 -or $textIndex.Count -ne 1 -or
      $Resource.indexingPolicy.indexingMode -ne 'consistent' -or -not $Resource.indexingPolicy.automatic) {
    throw 'Existing vector/full-text policies are incompatible. No resource was changed.'
  }
}

$baseArguments = @('--subscription', $SubscriptionId, '--resource-group', $ResourceGroup,
  '--account-name', $AccountName, '--database-name', $DatabaseName)
# Read-only discovery. A failed list is NOT interpreted as absence.
$existing = @(Invoke-AzureJson -Arguments (@('cosmosdb', 'sql', 'container', 'list') + $baseArguments))
$targets = @(
  @{ Name = 'reranker_dotnet_qflat'; Index = 'quantizedFlat' },
  @{ Name = 'reranker_dotnet_diskann'; Index = 'diskANN' }
)
# Check every existing target before creating anything.
foreach ($target in $targets) {
  $match = @($existing | Where-Object { $_.name -eq $target.Name -or $_.resource.id -eq $target.Name })
  if ($match.Count -gt 1) { throw 'Ambiguous existing container response.' }
  if ($match.Count -eq 1) {
    Assert-CompatiblePolicy -Resource $match[0].resource -IndexType $target.Index
    $offer = Invoke-AzureJson -Arguments (
      @('cosmosdb', 'sql', 'container', 'throughput', 'show') + $baseArguments + @('--name', $target.Name))
    if ($offer.resource.throughput -ne 400 -or
        ($offer.resource.PSObject.Properties.Name -contains 'autoscaleSettings' -and
         $null -ne $offer.resource.autoscaleSettings)) {
      throw 'Existing target does not have dedicated manual 400 RU/s. No throughput change will be attempted.'
    }
  }
}

$privateDirectory = Join-Path $PSScriptRoot '../.local/provisioning'
[System.IO.Directory]::CreateDirectory($privateDirectory) | Out-Null
foreach ($target in $targets) {
  $match = @($existing | Where-Object { $_.name -eq $target.Name -or $_.resource.id -eq $target.Name })
  if ($match.Count -eq 1) {
    Write-Output "Reusing compatible $($target.Name); no changes."
    continue
  }
  if (-not $ConfirmCreate) { throw 'A .NET container is missing. Review the dedicated 400 RU/s cost, then use -ConfirmCreate.' }
  $resource = [ordered]@{
    id = $target.Name
    partitionKey = @{ paths = @('/id'); kind = 'Hash'; version = 2 }
    indexingPolicy = @{
      automatic = $true
      indexingMode = 'consistent'
      includedPaths = @(@{ path = '/*' })
      excludedPaths = @(@{ path = '/"_etag"/?' }, @{ path = '/embedding/*' })
      vectorIndexes = @(@{ path = '/embedding'; type = $target.Index })
      fullTextIndexes = @(@{ path = '/text' })
    }
    vectorEmbeddingPolicy = @{
      vectorEmbeddings = @(@{ path = '/embedding'; dataType = 'float32'; dimensions = 1536; distanceFunction = 'cosine' })
    }
    fullTextPolicy = @{
      defaultLanguage = 'en-US'
      fullTextPaths = @(@{ path = '/text'; language = 'en-US' })
    }
  }
  $body = @{ properties = @{ resource = $resource; options = @{ throughput = 400 } } }
  $bodyPath = [System.IO.Path]::GetFullPath((Join-Path $privateDirectory "$($target.Name).json"))
  [System.IO.File]::WriteAllText($bodyPath, ($body | ConvertTo-Json -Depth 15), [System.Text.UTF8Encoding]::new($false))
  $accountPath = '/subscriptions/{0}/resourceGroups/{1}/providers/Microsoft.DocumentDB/databaseAccounts/{2}' -f `
    [uri]::EscapeDataString($SubscriptionId), [uri]::EscapeDataString($ResourceGroup), [uri]::EscapeDataString($AccountName)
  $url = 'https://management.azure.com{0}/sqlDatabases/{1}/containers/{2}?api-version=2025-04-15' -f `
    $accountPath, [uri]::EscapeDataString($DatabaseName), [uri]::EscapeDataString($target.Name)
  # Only the two fixed .NET container resource URLs can be written. Never PUT the account or database.
  $null = Invoke-AzureJson -Arguments @('rest', '--method', 'put', '--url', $url, '--body', "@$bodyPath")
  # ARM can be asynchronous. Poll read-only metadata rather than retry a PUT.
  $ready = $false
  for ($attempt = 0; $attempt -lt 60; $attempt++) {
    Start-Sleep -Seconds 2
    $containers = @(Invoke-AzureJson -Arguments (@('cosmosdb', 'sql', 'container', 'list') + $baseArguments))
    $created = @($containers | Where-Object { $_.name -eq $target.Name -or $_.resource.id -eq $target.Name })
    if ($created.Count -eq 1) {
      Assert-CompatiblePolicy -Resource $created[0].resource -IndexType $target.Index
      $ready = $true
      break
    }
  }
  if (-not $ready) { throw 'Container creation did not become visible before timeout. Inspect ARM status before retrying.' }
  $offer = Invoke-AzureJson -Arguments (
    @('cosmosdb', 'sql', 'container', 'throughput', 'show') + $baseArguments + @('--name', $target.Name))
  if ($offer.resource.throughput -ne 400) { throw 'Unexpected provisioned throughput. No automatic change will be made.' }
  Write-Output "Created and checked $($target.Name) at dedicated manual 400 RU/s."
}
