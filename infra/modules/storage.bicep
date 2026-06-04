@description('Azure region for the storage account.')
param location string

@description('Globally unique storage account name, 3-24 lowercase letters and numbers.')
param name string

@description('Name of the table used for SMS/GitHub correlation state.')
param correlationTableName string = 'SmsCorrelation'

@description('Tags to apply to the resource.')
param tags object = {}

resource storageAccount 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: name
  location: location
  tags: tags
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  properties: {
    allowBlobPublicAccess: false
    allowSharedKeyAccess: true
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    defaultToOAuthAuthentication: true
    publicNetworkAccess: 'Enabled'
    networkAcls: {
      bypass: 'AzureServices'
      defaultAction: 'Allow'
    }
  }
}

resource tableService 'Microsoft.Storage/storageAccounts/tableServices@2023-05-01' = {
  parent: storageAccount
  name: 'default'
}

resource correlationTable 'Microsoft.Storage/storageAccounts/tableServices/tables@2023-05-01' = {
  parent: tableService
  name: correlationTableName
}

var primaryKey = storageAccount.listKeys().keys[0].value
var connectionString = 'DefaultEndpointsProtocol=https;AccountName=${storageAccount.name};AccountKey=${primaryKey};EndpointSuffix=${environment().suffixes.storage}'

output name string = storageAccount.name
output resourceId string = storageAccount.id
output tableName string = correlationTable.name
@secure()
output connectionString string = connectionString
