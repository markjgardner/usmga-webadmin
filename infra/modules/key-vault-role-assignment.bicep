@description('Resource ID of the Key Vault that receives the role assignment.')
param keyVaultResourceId string

@description('Principal ID of the Function App managed identity.')
param principalId string

@description('Deterministic suffix used for the role assignment name.')
param nameSeed string

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: last(split(keyVaultResourceId, '/'))
}

resource secretsUserAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, principalId, nameSeed, 'Key Vault Secrets User')
  scope: keyVault
  properties: {
    principalId: principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')
  }
}

output roleAssignmentId string = secretsUserAssignment.id
