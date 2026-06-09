# USMGA SMS Function App

Azure Functions v4, C# .NET isolated worker. `SmsInbound` receives Twilio inbound SMS webhooks via HTTP POST, validates the Twilio request signature, allowlists board phones, claims each Twilio `MessageSid` during processing and only finalizes it after successful handling, creates GitHub issues assigned to `copilot-swe-agent[bot]`, and handles SMS approval/change commands. `NotifyRequester` is a secured HTTP endpoint for deployment workflows to text preview URLs.

## SMS grammar

- New request: any allowlisted text that is not a command.
- Approve: `APPROVE <code> <approval-nonce>`; code and nonce must match the bound phone and unexpired preview. Malformed `APPROVE` commands are rejected instead of being opened as new requests.
- Changes: `CHANGES <code>: <text>` posts `@copilot <text>` on the PR.

## Security model

GitHub uses a fine-grained PAT for a dedicated bot account with write access to `markjgardner/usmga-webadmin`; GitHub App installation tokens are intentionally not used. Startup flow verifies Copilot is assignable through GraphQL `suggestedActors`, and issue creation also verifies the returned issue has Copilot assigned before telling the requester a preview is coming. Merge approval requires the reviewed PR SHA plus passing commit statuses and GitHub Actions check-runs. Twilio webhook requests are validated using the `RequestValidator` with the account's Auth Token to prevent spoofing. Outbound SMS uses the Twilio REST API via `Twilio:AccountSid` and `Twilio:AuthToken`. `NotifyRequester` requires both a Functions key and the configured shared-secret header (default `x-usmga-notify-secret`).

## Configuration

Copy `Usmga.FunctionApp/local.settings.json.sample` to `local.settings.json` for local development and fill values. Do not commit real secrets. Required sections: `GitHub`, `Twilio`, `Storage`, and `Notify`.

## Build/test

```bash
cd func
dotnet build Usmga.Func.sln
dotnet test Usmga.Func.sln
```

Azure Functions Core Tools are optional for this track; the project is validated with `dotnet build` and tests.
