@description('Azure region for the Function App and plan.')
param location string

@description('Name of the Linux Consumption hosting plan.')
param planName string

@description('Name of the Function App.')
param functionAppName string

@description('Storage connection string used by Azure Functions runtime and Table storage correlation state.')
@secure()
param storageConnectionString string

@description('Application Insights connection string.')
param appInsightsConnectionString string

@description('Key Vault URI used to build Key Vault references, for example https://myvault.vault.azure.net/.')
param keyVaultUri string

@description('Name of the Key Vault secret that will contain the GitHub token or app credential.')
param githubCredentialSecretName string

@description('Name of the Key Vault secret that will contain the Twilio Account SID.')
param twilioAccountSidSecretName string

@description('Name of the Key Vault secret that will contain the Twilio Auth Token.')
param twilioAuthTokenSecretName string

@description('Name of the Key Vault secret that will contain the shared secret used to authenticate the workflow -> NotifyRequester callback.')
param notifySharedSecretName string

@description('Twilio phone number used as the SMS From address, in E.164 format.')
param twilioFromNumber string = ''

@description('Comma-separated allowlist of E.164 phone numbers permitted to submit change requests.')
param smsAllowlist string = ''

@description('Name of the storage table used by the function app for SMS/GitHub correlation state.')
param correlationTableName string = 'SmsCorrelation'

@description('Tags to apply to resources.')
param tags object = {}

resource plan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: planName
  location: location
  tags: tags
  kind: 'functionapp,linux'
  sku: {
    name: 'Y1'
    tier: 'Dynamic'
  }
  properties: {
    reserved: true
  }
}

resource functionApp 'Microsoft.Web/sites@2023-12-01' = {
  name: functionAppName
  location: location
  tags: tags
  kind: 'functionapp,linux'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    publicNetworkAccess: 'Enabled'
    clientAffinityEnabled: false
    siteConfig: {
      linuxFxVersion: 'DOTNET-ISOLATED|8.0'
      ftpsState: 'FtpsOnly'
      minTlsVersion: '1.2'
      http20Enabled: true
      alwaysOn: false
      appSettings: [
        {
          name: 'AzureWebJobsStorage'
          value: storageConnectionString
        }
        {
          name: 'FUNCTIONS_EXTENSION_VERSION'
          value: '~4'
        }
        {
          name: 'FUNCTIONS_WORKER_RUNTIME'
          value: 'dotnet-isolated'
        }
        {
          name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
          value: appInsightsConnectionString
        }
        {
          name: 'Storage__ConnectionString'
          value: storageConnectionString
        }
        {
          name: 'Storage__TableName'
          value: correlationTableName
        }
        {
          name: 'GitHub__Token'
          value: '@Microsoft.KeyVault(SecretUri=${keyVaultUri}secrets/${githubCredentialSecretName})'
        }
        {
          name: 'Twilio__AccountSid'
          value: '@Microsoft.KeyVault(SecretUri=${keyVaultUri}secrets/${twilioAccountSidSecretName})'
        }
        {
          name: 'Twilio__AuthToken'
          value: '@Microsoft.KeyVault(SecretUri=${keyVaultUri}secrets/${twilioAuthTokenSecretName})'
        }
        {
          name: 'Twilio__FromNumber'
          value: twilioFromNumber
        }
        {
          name: 'Twilio__Allowlist'
          value: smsAllowlist
        }
        {
          name: 'Notify__SharedSecret'
          value: '@Microsoft.KeyVault(SecretUri=${keyVaultUri}secrets/${notifySharedSecretName})'
        }
      ]
    }
  }
}

output name string = functionApp.name
output resourceId string = functionApp.id
output principalId string = functionApp.identity.principalId
output defaultHostname string = functionApp.properties.defaultHostName
