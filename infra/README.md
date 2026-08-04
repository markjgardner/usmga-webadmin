# Infrastructure

Azure Bicep for the Telegram-driven website-change pipeline. All files live under `infra/`.

## What is deployed

- Azure Static Web Apps, Standard SKU, for production and preview environments.
- Linux Azure Function App for .NET isolated on Consumption (Y1).
- Storage account for Azure Functions runtime state and Table storage correlation state.
- Log Analytics workspace and Application Insights.
- Key Vault with RBAC enabled, plus `Key Vault Secrets User` granted to the Function App managed identity.

No secrets are hardcoded. Telegram credentials and other secrets must be stored in Key Vault; Static Web Apps deployment tokens are retrieved at deploy/operation time.

## Layout

- `main.bicep` orchestrates the deployment.
- `main.parameters.json` is a non-secret test parameter file.
- `modules/static-web-app.bicep`
- `modules/function-app.bicep`
- `modules/storage.bicep`
- `modules/monitoring.bicep`
- `modules/key-vault.bicep`
- `modules/key-vault-role-assignment.bicep`

## Prerequisites

- Azure CLI with Bicep (`az bicep version`).
- An Azure subscription and resource group.
- Permission to create the listed resources and role assignments.
- A Telegram bot created with BotFather.

## Manual steps

1. **Telegram bot token:** create a Telegram bot with BotFather and store its token in the Key Vault secret named by `telegramBotTokenSecretName` (default `telegram-bot-token`). The function reads it as `Telegram__BotToken`.
2. **Telegram webhook secret:** create a random secret token, store it in the Key Vault secret named by `telegramWebhookSecretName` (default `telegram-webhook-secret`), and register it with Telegram `setWebhook`. Telegram sends it on the `X-Telegram-Bot-Api-Secret-Token` header, and the function reads it as `Telegram__WebhookSecret`.
3. **Telegram webhook URL:** configure the Telegram webhook URL to `https://<function-app>.azurewebsites.net/api/telegram/webhook?code=<function-key>`.
4. **GitHub credential:** create a GitHub PAT or GitHub App credential with least privilege to open issues and, later, merge PRs. Store it in the Key Vault secret named by `githubCredentialSecretName` (default `github-credential`). The function reads it as `GitHub__Token`.
5. **Notify shared secret:** create the Key Vault secret named by `notifySharedSecretName` (default `notify-shared-secret`) with a random value, and set the same value as the `NOTIFY_SHARED_SECRET` GitHub Actions secret so the preview workflow can authenticate to `NotifyRequester`.
6. **Telegram allowlist:** pass `telegramAllowlist` (comma-separated Telegram numeric user IDs permitted to submit requests) as a deployment parameter. It becomes the `Telegram__Allowlist` app setting.
7. **Static Web Apps GitHub link:** connect the Static Web App to this GitHub repository/workflow. Retrieve the SWA deployment token via Azure CLI/API when configuring workflows; do not commit it.

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

Deploy prod by overriding environment values:

```bash
az deployment group create \
  --resource-group <resource-group> \
  --template-file infra/main.bicep \
  --parameters @infra/main.parameters.json \
  --parameters environmentName=prod tags='{"project":"usmga-webadmin","environment":"prod","workload":"telegram-website-change-pipeline","managedBy":"bicep"}'
```

## Useful outputs for workflows and operations

- `staticWebAppName`
- `staticWebAppDefaultHostname`
- `functionAppName`
- `functionAppDefaultHostname`
- `functionAppResourceId`
- `storageAccountName`
- `correlationTableName`
- `keyVaultName`

Static Web Apps deployment token retrieval is intentionally not an output; retrieve it at deployment time through Azure CLI/API and store it in GitHub secrets.
