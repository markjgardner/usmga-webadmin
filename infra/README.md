# Infrastructure

Azure Bicep for the SMS-driven website-change pipeline. All files live under `infra/`.

## What is deployed

- Azure Static Web Apps, Standard SKU, for production and preview environments.
- Azure Communication Services (ACS) for inbound SMS integration.
- Event Grid system topic on ACS plus an event subscription filtered to `Microsoft.Communication.SMSReceived`.
- Linux Azure Function App for .NET isolated on Consumption (Y1).
- Storage account for Azure Functions runtime state and Table storage correlation state.
- Log Analytics workspace and Application Insights.
- Key Vault with RBAC enabled, plus `Key Vault Secrets User` granted to the Function App managed identity.

No secrets are hardcoded. Static Web Apps deployment tokens and ACS connection strings must be retrieved at deploy/operation time and stored securely.

## Layout

- `main.bicep` orchestrates the deployment.
- `main.parameters.json` is a non-secret test parameter file.
- `modules/static-web-app.bicep`
- `modules/communication-services.bicep`
- `modules/event-grid.bicep`
- `modules/function-app.bicep`
- `modules/storage.bicep`
- `modules/monitoring.bicep`
- `modules/key-vault.bicep`
- `modules/key-vault-role-assignment.bicep`

## Prerequisites

- Azure CLI with Bicep (`az bicep version`).
- An Azure subscription and resource group.
- Permission to create the listed resources and role assignments.
- Function code deployed before validating an Event Grid `AzureFunction` destination, or use the `WebHook` destination with a secured endpoint URL supplied at deployment time.

## Manual steps

1. **ACS phone number:** buy or port an SMS-capable phone number in ACS. Toll-free verification and/or 10DLC registration are manual/provider-governed and are not automatable in Bicep.
2. **ACS connection string:** retrieve it after ACS deployment and create the Key Vault secret named by `acsConnectionStringSecretName` (default `acs-connection-string`). The function reads it as `Sms__ConnectionString`.
3. **GitHub credential:** create a GitHub PAT or GitHub App credential with least privilege to open issues and, later, merge PRs. Store it in the Key Vault secret named by `githubCredentialSecretName` (default `github-credential`). The function reads it as `GitHub__Token`.
4. **Notify shared secret:** create the Key Vault secret named by `notifySharedSecretName` (default `notify-shared-secret`) with a random value, and set the same value as the `NOTIFY_SHARED_SECRET` GitHub Actions secret so the preview workflow can authenticate to `NotifyRequester`.
5. **SMS From number and allowlist:** pass `smsFromNumber` (the ACS-provisioned E.164 number) and `smsAllowlist` (comma-separated E.164 numbers permitted to submit requests) as deployment parameters. They become the `Sms__FromNumber` and `Sms__Allowlist` app settings.
6. **Static Web Apps GitHub link:** connect the Static Web App to this GitHub repository/workflow. Retrieve the SWA deployment token via Azure CLI/API when configuring workflows; do not commit it.
7. **Event Grid endpoint:** if the Function has not been deployed yet, deploy infra first with a webhook endpoint or create/update the Event Grid subscription after the function endpoint exists. The `AzureFunction` destination targets the `SmsInbound` function (`smsHandlerFunctionName`), which must match the `[Function("SmsInbound")]` name in the code.

## Deploy

Preview changes:

```bash
az deployment group what-if \
  --resource-group <resource-group> \
  --template-file infra/main.bicep \
  --parameters @infra/main.parameters.json
```

Deploy test:

```bash
az deployment group create \
  --resource-group <resource-group> \
  --template-file infra/main.bicep \
  --parameters @infra/main.parameters.json
```

Deploy prod by overriding environment values without editing in secrets:

```bash
az deployment group create \
  --resource-group <resource-group> \
  --template-file infra/main.bicep \
  --parameters @infra/main.parameters.json \
  --parameters environmentName=prod tags='{"project":"usmga-webadmin","environment":"prod","workload":"sms-website-change-pipeline","managedBy":"bicep"}'
```

For webhook delivery, pass the secured URL only at deployment time:

```bash
az deployment group create \
  --resource-group <resource-group> \
  --template-file infra/main.bicep \
  --parameters @infra/main.parameters.json \
  --parameters eventGridDestinationType=WebHook eventGridWebhookEndpointUrl='<secure-url>'
```

## Useful outputs for workflows and operations

- `staticWebAppName`
- `staticWebAppDefaultHostname`
- `functionAppName`
- `functionAppDefaultHostname`
- `smsHandlerAzureFunctionResourceId`
- `communicationServicesName`
- `eventGridSystemTopicName`
- `eventGridSubscriptionName`
- `storageAccountName`
- `correlationTableName`
- `keyVaultName`

Static Web Apps deployment token retrieval is intentionally not an output; retrieve it at deployment time through Azure CLI/API and store it in GitHub secrets.
