#!/usr/bin/env bash
set -euo pipefail

# Configure main branch protection for the SMS approval pipeline.
#
# WHY THIS MATTERS:
# The Azure Function can merge approved Copilot PRs after an SMS approval. Branch
# protection is the safety net that prevents the Function from merging code unless
# GitHub has first built and tested the PR successfully.
#
# IMPORTANT — two settings are tuned for the SMS auto-merge flow:
#   * REQUIRED_APPROVING_REVIEW_COUNT defaults to 0. A value > 0 would require a human
#     approving review, which BLOCKS the Function's PAT auto-merge (GitHub returns 405
#     for a non-admin bot merging with 0 approvals). The SMS approval + the required
#     status checks below are the gate. Set this to 0 unless you add the bot as a
#     branch-protection bypass actor or have a human approve every change.
#   * REQUIRED_CHECKS lists only the always-run CI jobs (ci.yml has no path filter).
#     Do NOT add path-filtered checks like "Deploy site preview" here — a required
#     context that doesn't report on every PR deadlocks merges forever. Preview success
#     is already enforced by the Function (it only merges after a preview was sent and
#     APPROVE was received for the reviewed SHA).
#
# Usage:
#   gh auth login
#   REPO=markjgardner/usmga-webadmin scripts/setup-branch-protection.sh
#
# Optional overrides:
#   BRANCH=main
#   REQUIRED_CHECKS="Site build,Function build and test,Infra Bicep validation"
#   ENFORCE_ADMINS=false
#   DISMISS_STALE_REVIEWS=true
#   REQUIRED_APPROVING_REVIEW_COUNT=0
#
# This script is intentionally not run by CI. A repository maintainer should review
# the required check names in GitHub after the first workflow run, then execute it.

REPO="${REPO:-markjgardner/usmga-webadmin}"
BRANCH="${BRANCH:-main}"
REQUIRED_CHECKS="${REQUIRED_CHECKS:-Site build,Function build and test,Infra Bicep validation}"
ENFORCE_ADMINS="${ENFORCE_ADMINS:-false}"
DISMISS_STALE_REVIEWS="${DISMISS_STALE_REVIEWS:-true}"
REQUIRED_APPROVING_REVIEW_COUNT="${REQUIRED_APPROVING_REVIEW_COUNT:-0}"

if ! command -v gh >/dev/null 2>&1; then
  echo "gh CLI is required: https://cli.github.com/" >&2
  exit 1
fi

if ! gh auth status >/dev/null 2>&1; then
  echo "gh CLI is not authenticated. Run: gh auth login" >&2
  exit 1
fi

contexts_json=$(jq -cn --arg checks "${REQUIRED_CHECKS}" '$checks | split(",") | map(gsub("^\\s+|\\s+$"; "")) | map(select(length > 0))')

echo "Configuring branch protection for ${REPO}:${BRANCH}"
echo "Required status checks: ${REQUIRED_CHECKS}"

jq -n \
  --argjson contexts "${contexts_json}" \
  --argjson enforceAdmins "${ENFORCE_ADMINS}" \
  --argjson dismissStale "${DISMISS_STALE_REVIEWS}" \
  --argjson reviewCount "${REQUIRED_APPROVING_REVIEW_COUNT}" \
  '{
    required_status_checks: {
      strict: true,
      contexts: $contexts
    },
    enforce_admins: $enforceAdmins,
    required_pull_request_reviews: {
      dismiss_stale_reviews: $dismissStale,
      require_code_owner_reviews: false,
      required_approving_review_count: $reviewCount
    },
    restrictions: null,
    required_linear_history: false,
    allow_force_pushes: false,
    allow_deletions: false,
    block_creations: false,
    required_conversation_resolution: true,
    lock_branch: false,
    allow_fork_syncing: true
  }' | gh api \
    --method PUT \
    --header 'Accept: application/vnd.github+json' \
    --header 'X-GitHub-Api-Version: 2022-11-28' \
    "repos/${REPO}/branches/${BRANCH}/protection" \
    --input -

echo "Branch protection updated. Confirm the required check names in repository settings."
