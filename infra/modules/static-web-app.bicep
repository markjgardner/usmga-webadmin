@description('Azure region for the Static Web App. Use a region supported by Azure Static Web Apps.')
param location string

@description('Name of the Static Web App.')
param name string

@description('Tags to apply to the resource.')
param tags object = {}

resource staticWebApp 'Microsoft.Web/staticSites@2023-12-01' = {
  name: name
  location: location
  tags: tags
  sku: {
    name: 'Standard'
    tier: 'Standard'
  }
  properties: {}
}

output name string = staticWebApp.name
output resourceId string = staticWebApp.id
output defaultHostname string = staticWebApp.properties.defaultHostname
