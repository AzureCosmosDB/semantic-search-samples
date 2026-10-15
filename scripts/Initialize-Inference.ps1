[CmdletBinding()]
param(
  [Parameter(Mandatory)][string]$SubscriptionId,
  [Parameter(Mandatory)][string]$ResourceGroup,
  [Parameter(Mandatory)][string]$CosmosAccountName,
  [Parameter(Mandatory)][string]$DatabaseName,
  [string]$InferenceLocation,
  [switch]$CreateMissingInference,
  [switch]$AssignRoles,
  [string]$PrincipalId,
  [ValidateSet('User', 'ServicePrincipal')][string]$PrincipalType = 'User',
  [ValidateSet('Reader', 'Contributor')][string]$CosmosAccess = 'Reader',
  [switch]$ConfirmChanges
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Invoke-AzureJson {
  param([string[]]$Arguments)
  $result = & az @Arguments --only-show-errors --output json
  if ($LASTEXITCODE -ne 0) { throw 'Azure CLI operation failed. No provisioning fallback will run.' }
  return (($result -join "`n") | ConvertFrom-Json)
}

# Existing account and database are prerequisites. These operations only read them.
$null = Invoke-AzureJson -Arguments @('cosmosdb', 'show', '--subscription', $SubscriptionId,
  '--resource-group', $ResourceGroup, '--name', $CosmosAccountName)
$null = Invoke-AzureJson -Arguments @('cosmosdb', 'sql', 'database', 'show', '--subscription', $SubscriptionId,
  '--resource-group', $ResourceGroup, '--account-name', $CosmosAccountName, '--name', $DatabaseName)
$resources = @(Invoke-AzureJson -Arguments @('resource', 'list', '--subscription', $SubscriptionId,
  '--resource-group', $ResourceGroup, '--resource-type', 'Microsoft.InferenceService/inferenceAccounts'))
$exists = @($resources | Where-Object { $_.name -eq $CosmosAccountName }).Count -eq 1
if (-not $exists -and -not $CreateMissingInference) {
  throw 'Companion inference is absent. Use -CreateMissingInference with a supported region only after reviewing costs.'
}
if ($exists -and $CreateMissingInference) {
  throw 'Companion already exists. Omit -CreateMissingInference to avoid redeploying it.'
}
if (-not $exists -and [string]::IsNullOrWhiteSpace($InferenceLocation)) { throw 'Specify -InferenceLocation for creation.' }
if (($CreateMissingInference -or $AssignRoles) -and -not $ConfirmChanges) { throw 'Use -ConfirmChanges after reviewing the requested changes.' }
if (-not $CreateMissingInference -and -not $AssignRoles) {
  Write-Output 'Existing Cosmos/database/inference resources found. No deployment or role change was performed.'
  return
}
$roleDefinition = ''
if ($AssignRoles) {
  if ([string]::IsNullOrWhiteSpace($PrincipalId)) { throw '-PrincipalId must be the user or managed identity object ID.' }
  $roles = @(Invoke-AzureJson -Arguments @('role', 'definition', 'list', '--subscription', $SubscriptionId,
    '--name', 'Semantic Reranker User'))
  if ($roles.Count -ne 1) { throw 'Cannot resolve Semantic Reranker User. Check subscription/service availability; do not substitute a broad role.' }
  $roleDefinition = $roles[0].id
}
$parameters = @{
  cosmosAccountName = @{ value = $CosmosAccountName }
  databaseName = @{ value = $DatabaseName }
  inferenceAccountName = @{ value = $CosmosAccountName }
  createInference = @{ value = [bool]$CreateMissingInference }
  inferenceLocation = @{ value = $(if ($InferenceLocation) { $InferenceLocation } else { '' }) }
  assignRoles = @{ value = [bool]$AssignRoles }
  principalId = @{ value = $(if ($PrincipalId) { $PrincipalId } else { '' }) }
  principalType = @{ value = $PrincipalType }
  cosmosAccess = @{ value = $CosmosAccess }
  inferenceRoleDefinitionId = @{ value = $roleDefinition }
}
$privateDirectory = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../.local/provisioning'))
[System.IO.Directory]::CreateDirectory($privateDirectory) | Out-Null
$parameterPath = Join-Path $privateDirectory 'inference.parameters.json'
[System.IO.File]::WriteAllText($parameterPath, (@{
  '$schema' = 'https://schema.management.azure.com/schemas/2019-04-01/deploymentParameters.json#'
  contentVersion = '1.0.0.0'
  parameters = $parameters
} | ConvertTo-Json -Depth 10), [System.Text.UTF8Encoding]::new($false))
$template = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../infra/main.bicep'))
$null = Invoke-AzureJson -Arguments @('deployment', 'group', 'create', '--subscription', $SubscriptionId,
  '--resource-group', $ResourceGroup, '--name', 'semantic-reranker-companion',
  '--mode', 'Incremental', '--template-file', $template, '--parameters', "@$parameterPath")
Write-Output 'Requested companion/permission setup completed. Cosmos account and database were not deployed or updated.'
