# Copilot coding agent assignment

To dispatch SMS-created issues to Copilot, enable the Copilot coding agent for the repository or organization, then use a dedicated bot account with a fine-grained PAT that has write access to `markjgardner/usmga-webadmin`. GitHub App installation tokens are not supported for this flow.

Before assigning an issue, verify that Copilot is eligible with `suggestedActors(capabilities:[CAN_BE_ASSIGNED])`:

```graphql
query($owner: String!, $repo: String!, $issue: Int!) {
  repository(owner: $owner, name: $repo) {
    issue(number: $issue) {
      suggestedActors(first: 50, capabilities: [CAN_BE_ASSIGNED]) {
        nodes {
          login
          __typename
        }
      }
    }
  }
}
```

Example:

```bash
gh api graphql \
  -f owner=markjgardner \
  -f repo=usmga-webadmin \
  -F issue=123 \
  -f query='query($owner: String!, $repo: String!, $issue: Int!) { repository(owner: $owner, name: $repo) { issue(number: $issue) { suggestedActors(first: 50, capabilities: [CAN_BE_ASSIGNED]) { nodes { login __typename } } } } }'
```

A successful Copilot run opens a PR from a `copilot/` branch authored by `copilot-swe-agent[bot]`. The Function should correlate the original issue to that PR by the linked issue, the `copilot/` head branch prefix, and the bot author.

To re-engage Copilot after review or SMS change requests, comment on the PR with `@copilot` and the requested changes. Copilot will update the same PR when repository settings and permissions allow it.
