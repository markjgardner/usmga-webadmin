# GitHub Actions workflows

Required secrets:

- `AZURE_STATIC_WEB_APPS_API_TOKEN` — deployment token for the Azure Static Web Apps resource; used for PR previews and production site deploys.
- `AZURE_FUNCTIONAPP_NAME` — Azure Function App name from the Bicep outputs or Azure portal.
- `AZURE_FUNCTIONAPP_PUBLISH_PROFILE` — Function App publish profile XML from Azure portal.
- `NOTIFY_FUNCTION_URL` — full `NotifyRequester` Function URL, including the Functions key query string if required.
- `NOTIFY_SHARED_SECRET` — value sent in the `x-usmga-notify-secret` header so `NotifyRequester` accepts deployment notifications.

The Azure resource names and tokens come from the infra deployment outputs and Azure portal. `site-preview.yml` stays on `pull_request` (not `pull_request_target`) so secrets are not exposed to untrusted fork code; same-repository Copilot PRs can use the secrets after workflow approval when required.
