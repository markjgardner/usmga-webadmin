#!/usr/bin/env bash
set -euo pipefail

# Register the Telegram webhook for the USMGA web-admin bot.
#
# WHY THIS MATTERS:
# Telegram only delivers the update types listed in `allowed_updates`. The bot sends
# inline keyboard buttons ("Approve & publish" / "Request changes") with the preview
# message, and a button tap arrives as a `callback_query` update -- NOT a `message`.
#
# If `callback_query` is missing from allowed_updates the buttons still render, but
# tapping them does nothing at all: Telegram drops the update before it reaches the
# Function, so there is no log line, no error, and no reply. The user sees a button
# that silently fails. Always include `callback_query`.
#
# Passing an empty/omitted allowed_updates is NOT equivalent -- Telegram then uses a
# default list, and any later setWebhook call that omits it will reset the value. Be
# explicit.
#
# Usage:
#   TELEGRAM_BOT_TOKEN=... TELEGRAM_WEBHOOK_SECRET=... FUNCTION_URL=... \
#     scripts/register-telegram-webhook.sh
#
# The values live in Key Vault (`telegram-bot-token`, `telegram-webhook-secret`); the
# FUNCTION_URL must include the function key, e.g.
#   https://<app>.azurewebsites.net/api/telegram/webhook?code=<function-key>
#
# Optional overrides:
#   ALLOWED_UPDATES='["message","callback_query"]'

ALLOWED_UPDATES="${ALLOWED_UPDATES:-[\"message\",\"callback_query\"]}"

: "${TELEGRAM_BOT_TOKEN:?TELEGRAM_BOT_TOKEN is required}"
: "${TELEGRAM_WEBHOOK_SECRET:?TELEGRAM_WEBHOOK_SECRET is required}"
: "${FUNCTION_URL:?FUNCTION_URL is required}"

api="https://api.telegram.org/bot${TELEGRAM_BOT_TOKEN}"

echo "Registering webhook with allowed_updates=${ALLOWED_UPDATES}"

response=$(curl -sS --fail-with-body -X POST "${api}/setWebhook" \
  -H 'Content-Type: application/json' \
  -d "$(cat <<JSON
{
  "url": "${FUNCTION_URL}",
  "secret_token": "${TELEGRAM_WEBHOOK_SECRET}",
  "allowed_updates": ${ALLOWED_UPDATES}
}
JSON
)")

echo "${response}"

if ! grep -q '"ok":true' <<<"${response}"; then
  echo "setWebhook failed" >&2
  exit 1
fi

echo
echo "Verifying..."
info=$(curl -sS "${api}/getWebhookInfo")

python3 - "$info" <<'PY'
import json
import sys

info = json.loads(sys.argv[1])["result"]
allowed = info.get("allowed_updates") or []

print(f"  url             : {info.get('url', '').split('?')[0]}")
print(f"  allowed_updates : {allowed or '(default)'}")
print(f"  pending updates : {info.get('pending_update_count')}")
print(f"  last error      : {info.get('last_error_message', '(none)')}")

if "callback_query" not in allowed:
    print()
    print("ERROR: callback_query is not in allowed_updates -- button taps will be", file=sys.stderr)
    print("silently dropped by Telegram before they reach the Function.", file=sys.stderr)
    sys.exit(1)

print()
print("OK: callback_query is subscribed; inline keyboard buttons will be delivered.")
PY
