# Copilot instructions for usmga-webadmin

## Build, test, and deploy

```bash
# Static site (Eleventy)
cd site && npm ci && npm run build        # output: site/_site

# Function app (.NET 8 isolated)
export PATH="/home/vscode/.dotnet:$PATH"  # devcontainer SDK path
dotnet build func/Usmga.Func.sln
dotnet test func/Usmga.Func.sln

# Run a single test class
dotnet test func/Usmga.Func.sln --filter "FullyQualifiedName~ClassifierTests"

# Validate infrastructure (no deploy)
az bicep build --file infra/main.bicep
```

## Architecture

Three independent stacks share this repo:

| Stack | Tech | Entry point |
|-------|------|-------------|
| `site/` | Eleventy 3 (Nunjucks, ESM) | `site/.eleventy.js` → builds `site/_site` |
| `func/` | C# .NET 8 isolated Azure Functions v4 | `func/Usmga.FunctionApp/Program.cs` |
| `infra/` | Bicep modules | `infra/main.bicep` orchestrates all modules |

### Telegram → Copilot pipeline (func/)

The function app has two thin entry-point functions that delegate to a service layer:

- **TelegramInbound** (HTTP trigger, `POST /api/telegram/webhook`) — receives Telegram Bot webhooks, validates the `X-Telegram-Bot-Api-Secret-Token` header against the configured webhook secret in constant time, deduplicates updates (claim/complete/release pattern), classifies the message, and dispatches to `RequestProcessor`.
- **NotifyRequester** (HTTP trigger) — receives GitHub Actions callbacks with preview URLs; validates a shared secret header before sending Telegram replies.

Core orchestration lives in `RequestProcessor`, which handles new requests (creates GitHub issues + assigns Copilot), approvals (merges PRs with SHA + status checks guard), and change requests (`@copilot` PR comments).

### DI and configuration

`Program.cs` uses the options pattern binding four config sections:

| Section | Env var prefix | Source |
|---------|---------------|--------|
| `GitHub` | `GitHub__` | Key Vault reference |
| `Telegram` | `Telegram__` | Key Vault (BotToken, WebhookSecret) + app settings (Allowlist, UploadBaseUrl) |
| `Storage` | `Storage__` | App setting (connection string + table name) |
| `Notify` | `Notify__` | Key Vault reference |

All services are registered as singletons. `IGitHubClient` uses `AddHttpClient<>` for typed HTTP.

## Conventions

### C# / func

- All public classes are `sealed`; models are `sealed record`.
- Nullable reference types and implicit usings are enabled.
- Security-sensitive comparisons use constant-time (`CryptographicOperations.FixedTimeEquals` or equivalent).
- Idempotency: inbound messages use claim-then-finalize with retry-safe release on exception.
- Namespaces follow folder structure: `Usmga.FunctionApp.{Functions,Services,Models,Options}`.

### Tests

- xUnit with `[Fact]` / `[Theory]` + `[InlineData]`.
- No mocking framework; tests use `InMemoryStateStore` and simple fakes.
- Test files are named `{Feature}Tests.cs` (e.g., `ClassifierTests.cs`, `ApproveGuardTests.cs`).

### Site

- Nunjucks templates in `site/src/`, layout in `site/src/_includes/base.njk`.
- Navigation defined in `site/src/_data/navigation.json`.
- Static assets passthrough-copied from `site/src/assets/`.

### Infrastructure

- Bicep modules under `infra/modules/`; the orchestrator is `infra/main.bicep`.
- App settings requiring secrets use Key Vault references (`@Microsoft.KeyVault(SecretUri=...)`).
- Telegram credentials (`telegram-bot-token` + `telegram-webhook-secret`) are stored in Key Vault; numeric user ID allowlist and upload base URL are plain app settings.

## CI/CD workflows

| Workflow | Trigger | Purpose |
|----------|---------|---------|
| `ci.yml` | PR (any path) | Build validation: site + func + Bicep |
| `site-preview.yml` | PR (`site/**`) | Deploy SWA preview + Telegram notification |
| `site-prod.yml` | Push to main (`site/**`) | Deploy SWA production |
| `func-deploy.yml` | Push to main (`func/**`) | Build, test, publish function app |

## Copilot coding agent integration

- Issues are dispatched to Copilot by assigning `copilot-swe-agent[bot]` via a user PAT (GitHub App tokens are not supported).
- The same bot is reported under different logins depending on the API (`copilot-swe-agent` from GraphQL `suggestedActors`, `Copilot` on REST assignees, `app/copilot-swe-agent` on PR authors). Match it by node id where possible — never by a single hard-coded login.
- Copilot PRs come from `copilot/` branches.
- Re-engage Copilot via `@copilot` **PR comments** only (issue comments are ignored after assignment).
