targetScope = 'resourceGroup'

param accountName string
param location string

// Companion resource only. There is deliberately no Cosmos resource declaration here.
resource companion 'Microsoft.InferenceService/inferenceAccounts@2026-08-01-preview' = {
  name: accountName
  location: location
}

output resourceId string = companion.id
