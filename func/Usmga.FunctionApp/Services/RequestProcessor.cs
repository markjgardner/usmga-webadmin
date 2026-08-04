using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Usmga.FunctionApp.Models;
using Usmga.FunctionApp.Options;

namespace Usmga.FunctionApp.Services;

public sealed class RequestProcessor
{
    private static readonly TimeSpan ConfirmationWindow = TimeSpan.FromMinutes(10);

    private readonly IGitHubClient _gitHub;
    private readonly IMessageChannel _channel;
    private readonly IStateStore _state;
    private readonly ITokenGenerator _tokens;
    private readonly MessageClassifier _classifier;
    private readonly IIntentClassifier _intents;
    private readonly TelegramOptions _telegramOptions;
    private readonly ILogger<RequestProcessor> _logger;

    public RequestProcessor(
        IGitHubClient gitHub,
        IMessageChannel channel,
        IStateStore state,
        ITokenGenerator tokens,
        MessageClassifier classifier,
        IIntentClassifier intents,
        IOptions<TelegramOptions> telegramOptions,
        ILogger<RequestProcessor> logger)
    {
        _gitHub = gitHub;
        _channel = channel;
        _state = state;
        _tokens = tokens;
        _classifier = classifier;
        _intents = intents;
        _telegramOptions = telegramOptions.Value;
        _logger = logger;
    }

    // ---------------------------------------------------------------- conversational entry

    /// <summary>
    /// Routes a free-form message using what is currently in flight for the chat, so the
    /// user never has to quote a request code or an approval nonce.
    /// </summary>
    public async Task HandleMessageAsync(string from, string text, long? replyToMessageId, CancellationToken cancellationToken)
    {
        var context = await BuildContextAsync(from, replyToMessageId, cancellationToken);
        var intent = _intents.Classify(text, context);

        switch (intent.Kind)
        {
            case IntentKind.Help:
                await SendHelpAsync(from, context, cancellationToken);
                return;

            case IntentKind.Status:
                await SendStatusAsync(from, context, cancellationToken);
                return;

            case IntentKind.Cancel:
                await HandleCancelAsync(from, context, cancellationToken);
                return;

            case IntentKind.Decline:
                await HandleDeclineAsync(from, context, cancellationToken);
                return;

            case IntentKind.NewRequest:
                await HandleNewRequestAsync(from, intent.Text, cancellationToken);
                return;

            case IntentKind.Approve:
                await HandleResolvedApproveAsync(from, intent, context, cancellationToken);
                return;

            case IntentKind.Changes:
                await HandleResolvedChangesAsync(from, intent, context, cancellationToken);
                return;

            default:
                await SendHelpAsync(from, context, cancellationToken);
                return;
        }
    }

    /// <summary>Handles an inline keyboard tap.</summary>
    /// <remarks>
    /// The callback payload carries the approval nonce, so this path is validated exactly
    /// as strictly as the typed <c>APPROVE code nonce</c> command.
    /// </remarks>
    public async Task HandleCallbackAsync(string from, string callbackQueryId, string? data, long? messageId, CancellationToken cancellationToken)
    {
        var action = CallbackAction.Parse(data);
        if (action is null)
        {
            _logger.LogWarning("Unparseable callback_data from chat {ChatId}", from);
            await _channel.AcknowledgeAsync(callbackQueryId, "That button is no longer valid.", cancellationToken);
            return;
        }

        switch (action.Kind)
        {
            case CallbackActionKind.Approve:
                await _channel.AcknowledgeAsync(callbackQueryId, "Publishing…", cancellationToken);
                if (messageId is not null)
                {
                    await _channel.ClearButtonsAsync(from, messageId.Value, cancellationToken);
                }
                await HandleApproveAsync(from, action.Code, action.ApprovalNonce!, cancellationToken);
                return;

            case CallbackActionKind.Changes:
                await _channel.AcknowledgeAsync(callbackQueryId, null, cancellationToken);
                // No extra state needed: while a preview is pending, the next free-form
                // message is already routed to this request as a revision.
                await _channel.SendAsync(from, $"What would you like changed for {action.Code}? Just tell me in your next message.", cancellationToken);
                return;

            default:
                await _channel.AcknowledgeAsync(callbackQueryId, null, cancellationToken);
                return;
        }
    }

    private async Task<ConversationContext> BuildContextAsync(string from, long? replyToMessageId, CancellationToken cancellationToken)
    {
        var active = await _state.ListActiveForChatAsync(from, cancellationToken);
        var repliedTo = replyToMessageId is null
            ? null
            : active.FirstOrDefault(r => r.PreviewMessageId == replyToMessageId);
        return new ConversationContext(active, repliedTo);
    }

    private async Task HandleResolvedApproveAsync(string from, IntentResult intent, ConversationContext context, CancellationToken cancellationToken)
    {
        if (intent.Code is not null && intent.ApprovalNonce is not null)
        {
            await HandleApproveAsync(from, intent.Code, intent.ApprovalNonce, cancellationToken);
            return;
        }

        // The user named a request but not a nonce ("approve ABC123"). The nonce still has
        // to exist and be live — it just comes from stored state instead of being retyped.
        if (intent.Code is not null)
        {
            var named = context.ActiveRequests.FirstOrDefault(r => StringComparer.OrdinalIgnoreCase.Equals(r.Code, intent.Code));
            if (named is null)
            {
                await _channel.SendAsync(from, $"Request {intent.Code} was not found for this chat.", cancellationToken);
                return;
            }

            if (!named.IsApprovable())
            {
                await _channel.SendAsync(from, $"Request {named.Code} isn't ready to publish — it's {DescribeStatus(named)}.", cancellationToken);
                return;
            }

            await ExecuteApprovalAsync(from, named, cancellationToken);
            return;
        }

        // An explicit reply names its target, so it outranks everything else — including
        // the "which one?" prompt that multiple pending previews would otherwise trigger.
        var record = context.RepliedToRequest is not null && context.RepliedToRequest.IsApprovable()
            ? context.RepliedToRequest
            : null;

        if (record is null)
        {
            var candidates = context.Approvable;
            if (candidates.Count == 0)
            {
                await _channel.SendAsync(from, DescribeNothingToApprove(context), cancellationToken);
                return;
            }

            if (candidates.Count > 1)
            {
                await AskWhichAsync(from, candidates, cancellationToken);
                return;
            }

            record = candidates[0];
        }

        // Vague wording ("ok", "nice") gets one confirmation step before anything is
        // published. Clear wording ("lgtm", "I approve") goes straight through.
        if (intent.Confidence < IntentConfidence.High && !record.IsAwaitingConfirmation())
        {
            record.AwaitingConfirmationUntil = DateTimeOffset.UtcNow.Add(ConfirmationWindow);
            await _state.SaveRequestAsync(record, cancellationToken);
            await _channel.SendAsync(
                from,
                $"Just to confirm — publish {record.Code} to the live site?",
                new[]
                {
                    new MessageButton("✅ Yes, publish", CallbackAction.Format(CallbackActionKind.Approve, record.Code, record.ApprovalNonce)),
                    new MessageButton("✏️ No, change something", CallbackAction.Format(CallbackActionKind.Changes, record.Code))
                },
                cancellationToken);
            return;
        }

        await ExecuteApprovalAsync(from, record, cancellationToken);
    }

    private async Task HandleResolvedChangesAsync(string from, IntentResult intent, ConversationContext context, CancellationToken cancellationToken)
    {
        if (intent.Code is not null)
        {
            await HandleChangesAsync(from, intent.Code, intent.Text, cancellationToken);
            return;
        }

        var record = context.Implied;
        if (record is null)
        {
            // Nothing unambiguous to revise, so this becomes a new request rather than
            // discarding what the user wrote. Say so when there were other candidates, or
            // a revision aimed at one of several previews would silently become a new one.
            if (context.ActiveRequests.Count > 0)
            {
                await _channel.SendAsync(
                    from,
                    $"I couldn't tell which request you meant ({string.Join(", ", context.ActiveRequests.Select(r => r.Code))}), so I'm starting a new one. To revise an existing request instead, reply to its preview message.",
                    cancellationToken);
            }

            await HandleNewRequestAsync(from, intent.Text, cancellationToken);
            return;
        }

        await ApplyChangesAsync(from, record, intent.Text, revisionWasInferred: true, cancellationToken);
    }

    private async Task AskWhichAsync(string from, IReadOnlyList<RequestRecord> candidates, CancellationToken cancellationToken)
    {
        var buttons = candidates
            .Take(5)
            .Select(r => new MessageButton($"✅ {r.Code} — {Summarize(r.OriginalMessage)}", CallbackAction.Format(CallbackActionKind.Approve, r.Code, r.ApprovalNonce)))
            .ToArray();

        await _channel.SendAsync(from, "You have more than one preview waiting. Which should I publish?", buttons, cancellationToken);
    }

    private async Task SendStatusAsync(string from, ConversationContext context, CancellationToken cancellationToken)
    {
        if (context.ActiveRequests.Count == 0)
        {
            await _channel.SendAsync(from, "Nothing is in flight right now. Send me a change and I'll get Copilot on it.", cancellationToken);
            return;
        }

        var lines = context.ActiveRequests.Select(r => $"• {r.Code} — {DescribeStatus(r)} — {Summarize(r.OriginalMessage)}");
        await _channel.SendAsync(from, "In flight:\n" + string.Join('\n', lines), cancellationToken);
    }

    private async Task SendHelpAsync(string from, ConversationContext context, CancellationToken cancellationToken)
    {
        var help = """
            Just talk to me normally.

            • Describe a change and I'll get Copilot started on it.
            • When a preview is ready, tap ✅ Approve, or reply "looks good".
            • Or just tell me what to fix and I'll pass it on.

            Commands, if you prefer:
            /new <change>  start a new request
            /status        what's in flight
            /cancel        drop the current request
            """;

        if (context.HasPendingPreview)
        {
            help += $"\n\nWaiting on you: {string.Join(", ", context.Approvable.Select(r => r.Code))}";
        }

        await _channel.SendAsync(from, help, cancellationToken);
    }

    private async Task HandleCancelAsync(string from, ConversationContext context, CancellationToken cancellationToken)
    {
        var record = context.Implied;
        if (record is null)
        {
            await _channel.SendAsync(from, "There's nothing in flight to cancel.", cancellationToken);
            return;
        }

        record.Status = RequestStatus.Cancelled;
        record.AwaitingConfirmationUntil = null;
        record.ApprovalNonce = null;
        record.ApprovalNonceExpiresAt = null;
        await _state.SaveRequestAsync(record, cancellationToken);
        await ClearPreviewButtonsAsync(from, record, cancellationToken);

        await _channel.SendAsync(from, $"Cancelled {record.Code}. Its pull request is still open on GitHub if you want it later.", cancellationToken);
    }

    /// <summary>
    /// Answers "no" to a publish prompt. Deliberately non-destructive: it only withdraws
    /// the pending confirmation, leaving the preview approvable if the user changes their mind.
    /// </summary>
    private async Task HandleDeclineAsync(string from, ConversationContext context, CancellationToken cancellationToken)
    {
        var record = context.Approvable.FirstOrDefault(r => r.IsAwaitingConfirmation())
            ?? (context.Approvable.Count == 1 ? context.Approvable[0] : null);

        if (record is null)
        {
            await _channel.SendAsync(from, "Nothing was published. Tell me what you'd like changed and I'll pass it to Copilot.", cancellationToken);
            return;
        }

        if (record.IsAwaitingConfirmation())
        {
            record.AwaitingConfirmationUntil = null;
            await _state.SaveRequestAsync(record, cancellationToken);
        }

        await _channel.SendAsync(
            from,
            $"Holding off on {record.Code} — nothing was published. Tell me what to change, or say \"approve\" when you're ready. Send /cancel to drop it entirely.",
            cancellationToken);
    }

    // ---------------------------------------------------------------- explicit commands
    public async Task HandleNewRequestAsync(string from, string text, CancellationToken cancellationToken)
    {
        var code = _tokens.NewRequestCode();
        var nonce = _tokens.NewNonce();
        var uploadLink = await MaybeCreateUploadLinkAsync(code, from, text, cancellationToken);
        var record = new RequestRecord
        {
            Code = code,
            CorrelationNonce = nonce,
            RequesterChatId = from,
            OriginalMessage = text,
            Status = RequestStatus.New
        };

        await _state.CreateRequestAsync(record, cancellationToken);
        try
        {
            await _gitHub.EnsureCopilotAssignableAsync(cancellationToken);
            var issue = await _gitHub.CreateIssueForCopilotAsync(BuildIssueTitle(code, nonce), BuildIssueBody(record, uploadLink), cancellationToken);
            record.IssueNumber = issue.Number;
            if (!issue.CopilotAssigned)
            {
                _logger.LogError("Created issue {IssueNumber} for request {Code}, but Copilot was not assigned", issue.Number, code);
                record.Status = RequestStatus.Failed;
                record.LastError = "Copilot was not assigned to the created issue.";
                record.UpdatedAt = DateTimeOffset.UtcNow;
                await _state.SaveRequestAsync(record, cancellationToken);
                await _channel.SendAsync(from, $"USMGA request {code} could not be started safely because Copilot was not assigned. Please contact the web admin.", cancellationToken);
                return;
            }

            record.Status = RequestStatus.AgentStarted;
            record.UpdatedAt = DateTimeOffset.UtcNow;
            await _state.SaveRequestAsync(record, cancellationToken);
            await _channel.SendAsync(from, $"USMGA request {code} received. Copilot is preparing a preview. We'll message you here when it's ready." + (uploadLink is null ? string.Empty : $" Upload files: {uploadLink}"), cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start Copilot request {Code}", code);
            record.Status = RequestStatus.Failed;
            record.LastError = ex.Message;
            record.UpdatedAt = DateTimeOffset.UtcNow;
            await _state.SaveRequestAsync(record, cancellationToken);
            await _channel.SendAsync(from, $"USMGA request {code} could not be started safely. Please contact the web admin.", cancellationToken);
        }
    }

    public async Task HandleApproveAsync(string from, string code, string approvalNonce, CancellationToken cancellationToken)
    {
        var record = await _state.GetByCodeAsync(code, cancellationToken);
        if (!ApprovalNonceValid(record, from, approvalNonce))
        {
            await _channel.SendAsync(from, "Approval rejected: invalid or expired code/nonce for this chat.", cancellationToken);
            return;
        }

        if (record!.PrNumber is null || string.IsNullOrWhiteSpace(record.ReviewedSha))
        {
            await _channel.SendAsync(from, $"Request {code} needs a fresh preview before it can be approved.", cancellationToken);
            return;
        }

        await ExecuteApprovalAsync(from, record, cancellationToken);
    }

    /// <summary>
    /// Publishes a request whose ownership has already been established.
    /// </summary>
    /// <remarks>
    /// These are the guards that actually make approval safe, and they are identical no
    /// matter how the approval arrived — typed command, button tap, or natural language:
    /// the merge must target the exact commit that was previewed, and its checks must be green.
    /// </remarks>
    private async Task ExecuteApprovalAsync(string from, RequestRecord record, CancellationToken cancellationToken)
    {
        var code = record.Code;
        if (record.PrNumber is null || string.IsNullOrWhiteSpace(record.ReviewedSha))
        {
            await _channel.SendAsync(from, $"Request {code} needs a fresh preview before it can be approved.", cancellationToken);
            return;
        }

        var pr = await _gitHub.GetPullRequestAsync(record.PrNumber.Value, cancellationToken);
        if (!StringComparer.OrdinalIgnoreCase.Equals(pr.HeadSha, record.ReviewedSha))
        {
            record.Status = RequestStatus.Stale;
            record.AwaitingConfirmationUntil = null;
            record.UpdatedAt = DateTimeOffset.UtcNow;
            await _state.SaveRequestAsync(record, cancellationToken);
            await _channel.SendAsync(from, $"Request {code} changed since your last preview and needs a fresh preview before publishing.", cancellationToken);
            return;
        }

        var checks = await _gitHub.GetChecksAsync(pr.HeadSha, cancellationToken);
        if (checks.State == CheckState.Pending)
        {
            await _channel.SendAsync(from, $"Request {code} checks are still running ({checks.Summary}). Tell me \"approve\" again in a minute.", cancellationToken);
            return;
        }

        if (!checks.Passed)
        {
            record.Status = RequestStatus.Stale;
            record.AwaitingConfirmationUntil = null;
            record.UpdatedAt = DateTimeOffset.UtcNow;
            await _state.SaveRequestAsync(record, cancellationToken);
            await _channel.SendAsync(from, $"Request {code} cannot be published: checks did not pass ({checks.Summary}). Tell me what to fix and I'll pass it to Copilot.", cancellationToken);
            return;
        }

        var merge = await _gitHub.MergePullRequestAsync(pr.Number, record.ReviewedSha!, cancellationToken);
        var merged = merge.Merged;
        var message = merge.Message;

        if (!merged)
        {
            // Two approvals can race (a double-tap produces two updates with different ids,
            // so the update_id claim does not deduplicate them). GitHub serializes the merge
            // and the loser gets an error, but the change *is* live — reporting "not merged"
            // and overwriting the winner's record with Failed would be wrong on both counts.
            var latest = await _gitHub.GetPullRequestAsync(pr.Number, cancellationToken);
            if (latest.Merged)
            {
                _logger.LogInformation("Merge of PR {PrNumber} for {Code} lost a race but the PR is merged; treating as success", pr.Number, code);
                merged = true;
                message = "merged";
            }
        }

        record.Status = merged ? RequestStatus.Merged : RequestStatus.Failed;
        record.LastError = merged ? null : message;
        record.AwaitingConfirmationUntil = null;
        if (merged)
        {
            // Spent: stops the same preview being approved twice.
            record.ApprovalNonce = null;
            record.ApprovalNonceExpiresAt = null;
        }

        record.UpdatedAt = DateTimeOffset.UtcNow;
        await _state.SaveRequestAsync(record, cancellationToken);

        if (merged)
        {
            await ClearPreviewButtonsAsync(from, record, cancellationToken);
        }

        await _channel.SendAsync(from, merged ? $"Request {code} approved and merged to production." : $"Request {code} was not merged: {message}", cancellationToken);
    }

    public async Task HandleChangesAsync(string from, string code, string changes, CancellationToken cancellationToken)
    {
        var record = await _state.GetByCodeAsync(code, cancellationToken);
        if (record is null || !StringComparer.OrdinalIgnoreCase.Equals(record.RequesterChatId, from))
        {
            await _channel.SendAsync(from, $"Request {code} was not found for this chat.", cancellationToken);
            return;
        }

        await ApplyChangesAsync(from, record, changes, revisionWasInferred: false, cancellationToken);
    }

    private async Task ApplyChangesAsync(string from, RequestRecord record, string changes, bool revisionWasInferred, CancellationToken cancellationToken)
    {
        var code = record.Code;
        if (record.PrNumber is null)
        {
            var pr = await _gitHub.FindPullRequestAsync(record, cancellationToken);
            record.PrNumber = pr?.Number;
        }

        if (record.PrNumber is null)
        {
            await _channel.SendAsync(from, $"Request {code} does not have a PR yet. Please wait for the preview message.", cancellationToken);
            return;
        }

        await _gitHub.PostCopilotPrCommentAsync(record.PrNumber.Value, changes, cancellationToken);
        record.Status = RequestStatus.ChangesRequested;
        record.ReviewedSha = null;
        record.ApprovalNonce = null;
        record.ApprovalNonceExpiresAt = null;
        record.AwaitingConfirmationUntil = null;
        record.UpdatedAt = DateTimeOffset.UtcNow;
        await _state.SaveRequestAsync(record, cancellationToken);
        await ClearPreviewButtonsAsync(from, record, cancellationToken);

        // When the target was inferred rather than named, always say which request it went
        // to and how to start a separate one instead. That makes a misroute obvious and
        // one message away from repair.
        var reply = revisionWasInferred
            ? $"Request {code}: sent to Copilot as a revision. We'll message you when a fresh preview is ready.\n\nIf you meant to start a separate change, send /new followed by your request."
            : $"Request {code}: changes sent to Copilot. We'll message you here when a fresh preview is ready.";

        await _channel.SendAsync(from, reply, cancellationToken);
    }

    /// <summary>Returns false when the preview has no originating request to notify (e.g. a human-authored PR).</summary>
    public async Task<bool> NotifyPreviewAsync(NotifyRequest request, CancellationToken cancellationToken)
    {
        var record = !string.IsNullOrWhiteSpace(request.Code)
            ? await _state.GetByCodeAsync(request.Code!, cancellationToken)
            : await _state.FindByIssueOrPrAsync(request.IssueNumber, request.PrNumber, cancellationToken);

        if (record is null && request.PrNumber is not null)
        {
            var issueNumber = await _gitHub.GetLinkedIssueNumberForPullRequestAsync(request.PrNumber.Value, cancellationToken);
            if (issueNumber is not null)
            {
                record = await _state.FindByIssueOrPrAsync(issueNumber, null, cancellationToken);
            }
        }

        if (record is null)
        {
            return false;
        }

        if (request.PrNumber is not null)
        {
            record.PrNumber = request.PrNumber;
        }

        record.PreviewUrl = request.PreviewUrl;
        record.DeployedSha = request.DeployedSha;
        record.ReviewedSha = request.DeployedSha;
        record.ApprovalNonce = _tokens.NewNonce(12);
        record.ApprovalNonceExpiresAt = DateTimeOffset.UtcNow.AddHours(24);
        record.AwaitingConfirmationUntil = null;
        record.Status = RequestStatus.PreviewDeployed;
        record.UpdatedAt = DateTimeOffset.UtcNow;

        var buttons = new[]
        {
            new MessageButton("✅ Approve & publish", CallbackAction.Format(CallbackActionKind.Approve, record.Code, record.ApprovalNonce)),
            new MessageButton("✏️ Request changes", CallbackAction.Format(CallbackActionKind.Changes, record.Code))
        };

        var messageId = await _channel.SendAsync(
            record.RequesterChatId,
            $"Preview for {record.Code}: {record.PreviewUrl}\n\nTap a button below, reply \"looks good\" to publish, or just tell me what to change.",
            buttons,
            cancellationToken);

        record.PreviewMessageId = messageId;
        await _state.SaveRequestAsync(record, cancellationToken);
        return true;
    }

    public bool ApprovalNonceValid(RequestRecord? record, string from, string approvalNonce)
    {
        return record is not null
            && StringComparer.OrdinalIgnoreCase.Equals(record.RequesterChatId, from)
            && !string.IsNullOrWhiteSpace(record.ApprovalNonce)
            && FixedTimeEquals(record.ApprovalNonce, approvalNonce)
            && record.ApprovalNonceExpiresAt is not null
            && record.ApprovalNonceExpiresAt > DateTimeOffset.UtcNow;
    }

    // ---------------------------------------------------------------- helpers

    private async Task ClearPreviewButtonsAsync(string chatId, RequestRecord record, CancellationToken cancellationToken)
    {
        if (record.PreviewMessageId is null)
        {
            return;
        }

        await _channel.ClearButtonsAsync(chatId, record.PreviewMessageId.Value, cancellationToken);
        record.PreviewMessageId = null;
    }

    private static string DescribeNothingToApprove(ConversationContext context)
    {
        if (context.ActiveRequests.Count == 0)
        {
            return "There's nothing waiting for approval. Send me a change and I'll get Copilot started.";
        }

        var pending = context.ActiveRequests[0];
        return $"Nothing is ready to publish yet — {pending.Code} is {DescribeStatus(pending)}. I'll message you when its preview is ready.";
    }

    private static string DescribeStatus(RequestRecord record) => record.Status switch
    {
        RequestStatus.New => "just received",
        RequestStatus.AgentStarted => "with Copilot",
        RequestStatus.PrOpened => "in review",
        RequestStatus.PreviewDeployed => "waiting for your approval",
        RequestStatus.ChangesRequested => "being revised by Copilot",
        RequestStatus.Approved => "approved",
        RequestStatus.Stale => "needs a fresh preview",
        RequestStatus.Failed => "stalled — contact the web admin",
        _ => record.Status
    };

    private static string Summarize(string message)
    {
        var text = (message ?? string.Empty).Trim().ReplaceLineEndings(" ");
        return text.Length <= 40 ? text : text[..39] + "…";
    }

    private static bool FixedTimeEquals(string expected, string? actual)
    {
        if (actual is null) return false;
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var actualBytes = Encoding.UTF8.GetBytes(actual);
        return expectedBytes.Length == actualBytes.Length && CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }

    private async Task<string?> MaybeCreateUploadLinkAsync(string code, string from, string text, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_telegramOptions.UploadBaseUrl) || !_classifier.SuggestsAttachment(text))
        {
            return null;
        }

        var token = await _state.CreateUploadTokenAsync(code, from, cancellationToken);
        return $"{_telegramOptions.UploadBaseUrl.TrimEnd('/')}/{Uri.EscapeDataString(token)}";
    }

    private static string BuildIssueTitle(string code, string nonce) => $"[USMGA-TG {code}] Website change request {nonce}";

    private static string BuildIssueBody(RequestRecord record, string? uploadLink) => $"""
Telegram-driven website change request.

Request code: {record.Code}
Correlation nonce: {record.CorrelationNonce}
Requester chat ID: {record.RequesterChatId}
Received UTC: {record.CreatedAt:O}

Request:
{record.OriginalMessage}

Upload link issued: {uploadLink ?? "none"}

Copilot: please implement this request in the repository and open a pull request.
""";
}

public sealed class NotifyRequest
{
    public string? Code { get; set; }
    public int? IssueNumber { get; set; }
    public int? PrNumber { get; set; }
    public string PreviewUrl { get; set; } = string.Empty;
    public string DeployedSha { get; set; } = string.Empty;
}
