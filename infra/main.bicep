targetScope = 'resourceGroup'

@description('An existing Cosmos DB for NoSQL account. This template never deploys or updates it.')
param cosmosAccountName string

@description('An existing database. No database or container is created by this template.')
param databaseName string

@description('The companion inference account name. Use the Cosmos account name for the linked service.')
param inferenceAccountName string = cosmosAccountName

@description('Set true only after confirming the companion inference account is absent.')
param createInference bool = false

@description('Region supported by the inference service in your subscription. Required for companion creation.')
param inferenceLocation string = ''

@description('Assign scoped data and inference access only when explicitly enabled.')
param assignRoles bool = false

@description('Object ID, not application/client ID. Use the managed identity principalId for App Service.')
param principalId string = ''

@allowed([
  'User'
  'ServicePrincipal'
])
param principalType string = 'User'

@allowed([
  'Reader'
  'Contributor'
])
@description('Reader for the web app; Contributor for the operator who runs the loader.')
param cosmosAccess string = 'Reader'

@description('Full Azure role definition resource ID for Semantic Reranker User. Discover by name with Azure CLI.')
param inferenceRoleDefinitionId string = ''

@description('Only these existing containers receive Cosmos data access.')
param containerNames array = [
  'reranker_dotnet_qflat'
  'reranker_dotnet_diskann'
]

resource cosmos 'Microsoft.DocumentDB/databaseAccounts@2025-04-15' existing = {
  name: cosmosAccountName
}

resource inference 'Microsoft.InferenceService/inferenceAccounts@2026-10-15' existing = {
  name: inferenceAccountName
}

module companion 'inference-account.bicep' = if (createInference) {
  name: 'create-companion-inference'
  params: {
    accountName: inferenceAccountName
    location: inferenceLocation
  }
}

var cosmosRoleId = cosmosAccess == 'Reader'
  ? '00000000-0000-0000-0000-000000000001'
  : '00000000-0000-0000-0000-000000000002'

resource cosmosAssignments 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2025-04-15' = [for containerName in containerNames: if (assignRoles) {
  parent: cosmos
  name: guid(cosmos.id, databaseName, containerName, principalId, cosmosRoleId)
  properties: {
    principalId: principalId
    roleDefinitionId: '${cosmos.id}/sqlRoleDefinitions/${cosmosRoleId}'
    scope: '${cosmos.id}/dbs/${databaseName}/colls/${containerName}'
  }
}]

resource inferenceAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (assignRoles) {
  name: guid(inference.id, principalId, inferenceRoleDefinitionId)
  scope: inference
  properties: {
    principalId: principalId
    principalType: principalType
    roleDefinitionId: inferenceRoleDefinitionId
  }
  dependsOn: [
    companion
  ]
}

output cosmosAccountId string = cosmos.id
output inferenceAccountId string = inference.id
