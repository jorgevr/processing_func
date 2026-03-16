param appName string
param location string = resourceGroup().location
param serviceBusNamespace string
param onelakeEndpoint string
param otelServiceName string = 'dataset-processing-func'

module storage 'modules/storage.bicep' = {
  name: 'storage'
  params: {
    appName: appName
    location: location
  }
}

module appInsights 'modules/app-insights.bicep' = {
  name: 'app-insights'
  params: {
    appName: appName
    location: location
  }
}

module keyVault 'modules/key-vault.bicep' = {
  name: 'key-vault'
  params: {
    appName: appName
    location: location
  }
}

module serviceBus 'modules/service-bus.bicep' = {
  name: 'service-bus'
  params: {
    namespaceName: serviceBusNamespace
    location: location
  }
}

module functionApp 'modules/function-app.bicep' = {
  name: 'function-app'
  params: {
    appName: appName
    location: location
    storageAccountName: storage.outputs.storageAccountName
    appInsightsConnectionString: appInsights.outputs.connectionString
    serviceBusNamespace: serviceBusNamespace
    onelakeEndpoint: onelakeEndpoint
    otelServiceName: otelServiceName
    keyVaultName: keyVault.outputs.keyVaultName
  }
  dependsOn: [storage, appInsights, keyVault, serviceBus]
}
