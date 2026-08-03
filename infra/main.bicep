targetScope = 'resourceGroup'

@description('Azure region for regional resources.')
param location string = resourceGroup().location

@description('Short environment name such as test, staging, or prod.')
@minLength(2)
@maxLength(12)
param environmentName string = 'test'

@description('Short resource name prefix. Use lowercase letters/numbers/hyphens where supported.')
@minLength(2)
@maxLength(12)
param namePrefix string = 'usmga'

@description('Azure Static Web Apps region. Choose a region supported by Static Web Apps.')
param staticWebAppLocation string = 'eastus2'

@description('Name of the Key Vault secret that operators will create for the GitHub PAT or GitHub App credential.')
param githubCredentialSecretName string = 'github-credential'

@description('Name of the Key Vault secret that operators will create for the Telegram bot token from BotFather.')
param telegramBotTokenSecretName string = 'telegram-bot-token'

@description('Name of the Key Vault secret that operators will create for the Telegram webhook secret token registered with setWebhook.')
param telegramWebhookSecretName string = 'telegram-webhook-secret'

@description('Name of the Key Vault secret that operators will create for the shared secret used to authenticate the workflow -> NotifyRequester callback. Must match the NOTIFY_SHARED_SECRET GitHub Actions secret.')
param notifySharedSecretName string = 'notify-shared-secret'

@description('Comma-separated Telegram numeric user IDs permitted to submit change requests.')
param telegramAllowlist string = ''

@description('Storage table name used for Telegram/GitHub correlation state.')
param correlationTableName string = 'TelegramCorrelation'

@description('Tags applied to all resources.')
param tags object = {
  project: 'usmga-webadmin'
  workload: 'telegram-website-change-pipeline'
}

var suffix = uniqueString(resourceGroup().id)
var normalizedPrefix = toLower(replace(namePrefix, '-', ''))
var normalizedEnvironment = toLower(replace(environmentName, '-', ''))
var baseName = toLower('${namePrefix}-${environmentName}-${suffix}')

var staticWebAppName = '${baseName}-swa'
var storageAccountName = '${take('${normalizedPrefix}${normalizedEnvironment}', 9)}st${suffix}'
var workspaceName = '${baseName}-law'
var appInsightsName = '${baseName}-appi'
var keyVaultName = '${take('${normalizedPrefix}${normalizedEnvironment}', 9)}kv${suffix}'
var planName = '${baseName}-plan'
var functionAppName = '${baseName}-func'

module monitoring 'modules/monitoring.bicep' = {
  name: 'monitoring'
  params: {
    location: location
    workspaceName: workspaceName
    appInsightsName: appInsightsName
    tags: tags
  }
}

module storage 'modules/storage.bicep' = {
  name: 'storage'
  params: {
    location: location
    name: storageAccountName
    correlationTableName: correlationTableName
    tags: tags
  }
}

module keyVault 'modules/key-vault.bicep' = {
  name: 'key-vault'
  params: {
    location: location
    name: keyVaultName
    tags: tags
  }
}

module functionApp 'modules/function-app.bicep' = {
  name: 'function-app'
  params: {
    location: location
    planName: planName
    functionAppName: functionAppName
    storageConnectionString: storage.outputs.connectionString
    appInsightsConnectionString: monitoring.outputs.appInsightsConnectionString
    keyVaultUri: keyVault.outputs.vaultUri
    githubCredentialSecretName: githubCredentialSecretName
    telegramBotTokenSecretName: telegramBotTokenSecretName
    telegramWebhookSecretName: telegramWebhookSecretName
    notifySharedSecretName: notifySharedSecretName
    telegramAllowlist: telegramAllowlist
    correlationTableName: correlationTableName
    tags: tags
  }
}

module keyVaultRoleAssignment 'modules/key-vault-role-assignment.bicep' = {
  name: 'key-vault-secret-reader'
  params: {
    keyVaultResourceId: keyVault.outputs.resourceId
    principalId: functionApp.outputs.principalId
    nameSeed: suffix
  }
}

module staticWebApp 'modules/static-web-app.bicep' = {
  name: 'static-web-app'
  params: {
    location: staticWebAppLocation
    name: staticWebAppName
    tags: tags
  }
}

output staticWebAppName string = staticWebApp.outputs.name
output staticWebAppDefaultHostname string = staticWebApp.outputs.defaultHostname
output functionAppName string = functionApp.outputs.name
output functionAppDefaultHostname string = functionApp.outputs.defaultHostname
output functionAppResourceId string = functionApp.outputs.resourceId
output storageAccountName string = storage.outputs.name
output correlationTableName string = correlationTableName
output keyVaultName string = keyVault.outputs.name
output logAnalyticsWorkspaceId string = monitoring.outputs.workspaceId
output applicationInsightsId string = monitoring.outputs.appInsightsId
