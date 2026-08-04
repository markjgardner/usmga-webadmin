# USMGA Web Admin

Self-service website administration for the [United States Mounted Games Association](https://www.usmga.us)
(USMGA). Authorized board members **send a change request** to a Telegram bot; the change is
implemented by the **GitHub Copilot coding agent**, deployed to a **preview environment**, and—after
a Telegram approval—**published to production**. No GitHub account, build tooling, or web hosting
knowledge required for the requester.

## How it works

```
Board member ──Telegram──▶ Telegram Bot ──webhook──▶ Azure Function (TelegramInbound)
   │                                                    │
   │                                                    ├─ creates a GitHub issue + assigns Copilot
   │                                                    │
   │                          Copilot coding agent works the issue ──▶ opens a PR
   │                                                    │
   │                          PR ──▶ GitHub Actions builds /site + deploys SWA preview
   │                                                    │
   ◀──Telegram "preview URL + ✅ Approve / ✏️ Request changes"── Function (NotifyRequester)
   │
   ├─ tap ✅, or reply "looks good"  ──▶ Function merges the PR ──▶ Actions deploys production
   └─ tap ✏️, or just say what's wrong ──▶ Function comments "@copilot …" on the PR (revise)
```

### Talking to the bot

The bot is conversational: it works out **what** you mean and **which** request you mean from
what is currently in flight for your chat, so codes and nonces never have to be typed.

| You send | Effect |
| --- | --- |
| any text, with nothing in flight | Opens a new change request (GitHub issue assigned to Copilot). |
| tap **✅ Approve & publish** | Merges the reviewed PR to production. |
| "looks good" / "lgtm" / "ship it" / "I approve" | Same as tapping ✅. |
| "ok" / "nice" / "thanks" | Ambiguous, so the bot asks "publish X?" first; answer yes/no. |
| any other text, while a preview is waiting | Sent to Copilot as a revision to **that** request. |
| "not yet" / "hold on" | Declines publishing without abandoning the request. |
| `/new <change>` | Starts a **separate** request instead of revising the pending one. |
| `/status` | Lists everything in flight. |
| `/cancel` | Abandons the pending request (the PR stays open on GitHub). |
| `approve <code>` | Publishes a named request, for when several previews are waiting. |
| `APPROVE <code> <nonce>` / `CHANGES <code>: <text>` | Legacy explicit syntax; still supported. |

**Resolution order.** When a message doesn't name a request, the bot binds it to: the preview you
*replied to*, else the only preview awaiting approval, else the only request in flight. If more
than one preview is waiting, it asks which — it never guesses.

A phrase only counts as approval if the message reduces to *just* that phrase. "Looks good but make
the logo bigger" is a revision, not an approval.

## Repository layout

| Path | Description |
| --- | --- |
| [`site/`](site/) | The public website, an [Eleventy](https://www.11ty.dev/) static site. Builds to `site/_site`. Hosted on Azure Static Web Apps. |
| [`func/`](func/) | Azure Functions app (C# .NET 8 isolated): `TelegramInbound` (Telegram Bot webhook HTTP trigger) and `NotifyRequester` (secured HTTP). |
| [`infra/`](infra/) | Azure Bicep IaC for all resources (Static Web App, Function App, Storage, Key Vault, monitoring). |
| [`.github/workflows/`](.github/workflows/) | CI + deployment workflows (preview, production, function deploy). |
| [`.github/docs/copilot-assignment.md`](.github/docs/copilot-assignment.md) | How issues are dispatched to / linked back from the Copilot coding agent. |
| [`scripts/`](scripts/) | Operational scripts (e.g. branch-protection setup). |

## Architecture decisions

- **Telegram:** Inbound arrives via Telegram Bot webhook POST to an HTTP-triggered function; outbound replies are sent as Telegram messages. Telegram's webhook `secret_token` is validated via the `X-Telegram-Bot-Api-Secret-Token` header to prevent spoofing. Attachments are handled by replying with an upload link.
- **Hosting:** Azure Static Web Apps (Standard) — native per-PR **preview environments** plus production.
- **IaC:** Bicep.
- **Function runtime:** C# .NET 8 isolated, Azure Functions v4.
- **Copilot dispatch:** the agent is assigned via a **user fine-grained PAT** on a dedicated bot account (GitHub App *installation* tokens are not supported by Copilot). Re-engagement uses `@copilot` **PR** comments (issue comments are ignored after assignment).

## Safety model

- Inbound messages are restricted to an **allowlist** of numeric Telegram user IDs, enforced on button taps (`callback_query.from.id`) as well as on messages.
- Approvals still require an **unguessable, expiring nonce** bound to the requester's Telegram user ID and the reviewed commit SHA. Conversational approval removes the need to *retype* it, not the need for it to exist: a request is only publishable while its nonce is live, and a button carries that nonce in its `callback_data`.
- Merge happens only if the PR head SHA still equals the reviewed SHA **and** required GitHub Actions check-runs + commit statuses pass; the merge call pins the expected `sha` for atomicity. **This, not the nonce, is what makes approval safe** — you can only publish the exact commit you previewed, and only if it is green.
- The nonce is cleared on merge and on every revision, so a stale button or a repeated phrase cannot publish twice.
- Ambiguous approvals ("ok", "nice") require an explicit confirmation before anything is published; "no" declines without destroying the request.
- Telegram webhooks are validated by comparing `X-Telegram-Bot-Api-Secret-Token` to the configured webhook secret in constant time; additionally the Function uses `AuthorizationLevel.Function` for defense-in-depth.
- The function **deduplicates on Telegram update IDs** (claim-then-finalize so transient failures can be retried).
- `NotifyRequester` requires both a Functions key and a shared-secret header.
- `NotifyRequester` returns `204 No Content` when a preview has no originating request (a human-authored PR), so those previews do not fail the workflow.
- **Branch protection** on `main` (see `scripts/setup-branch-protection.sh`) is the backstop that prevents merging unbuilt/failing code.

## Getting started (development)

The [dev container](.devcontainer/devcontainer.json) provides every toolchain: Azure CLI + Bicep,
Node LTS, **.NET 8 SDK**, **Azure Functions Core Tools**, Terraform, kubectl, and the GitHub CLI.

```bash
# Static site
cd site && npm ci && npm run build      # output: site/_site
npm run serve                           # local preview

# Function app
cd func && dotnet build Usmga.Func.sln && dotnet test Usmga.Func.sln

# Infrastructure (compile/lint without deploying)
cd infra && az bicep build --file main.bicep
```

## Deployment

### 1. Provision infrastructure

```bash
az group create -n <rg> -l <location>
az deployment group create -g <rg> -f infra/main.bicep -p infra/main.parameters.json
```

Then complete the **manual steps** documented in [`infra/README.md`](infra/README.md):

1. Create a Telegram bot with BotFather and register its webhook to point at the Function App's `/api/telegram/webhook` endpoint, using a `secret_token`. Use [`scripts/register-telegram-webhook.sh`](scripts/register-telegram-webhook.sh) — it sets the required `allowed_updates`.
2. Store the Telegram bot token, Telegram webhook secret, and the GitHub bot PAT as Key Vault secrets.
3. Link the Static Web App to this GitHub repository and capture its deployment token.

> **The webhook must be registered with `allowed_updates=["message","callback_query"]`.** Telegram
> defaults to omitting `callback_query`, and drops those updates *before* they reach the Function —
> the ✅/✏️ buttons would silently do nothing, with no error anywhere. `setWebhook` also resets this
> list whenever it is called without `allowed_updates`.

Telegram Key Vault secrets:

| Secret | Value |
| --- | --- |
| `telegram-bot-token` | Bot token from BotFather |
| `telegram-webhook-secret` | Secret token supplied to Telegram `setWebhook` and validated on inbound requests |

Telegram app settings:

| Setting | Value |
| --- | --- |
| `Telegram__Allowlist` | Comma-separated numeric Telegram user IDs for authorized board members |
| `Telegram__UploadBaseUrl` | Base URL sent when a requester needs to upload an attachment |

### 2. Configure GitHub secrets

Set these repository secrets (see [`.github/workflows/README.md`](.github/workflows/README.md)):

| Secret | Source |
| --- | --- |
| `AZURE_STATIC_WEB_APPS_API_TOKEN` | Static Web App deployment token |
| `AZURE_FUNCTIONAPP_NAME` | Bicep output / portal |
| `AZURE_FUNCTIONAPP_PUBLISH_PROFILE` | Function App publish profile |
| `NOTIFY_FUNCTION_URL` | `NotifyRequester` URL (incl. Functions key) |
| `NOTIFY_SHARED_SECRET` | value for the `x-usmga-notify-secret` header |

### 3. Enable the Copilot coding agent & branch protection

- Enable the Copilot coding agent for the repo/org and confirm the bot PAT can assign it
  (see [`.github/docs/copilot-assignment.md`](.github/docs/copilot-assignment.md)).
- After the first CI run, lock down `main`:

  ```bash
  REPO=markjgardner/usmga-webadmin scripts/setup-branch-protection.sh
  ```

### 4. Deploy code

Pushing to `main` deploys automatically: `func-deploy.yml` (on `func/**`) and `site-prod.yml`
(on `site/**`). Pull requests trigger `ci.yml` (build validation) and `site-preview.yml`
(preview deploy + Telegram notification).

## License

See [LICENSE](LICENSE).
