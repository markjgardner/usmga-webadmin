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

@description('Data residency location for Azure Communication Services data, for example United States or Europe.')
param acsDataLocation string = 'United States'

@description('Azure region for Azure Communication Services resource metadata. ACS is commonly deployed as Global.')
param acsLocation string = 'Global'

@description('Azure Static Web Apps region. Choose a region supported by Static Web Apps.')
param staticWebAppLocation string = 'eastus2'

@description('Name of the Function in the Function App that receives Event Grid SMS events. Must match the [Function(...)] name in the function code. Deploy function code before creating or validating the Event Grid AzureFunction destination.')
param smsHandlerFunctionName string = 'SmsInbound'

@description('Event Grid destination mode. Use AzureFunction after function code exists; use WebHook when passing a secured endpoint URL at deployment time.')
@allowed([
  'AzureFunction'
  'WebHook'
])
param eventGridDestinationType string = 'AzureFunction'

@description('Webhook endpoint URL for Event Grid when eventGridDestinationType is WebHook. Do not store secret-bearing URLs in parameter files.')
@secure()
param eventGridWebhookEndpointUrl string = ''

@description('Name of the Key Vault secret that operators will create for the GitHub PAT or GitHub App credential.')
param githubCredentialSecretName string = 'github-credential'

@description('Name of the Key Vault secret that operators will create for the ACS connection string.')
param acsConnectionStringSecretName string = 'acs-connection-string'

@description('Name of the Key Vault secret that operators will create for the shared secret used to authenticate the workflow -> NotifyRequester callback. Must match the NOTIFY_SHARED_SECRET GitHub Actions secret.')
param notifySharedSecretName string = 'notify-shared-secret'

@description('ACS-provisioned phone number used as the SMS From address, in E.164 format (for example +15551234567).')
param smsFromNumber string = ''

@description('Comma-separated allowlist of E.164 phone numbers permitted to submit change requests.')
param smsAllowlist string = ''

@description('Storage table name used for SMS/GitHub correlation state.')
param correlationTableName string = 'SmsCorrelation'

@description('Tags applied to all resources.')
param tags object = {
  project: 'usmga-webadmin'
  workload: 'sms-website-change-pipeline'
}

var suffix = uniqueString(resourceGroup().id)
var normalizedPrefix = toLower(replace(namePrefix, '-', ''))
var normalizedEnvironment = toLower(replace(environmentName, '-', ''))
var baseName = toLower('${namePrefix}-${environmentName}-${suffix}')

var staticWebAppName = '${baseName}-swa'
var communicationServiceName = '${baseName}-acs'
var eventGridSystemTopicName = '${baseName}-acs-egst'
var eventGridSubscriptionName = 'sms-received-to-function'
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
    acsConnectionStringSecretName: acsConnectionStringSecretName
    notifySharedSecretName: notifySharedSecretName
    smsFromNumber: smsFromNumber
    smsAllowlist: smsAllowlist
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

module communicationServices 'modules/communication-services.bicep' = {
  name: 'communication-services'
  params: {
    location: acsLocation
    name: communicationServiceName
    dataLocation: acsDataLocation
    tags: tags
  }
}

module eventGrid 'modules/event-grid.bicep' = {
  name: 'event-grid'
  params: {
    location: acsLocation
    systemTopicName: eventGridSystemTopicName
    sourceResourceId: communicationServices.outputs.resourceId
    eventSubscriptionName: eventGridSubscriptionName
    destinationType: eventGridDestinationType
    azureFunctionResourceId: '${functionApp.outputs.resourceId}/functions/${smsHandlerFunctionName}'
    webhookEndpointUrl: eventGridWebhookEndpointUrl
    tags: tags
  }
}

output staticWebAppName string = staticWebApp.outputs.name
output staticWebAppDefaultHostname string = staticWebApp.outputs.defaultHostname
output functionAppName string = functionApp.outputs.name
output functionAppDefaultHostname string = functionApp.outputs.defaultHostname
output functionAppResourceId string = functionApp.outputs.resourceId
output smsHandlerAzureFunctionResourceId string = '${functionApp.outputs.resourceId}/functions/${smsHandlerFunctionName}'
output communicationServicesName string = communicationServices.outputs.name
output eventGridSystemTopicName string = eventGrid.outputs.systemTopicName
output eventGridSubscriptionName string = eventGrid.outputs.eventSubscriptionName
output storageAccountName string = storage.outputs.name
output correlationTableName string = correlationTableName
output keyVaultName string = keyVault.outputs.name
output logAnalyticsWorkspaceId string = monitoring.outputs.workspaceId
output applicationInsightsId string = monitoring.outputs.appInsightsId
