# USMGA Telegram Function App

Azure Functions v4, C# .NET isolated worker. `TelegramInbound` receives Telegram Bot webhooks via HTTP POST at `/api/telegram/webhook`, validates the `X-Telegram-Bot-Api-Secret-Token` header in constant time, allowlists numeric Telegram user IDs, claims each update during processing and only finalizes it after successful handling, creates GitHub issues assigned to `copilot-swe-agent[bot]`, and handles Telegram approval/change commands. `NotifyRequester` is a secured HTTP endpoint for deployment workflows to send preview URLs as Telegram messages.

## Telegram grammar

- New request: any allowlisted text that is not a command.
- Approve: `APPROVE <code> <approval-nonce>`; code and nonce must match the bound Telegram user ID and unexpired preview. Malformed `APPROVE` commands are rejected instead of being opened as new requests.
- Changes: `CHANGES <code>: <text>` posts `@copilot <text>` on the PR.

## Security model

GitHub uses a fine-grained PAT for a dedicated bot account with write access to `markjgardner/usmga-webadmin`; GitHub App installation tokens are intentionally not used. Startup flow verifies Copilot is assignable through GraphQL `suggestedActors`, and issue creation also verifies the returned issue has Copilot assigned before telling the requester a preview is coming. Merge approval requires the reviewed PR SHA plus passing commit statuses and GitHub Actions check-runs. Telegram webhook requests are validated with the configured `Telegram:WebhookSecret` and the `X-Telegram-Bot-Api-Secret-Token` header to prevent spoofing. Outbound Telegram messages use the bot token from `Telegram:BotToken`; requester-initiated chat requires the user to press Start or message the bot once before notifications can be delivered. `NotifyRequester` requires both a Functions key and the configured shared-secret header (default `x-usmga-notify-secret`).

## Configuration

Copy `Usmga.FunctionApp/local.settings.json.sample` to `local.settings.json` for local development and fill values. Do not commit real secrets. Required sections: `GitHub`, `Telegram`, `Storage`, and `Notify`.

| Section | Key | Source |
| --- | --- | --- |
| `Telegram` | `BotToken` | Bot token from BotFather, stored in Key Vault as `telegram-bot-token` |
| `Telegram` | `WebhookSecret` | Secret token supplied to Telegram `setWebhook`, stored in Key Vault as `telegram-webhook-secret` |
| `Telegram` | `Allowlist` | Comma-separated numeric Telegram user IDs, plain app setting |
| `Telegram` | `UploadBaseUrl` | Plain app setting used for upload-link replies |

Register the webhook with Telegram using the Function endpoint and the same secret token:

```bash
curl --request POST "https://api.telegram.org/bot<bot-token>/setWebhook" \
  --header "content-type: application/json" \
  --data '{"url":"https://<function-app>.azurewebsites.net/api/telegram/webhook?code=<function-key>","secret_token":"<webhook-secret>"}'
```

Example inbound update for local testing:

```bash
curl --request POST "http://localhost:7071/api/telegram/webhook" \
  --header "content-type: application/json" \
  --header "X-Telegram-Bot-Api-Secret-Token: <webhook-secret>" \
  --data '{
    "update_id": 10001,
    "message": {
      "message_id": 5,
      "from": { "id": 123456789, "is_bot": false, "first_name": "Board" },
      "chat": { "id": 123456789, "type": "private" },
      "date": 1720000000,
      "text": "Update the homepage headline"
    }
  }'
```

## Build/test

```bash
cd func
dotnet build Usmga.Func.sln
dotnet test Usmga.Func.sln
```

Azure Functions Core Tools are optional for this track; the project is validated with `dotnet build` and tests.
