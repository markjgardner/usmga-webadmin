@description('Azure region for the Communication Services resource metadata. ACS is a global service; use Global unless Azure guidance changes.')
param location string = 'Global'

@description('Name of the Azure Communication Services resource.')
param name string

@description('Data residency location for Communication Services data, for example United States or Europe.')
param dataLocation string

@description('Tags to apply to the resource.')
param tags object = {}

// Phone number purchase, toll-free verification, and 10DLC registration are manual/provider-governed steps
// and are not automatable with Bicep. Complete them after this ACS resource is deployed.
resource communicationService 'Microsoft.Communication/communicationServices@2023-03-31' = {
  name: name
  location: location
  tags: tags
  properties: {
    dataLocation: dataLocation
  }
}

output name string = communicationService.name
output resourceId string = communicationService.id
