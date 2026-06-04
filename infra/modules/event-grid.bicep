@description('Azure region for the Event Grid system topic.')
param location string

@description('Name of the Event Grid system topic.')
param systemTopicName string

@description('Resource ID of the Azure Communication Services resource that emits SMS events.')
param sourceResourceId string

@description('Name of the Event Grid subscription.')
param eventSubscriptionName string

@description('Destination mode. AzureFunction expects azureFunctionResourceId to identify a deployed function endpoint. WebHook expects webhookEndpointUrl.')
@allowed([
  'AzureFunction'
  'WebHook'
])
param destinationType string = 'AzureFunction'

@description('Azure Function endpoint resource ID, usually <functionAppResourceId>/functions/<functionName>.')
param azureFunctionResourceId string = ''

@description('Webhook endpoint URL. Do not place secrets in parameter files; pass secure values at deploy time if the URL includes a function key.')
@secure()
param webhookEndpointUrl string = ''

@description('Tags to apply to resources.')
param tags object = {}

resource systemTopic 'Microsoft.EventGrid/systemTopics@2025-02-15' = {
  name: systemTopicName
  location: location
  tags: tags
  properties: {
    source: sourceResourceId
    topicType: 'Microsoft.Communication.CommunicationServices'
  }
}

resource functionSubscription 'Microsoft.EventGrid/systemTopics/eventSubscriptions@2025-02-15' = if (destinationType == 'AzureFunction') {
  parent: systemTopic
  name: eventSubscriptionName
  properties: {
    destination: {
      endpointType: 'AzureFunction'
      properties: {
        resourceId: azureFunctionResourceId
        maxEventsPerBatch: 1
        preferredBatchSizeInKilobytes: 64
      }
    }
    filter: {
      includedEventTypes: [
        'Microsoft.Communication.SMSReceived'
      ]
    }
    eventDeliverySchema: 'EventGridSchema'
    retryPolicy: {
      maxDeliveryAttempts: 30
      eventTimeToLiveInMinutes: 1440
    }
  }
}

resource webhookSubscription 'Microsoft.EventGrid/systemTopics/eventSubscriptions@2025-02-15' = if (destinationType == 'WebHook') {
  parent: systemTopic
  name: eventSubscriptionName
  properties: {
    destination: {
      endpointType: 'WebHook'
      properties: {
        endpointUrl: webhookEndpointUrl
      }
    }
    filter: {
      includedEventTypes: [
        'Microsoft.Communication.SMSReceived'
      ]
    }
    eventDeliverySchema: 'EventGridSchema'
    retryPolicy: {
      maxDeliveryAttempts: 30
      eventTimeToLiveInMinutes: 1440
    }
  }
}

output systemTopicName string = systemTopic.name
output systemTopicResourceId string = systemTopic.id
output eventSubscriptionName string = eventSubscriptionName
