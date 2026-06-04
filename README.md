# USMGA Web Admin

Self-service website administration for the [United States Mounted Games Association](https://www.usmga.us)
(USMGA). Authorized board members **text a change request** to a phone number; the change is
implemented by the **GitHub Copilot coding agent**, deployed to a **preview environment**, and—after
an SMS approval—**published to production**. No GitHub account, build tooling, or web hosting
knowledge required for the requester.

## How it works

```
Board member ──SMS──▶ ACS number ──Event Grid──▶ Azure Function (SmsInbound)
   │                                                    │
   │                                                    ├─ creates a GitHub issue + assigns Copilot
   │                                                    │
   │                          Copilot coding agent works the issue ──▶ opens a PR
   │                                                    │
   │                          PR ──▶ GitHub Actions builds /site + deploys SWA preview
   │                                                    │
   ◀──SMS "preview URL + code + nonce"──  Function (NotifyRequester) ◀── workflow callback
   │
   ├─ reply  "APPROVE <code> <nonce>"  ──▶ Function merges the PR ──▶ Actions deploys production
   └─ reply  "CHANGES <code>: <text>"  ──▶ Function comments "@copilot …" on the PR (revise)
```

### SMS command grammar

| Message | Effect |
| --- | --- |
| any allowlisted text (not a command) | Opens a new change request (GitHub issue assigned to Copilot). |
| `APPROVE <code> <nonce>` | Merges the reviewed PR to production. Requires the request code **and** the unguessable nonce from the preview SMS, sent from the bound phone, matching the reviewed commit SHA with passing checks. |
| `CHANGES <code>: <text>` | Sends `<text>` to Copilot as a `@copilot` PR comment to revise the change; marks the preview stale. |

## Repository layout

| Path | Description |
| --- | --- |
| [`site/`](site/) | The public website, an [Eleventy](https://www.11ty.dev/) static site. Builds to `site/_site`. Hosted on Azure Static Web Apps. |
| [`func/`](func/) | Azure Functions app (C# .NET 8 isolated): `SmsInbound` (Event Grid) and `NotifyRequester` (secured HTTP). |
| [`infra/`](infra/) | Azure Bicep IaC for all resources (Static Web App, Communication Services, Event Grid, Function App, Storage, Key Vault, monitoring). |
| [`.github/workflows/`](.github/workflows/) | CI + deployment workflows (preview, production, function deploy). |
| [`.github/docs/copilot-assignment.md`](.github/docs/copilot-assignment.md) | How issues are dispatched to / linked back from the Copilot coding agent. |
| [`scripts/`](scripts/) | Operational scripts (e.g. branch-protection setup). |

## Architecture decisions

- **SMS:** Azure Communication Services (ACS). Inbound arrives via Event Grid (`Microsoft.Communication.SMSReceived`); outbound via the ACS SMS SDK. **ACS has no MMS** — attachments are handled by replying with an upload link, not by receiving media.
- **Hosting:** Azure Static Web Apps (Standard) — native per-PR **preview environments** plus production.
- **IaC:** Bicep.
- **Function runtime:** C# .NET 8 isolated, Azure Functions v4.
- **Copilot dispatch:** the agent is assigned via a **user fine-grained PAT** on a dedicated bot account (GitHub App *installation* tokens are not supported by Copilot). Re-engagement uses `@copilot` **PR** comments (issue comments are ignored after assignment).

## Safety model

- Inbound texts are restricted to an **allowlist** of board phone numbers (normalized to E.164).
- Approvals require an **unguessable, expiring nonce** bound to the requester's phone and the reviewed commit SHA — not just a short code.
- Merge happens only if the PR head SHA still equals the reviewed SHA **and** required GitHub Actions check-runs + commit statuses pass; the merge call pins the expected `sha` for atomicity.
- Event Grid is at-least-once; the function **deduplicates on `messageId`** (claim-then-finalize so transient failures can be retried).
- `NotifyRequester` requires both a Functions key and a shared-secret header.
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

1. Purchase an SMS-capable ACS phone number and complete toll-free / 10DLC verification (not automatable).
2. Store the ACS connection string and the GitHub bot PAT as Key Vault secrets.
3. Link the Static Web App to this GitHub repository and capture its deployment token.

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
(preview deploy + SMS notification).

## License

See [LICENSE](LICENSE).
