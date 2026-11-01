targetScope = 'resourceGroup'

@description('Region for the new environment and all networking resources.')
param location string = resourceGroup().location

@description('New workload profiles environment name; never the original default-network environment.')
param environmentName string = 'cae-d1-invoice-demo-nat'

@description('Existing Log Analytics workspace in this resource group.')
param logAnalyticsWorkspaceName string = 'log-d1-invoice-demo'

@description('Dedicated VNet name for the new environment.')
param virtualNetworkName string = 'vnet-d1-invoice-demo'

@description('Exclusive Container Apps infrastructure subnet name.')
param subnetName string = 'snet-containerapps'

@description('VNet IPv4 range; verify it does not overlap connected networks.')
param virtualNetworkAddressPrefix string = '10.42.0.0/16'

@description('Infrastructure subnet IPv4 range; /27 or larger for workload profiles.')
param subnetAddressPrefix string = '10.42.0.0/23'

@description('Standard NAT gateway name.')
param natGatewayName string = 'nat-d1-invoice-demo'

@description('Static Standard IPv4 public IP name for NAT egress.')
param publicIpName string = 'pip-d1-invoice-egress'

var tags = {
  'd1-demo': 'invoice-system'
}

resource workspace 'Microsoft.OperationalInsights/workspaces@2026-03-01' existing = {
  name: logAnalyticsWorkspaceName
}

resource publicIp 'Microsoft.Network/publicIPAddresses@2026-03-01' = {
  name: publicIpName
  location: location
  tags: tags
  sku: {
    name: 'Standard'
    tier: 'Regional'
  }
  properties: {
    publicIPAllocationMethod: 'Static'
    publicIPAddressVersion: 'IPv4'
  }
}

resource natGateway 'Microsoft.Network/natGateways@2026-03-01' = {
  name: natGatewayName
  location: location
  tags: tags
  sku: {
    name: 'Standard'
  }
  properties: {
    idleTimeoutInMinutes: 10
    publicIpAddresses: [
      {
        id: publicIp.id
      }
    ]
  }
}

resource virtualNetwork 'Microsoft.Network/virtualNetworks@2026-03-01' = {
  name: virtualNetworkName
  location: location
  tags: tags
  properties: {
    addressSpace: {
      addressPrefixes: [
        virtualNetworkAddressPrefix
      ]
    }
    subnets: [
      {
        name: subnetName
        properties: {
          addressPrefix: subnetAddressPrefix
          delegations: [
            {
              name: 'container-apps'
              properties: {
                serviceName: 'Microsoft.App/environments'
              }
            }
          ]
          natGateway: {
            id: natGateway.id
          }
        }
      }
    ]
  }
}

resource environment 'Microsoft.App/managedEnvironments@2026-07-01' = {
  name: environmentName
  location: location
  tags: tags
  properties: {
    publicNetworkAccess: 'Enabled'
    workloadProfiles: [
      {
        name: 'Consumption'
        workloadProfileType: 'Consumption'
      }
    ]
    vnetConfiguration: {
      infrastructureSubnetId: '${virtualNetwork.id}/subnets/${subnetName}'
      internal: false
    }
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: workspace.properties.customerId
        sharedKey: workspace.listKeys().primarySharedKey
      }
    }
  }
}

output environmentResourceId string = environment.id
output environmentDomain string = environment.properties.defaultDomain
output infrastructureSubnetId string = '${virtualNetwork.id}/subnets/${subnetName}'
output natGatewayResourceId string = natGateway.id
output publicIpResourceId string = publicIp.id
output egressIps array = [publicIp.properties.ipAddress]