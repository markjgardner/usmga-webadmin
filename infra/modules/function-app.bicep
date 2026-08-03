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

@description('Name of the Key Vault secret that will contain the Telegram bot token from BotFather.')
param telegramBotTokenSecretName string

@description('Name of the Key Vault secret that will contain the Telegram webhook secret token registered with setWebhook.')
param telegramWebhookSecretName string

@description('Name of the Key Vault secret that will contain the shared secret used to authenticate the workflow -> NotifyRequester callback.')
param notifySharedSecretName string

@description('Comma-separated Telegram numeric user IDs permitted to submit change requests.')
param telegramAllowlist string = ''

@description('Name of the storage table used by the function app for Telegram/GitHub correlation state.')
param correlationTableName string = 'TelegramCorrelation'

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
          name: 'Telegram__BotToken'
          value: '@Microsoft.KeyVault(SecretUri=${keyVaultUri}secrets/${telegramBotTokenSecretName})'
        }
        {
          name: 'Telegram__WebhookSecret'
          value: '@Microsoft.KeyVault(SecretUri=${keyVaultUri}secrets/${telegramWebhookSecretName})'
        }
        {
          name: 'Telegram__Allowlist'
          value: telegramAllowlist
        }
        {
          name: 'Telegram__UploadBaseUrl'
          value: ''
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
