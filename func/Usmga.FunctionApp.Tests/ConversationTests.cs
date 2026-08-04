using Microsoft.Extensions.Logging.Abstractions;
using Usmga.FunctionApp.Models;
using Usmga.FunctionApp.Options;
using Usmga.FunctionApp.Services;

namespace Usmga.FunctionApp.Tests;

/// <summary>
/// Covers the conversational layer: working out what a bare message means and which
/// request it refers to, without the user quoting a code or an approval nonce.
/// </summary>
public sealed class ConversationTests
{
    private const string Chat = "111111111";

    // ------------------------------------------------------------------ intent: approvals

    [Theory]
    [InlineData("approve")]
    [InlineData("Approve")]
    [InlineData("I approve")]
    [InlineData("approve it")]
    [InlineData("lgtm")]
    [InlineData("LGTM!")]
    [InlineData("looks good")]
    [InlineData("Looks good!")]
    [InlineData("looks good to me")]
    [InlineData("that looks really good")]
    [InlineData("ship it")]
    [InlineData("publish it")]
    [InlineData("please publish it")]
    [InlineData("go live")]
    [InlineData("merge it")]
    [InlineData("looks good, I approve")]
    public void ClearApprovalPhrasesApproveImmediately(string message)
    {
        var intent = Classify(message, ContextWithPreview());

        Assert.Equal(IntentKind.Approve, intent.Kind);
        Assert.Equal(IntentConfidence.High, intent.Confidence);
    }

    [Theory]
    [InlineData("ok")]
    [InlineData("okay")]
    [InlineData("yes")]
    [InlineData("sure")]
    [InlineData("nice")]
    [InlineData("perfect")]
    [InlineData("love it")]
    [InlineData("thanks")]
    [InlineData("ok thanks")]
    [InlineData("thanks!")]
    public void VagueApprovalPhrasesRequireConfirmation(string message)
    {
        var intent = Classify(message, ContextWithPreview());

        Assert.Equal(IntentKind.Approve, intent.Kind);
        Assert.Equal(IntentConfidence.Medium, intent.Confidence);
    }

    /// <summary>
    /// The single most important classifier rule: a message that praises the preview *and*
    /// asks for a change must never publish it. Substring matching would get this wrong.
    /// </summary>
    [Theory]
    [InlineData("looks good but can you make the logo bigger")]
    [InlineData("looks good, one more thing: fix the footer")]
    [InlineData("nice, now make the header blue")]
    [InlineData("approve after you fix the typo in the header")]
    [InlineData("ok but the sponsor logos are still not animating")]
    public void ApprovalPhrasePlusAnInstructionIsARevision(string message)
    {
        var intent = Classify(message, ContextWithPreview());

        Assert.Equal(IntentKind.Changes, intent.Kind);
        Assert.Equal(message, intent.Text);
    }

    [Fact]
    public void FreeFormTextWhileAPreviewIsPendingIsARevision()
    {
        var intent = Classify("make the banner taller", ContextWithPreview());

        Assert.Equal(IntentKind.Changes, intent.Kind);
        Assert.Equal("make the banner taller", intent.Text);
    }

    [Fact]
    public void SlashNewEscapesTheRevisionReadingWhileAPreviewIsPending()
    {
        var intent = Classify("/new add a favicon", ContextWithPreview());

        Assert.Equal(IntentKind.NewRequest, intent.Kind);
        Assert.Equal("add a favicon", intent.Text);
    }

    [Fact]
    public void FreeFormTextWithNothingPendingIsANewRequest()
    {
        var intent = Classify("add a favicon", ConversationContext.Empty);

        Assert.Equal(IntentKind.NewRequest, intent.Kind);
        Assert.Equal("add a favicon", intent.Text);
    }

    /// <summary>
    /// A reaction with nothing to publish must not become a change request, or it would
    /// open a GitHub issue whose entire body is "looks good".
    /// </summary>
    [Theory]
    [InlineData("looks good", IntentKind.Approve)]
    [InlineData("lgtm", IntentKind.Approve)]
    [InlineData("thanks", IntentKind.Status)]
    [InlineData("ok", IntentKind.Status)]
    public void BareReactionsWithNothingPendingDoNotCreateRequests(string message, IntentKind expected)
    {
        var intent = Classify(message, ConversationContext.Empty);

        Assert.Equal(expected, intent.Kind);
    }

    // ------------------------------------------------------------------ intent: other

    [Fact]
    public void LegacyApproveSyntaxStillParses()
    {
        var intent = Classify("APPROVE ab12cd nonce_123-XYZ", ConversationContext.Empty);

        Assert.Equal(IntentKind.Approve, intent.Kind);
        Assert.Equal(IntentConfidence.High, intent.Confidence);
        Assert.Equal("AB12CD", intent.Code);
        Assert.Equal("nonce_123-XYZ", intent.ApprovalNonce);
    }

    [Fact]
    public void LegacyChangesSyntaxStillParses()
    {
        var intent = Classify("CHANGES zz9xyz: make the header blue", ConversationContext.Empty);

        Assert.Equal(IntentKind.Changes, intent.Kind);
        Assert.Equal("ZZ9XYZ", intent.Code);
        Assert.Equal("make the header blue", intent.Text);
    }

    [Fact]
    public void ApproveWithACodeButNoNonceResolvesTheNonceFromState()
    {
        var intent = Classify("approve QXWHUP", ContextWithPreview());

        Assert.Equal(IntentKind.Approve, intent.Kind);
        Assert.Equal("QXWHUP", intent.Code);
        Assert.Null(intent.ApprovalNonce);
    }

    /// <summary>
    /// "approve &lt;word&gt;" must not be mistaken for "approve &lt;code&gt;" — several
    /// English words fit the generated-code shape exactly.
    /// </summary>
    [Theory]
    [InlineData("approve this")]
    [InlineData("approve it")]
    [InlineData("approve please")]
    [InlineData("approve thanks")]
    public void ApprovePlusAWordIsAnApprovalNotACodeLookup(string message)
    {
        var intent = Classify(message, ContextWithPreview());

        Assert.Equal(IntentKind.Approve, intent.Kind);
        Assert.Null(intent.Code);
    }

    /// <summary>
    /// Punctuation is the only thing separating "publish?" (asking) from "publish"
    /// (instructing), and <c>Normalize</c> erases it, so questions must be demoted to the
    /// confirmation path instead of merging outright.
    /// </summary>
    [Theory]
    [InlineData("publish?")]
    [InlineData("go live?")]
    [InlineData("merge?")]
    [InlineData("looks good?")]
    public void ApprovalPhrasedAsAQuestionAsksForConfirmation(string message)
    {
        var intent = Classify(message, ContextWithPreview());

        Assert.Equal(IntentKind.Approve, intent.Kind);
        Assert.Equal(IntentConfidence.Medium, intent.Confidence);
    }

    [Theory]
    [InlineData("send it to me")]
    [InlineData("send it to me please")]
    public void AskingForSomethingIsNotAnApproval(string message)
    {
        Assert.Equal(IntentKind.Changes, Classify(message, ContextWithPreview()).Kind);
    }

    [Fact]
    public async Task QuestionDoesNotMergeWithoutConfirmation()
    {
        var state = new InMemoryStateStore();
        await state.CreateRequestAsync(PreviewRecord("QXWHUP"), CancellationToken.None);
        var github = new FakeGitHub();
        var channel = new FakeChannel();

        await Processor(github, channel, state).HandleMessageAsync(Chat, "go live?", null, CancellationToken.None);

        Assert.False(github.MergeCalled);
        Assert.Contains("Just to confirm", channel.Messages.Single().Message);
    }

    /// <summary>
    /// Two approvals can race (a double tap yields two update ids, so the idempotency claim
    /// does not deduplicate them). GitHub serialises the merge; the loser must not report
    /// failure and overwrite the winner's record, because the change really is live.
    /// </summary>
    [Fact]
    public async Task LosingAMergeRaceIsReportedAsSuccessWhenThePrIsAlreadyMerged()
    {
        var state = new InMemoryStateStore();
        await state.CreateRequestAsync(PreviewRecord("QXWHUP"), CancellationToken.None);
        var github = new FakeGitHub
        {
            MergeResponse = new MergeResult(false, "Pull Request is not mergeable"),
            PullRequestAfterMerge = new GitHubPullRequest(42, "reviewed-sha", "copilot/test", "copilot-swe-agent[bot]", "url", string.Empty, Merged: true)
        };
        var channel = new FakeChannel();

        await Processor(github, channel, state).HandleApproveAsync(Chat, "QXWHUP", "nonce", CancellationToken.None);

        Assert.Equal(RequestStatus.Merged, (await state.GetByCodeAsync("QXWHUP", CancellationToken.None))!.Status);
        Assert.Contains("merged to production", channel.Messages.Last().Message);
    }

    [Fact]
    public async Task AGenuineMergeFailureIsStillReportedAsAFailure()
    {
        var state = new InMemoryStateStore();
        await state.CreateRequestAsync(PreviewRecord("QXWHUP"), CancellationToken.None);
        var github = new FakeGitHub { MergeResponse = new MergeResult(false, "Base branch was modified") };
        var channel = new FakeChannel();

        await Processor(github, channel, state).HandleApproveAsync(Chat, "QXWHUP", "nonce", CancellationToken.None);

        Assert.Equal(RequestStatus.Failed, (await state.GetByCodeAsync("QXWHUP", CancellationToken.None))!.Status);
        Assert.Contains("was not merged", channel.Messages.Last().Message);
    }

    /// <summary>
    /// A PR closed directly on GitHub leaves the record claiming it still awaits approval.
    /// Inferred approval makes that reachable by an offhand "looks good", so it must be
    /// answered truthfully instead of attempting a merge that can only fail.
    /// </summary>
    [Fact]
    public async Task ApprovingAPullRequestClosedOnGitHubExplainsRatherThanFailingToMerge()
    {
        var state = new InMemoryStateStore();
        await state.CreateRequestAsync(PreviewRecord("QXWHUP"), CancellationToken.None);
        var github = new FakeGitHub
        {
            PullRequest = new GitHubPullRequest(42, "reviewed-sha", "copilot/test", "copilot-swe-agent[bot]", "url", string.Empty, Merged: false, Closed: true)
        };
        var channel = new FakeChannel();

        await Processor(github, channel, state).HandleMessageAsync(Chat, "looks good, I approve", null, CancellationToken.None);

        Assert.False(github.MergeCalled);
        Assert.Equal(RequestStatus.Cancelled, (await state.GetByCodeAsync("QXWHUP", CancellationToken.None))!.Status);
        Assert.Contains("closed on GitHub without being published", channel.Messages.Last().Message);
    }

    /// <summary>A PR merged outside the bot is live; reporting a merge failure would be a lie.</summary>
    [Fact]
    public async Task ApprovingAPullRequestAlreadyMergedOnGitHubReportsItAsPublished()
    {
        var state = new InMemoryStateStore();
        await state.CreateRequestAsync(PreviewRecord("QXWHUP"), CancellationToken.None);
        var github = new FakeGitHub
        {
            PullRequest = new GitHubPullRequest(42, "reviewed-sha", "copilot/test", "copilot-swe-agent[bot]", "url", string.Empty, Merged: true)
        };
        var channel = new FakeChannel();

        await Processor(github, channel, state).HandleMessageAsync(Chat, "looks good, I approve", null, CancellationToken.None);

        Assert.False(github.MergeCalled);
        var record = (await state.GetByCodeAsync("QXWHUP", CancellationToken.None))!;
        Assert.Equal(RequestStatus.Merged, record.Status);
        Assert.Null(record.ApprovalNonce);
        Assert.Contains("already published", channel.Messages.Last().Message);
    }

    /// <summary>A closed PR must not be silently treated as approvable by the status listing either.</summary>
    [Fact]
    public async Task AClosedPullRequestReleasesTheRequestSoALaterMessageStartsFresh()
    {
        var state = new InMemoryStateStore();
        await state.CreateRequestAsync(PreviewRecord("QXWHUP"), CancellationToken.None);
        var github = new FakeGitHub
        {
            PullRequest = new GitHubPullRequest(42, "reviewed-sha", "copilot/test", "copilot-swe-agent[bot]", "url", string.Empty, Merged: false, Closed: true)
        };
        var channel = new FakeChannel();
        var processor = Processor(github, channel, state);

        await processor.HandleMessageAsync(Chat, "looks good, I approve", null, CancellationToken.None);
        await processor.HandleMessageAsync(Chat, "add a favicon to the site", null, CancellationToken.None);

        Assert.Equal(1, github.CreateIssueCalls);
    }

    /// <summary>
    /// A half-typed legacy command must not be forwarded to Copilot verbatim, nor opened as
    /// an issue whose body is "APPROVE abc123".
    /// </summary>
    [Theory]
    [InlineData("APPROVE abc123 nonce and also fix the footer")]
    [InlineData("CHANGES abc123")]
    public void MalformedLegacyCommandsAskForHelpInsteadOfBeingTakenLiterally(string message)
    {
        Assert.Equal(IntentKind.Help, Classify(message, ContextWithPreview()).Kind);
        Assert.Equal(IntentKind.Help, Classify(message, ConversationContext.Empty).Kind);
    }

    [Fact]
    public async Task ApproveWithACodePublishesThatRequestOutOfSeveral()
    {
        var state = new InMemoryStateStore();
        await state.CreateRequestAsync(PreviewRecord("QXWHUP"), CancellationToken.None);
        await state.CreateRequestAsync(PreviewRecord("K7MTZP"), CancellationToken.None);
        var github = new FakeGitHub();
        var channel = new FakeChannel();

        await Processor(github, channel, state).HandleMessageAsync(Chat, "approve K7MTZP", null, CancellationToken.None);

        Assert.True(github.MergeCalled);
        Assert.Equal(RequestStatus.Merged, (await state.GetByCodeAsync("K7MTZP", CancellationToken.None))!.Status);
        Assert.Equal(RequestStatus.PreviewDeployed, (await state.GetByCodeAsync("QXWHUP", CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task ApproveWithACodeFromAnotherChatIsNotFound()
    {
        var state = new InMemoryStateStore();
        await state.CreateRequestAsync(PreviewRecord("QXWHUP"), CancellationToken.None);
        var github = new FakeGitHub();
        var channel = new FakeChannel();

        await Processor(github, channel, state).HandleMessageAsync("222222222", "approve QXWHUP", null, CancellationToken.None);

        Assert.False(github.MergeCalled);
        Assert.Contains("was not found for this chat", channel.Messages.Single().Message);
    }

    [Theory]
    [InlineData("/status", IntentKind.Status)]
    [InlineData("/help", IntentKind.Help)]
    [InlineData("/start", IntentKind.Help)]
    [InlineData("/cancel", IntentKind.Cancel)]
    [InlineData("status", IntentKind.Status)]
    [InlineData("cancel", IntentKind.Cancel)]
    [InlineData("never mind", IntentKind.Cancel)]
    public void CommandsAndControlPhrasesRoute(string message, IntentKind expected)
    {
        Assert.Equal(expected, Classify(message, ContextWithPreview()).Kind);
    }

    [Theory]
    [InlineData("no")]
    [InlineData("not yet")]
    [InlineData("hold on")]
    [InlineData("wait")]
    public void RejectionDeclinesRatherThanCancels(string message)
    {
        Assert.Equal(IntentKind.Decline, Classify(message, ContextWithPreview()).Kind);
        Assert.Equal(IntentKind.Decline, Classify(message, ContextAwaitingConfirmation()).Kind);
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("yep")]
    [InlineData("do it")]
    [InlineData("ok")]
    public void BareAffirmationConfirmsWhenAConfirmationIsPending(string message)
    {
        var intent = Classify(message, ContextAwaitingConfirmation());

        Assert.Equal(IntentKind.Approve, intent.Kind);
        Assert.Equal(IntentConfidence.High, intent.Confidence);
    }

    // ------------------------------------------------------------------ resolution

    [Fact]
    public async Task NaturalLanguageApprovalMergesTheOnlyPendingPreview()
    {
        var state = new InMemoryStateStore();
        await state.CreateRequestAsync(PreviewRecord("QXWHUP"), CancellationToken.None);
        var github = new FakeGitHub();
        var channel = new FakeChannel();

        await Processor(github, channel, state).HandleMessageAsync(Chat, "looks good, I approve", null, CancellationToken.None);

        Assert.True(github.MergeCalled);
        Assert.Equal("reviewed-sha", github.ExpectedSha);
        Assert.Equal(RequestStatus.Merged, (await state.GetByCodeAsync("QXWHUP", CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task NaturalLanguageApprovalWithTwoPreviewsAsksWhichWithoutMerging()
    {
        var state = new InMemoryStateStore();
        await state.CreateRequestAsync(PreviewRecord("QXWHUP"), CancellationToken.None);
        await state.CreateRequestAsync(PreviewRecord("K7MTZP"), CancellationToken.None);
        var github = new FakeGitHub();
        var channel = new FakeChannel();

        await Processor(github, channel, state).HandleMessageAsync(Chat, "looks good", null, CancellationToken.None);

        Assert.False(github.MergeCalled);
        Assert.Contains("more than one preview", channel.Messages.Single().Message);
        Assert.Equal(2, channel.ButtonMessages.Single().Buttons.Count);
    }

    [Fact]
    public async Task ReplyingToAPreviewPicksThatRequestOutOfSeveral()
    {
        var state = new InMemoryStateStore();
        var target = PreviewRecord("K7MTZP");
        target.PreviewMessageId = 777;
        await state.CreateRequestAsync(PreviewRecord("QXWHUP"), CancellationToken.None);
        await state.CreateRequestAsync(target, CancellationToken.None);
        var github = new FakeGitHub();
        var channel = new FakeChannel();

        await Processor(github, channel, state).HandleMessageAsync(Chat, "looks good", 777, CancellationToken.None);

        Assert.True(github.MergeCalled);
        Assert.Equal(RequestStatus.Merged, (await state.GetByCodeAsync("K7MTZP", CancellationToken.None))!.Status);
        Assert.Equal(RequestStatus.PreviewDeployed, (await state.GetByCodeAsync("QXWHUP", CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task NaturalLanguageApprovalWithNothingPendingDoesNotMergeOrCreateAnIssue()
    {
        var state = new InMemoryStateStore();
        var github = new FakeGitHub();
        var channel = new FakeChannel();

        await Processor(github, channel, state).HandleMessageAsync(Chat, "looks good", null, CancellationToken.None);

        Assert.False(github.MergeCalled);
        Assert.Equal(0, github.CreateIssueCalls);
        Assert.Contains("nothing waiting for approval", channel.Messages.Single().Message);
    }

    /// <summary>
    /// An expired nonce makes a request non-approvable, so a natural-language approval must
    /// not publish it — the same outcome the typed command has always had.
    /// </summary>
    [Fact]
    public async Task NaturalLanguageApprovalIsRejectedWhenTheNonceHasExpired()
    {
        var state = new InMemoryStateStore();
        var record = PreviewRecord("QXWHUP");
        record.ApprovalNonceExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await state.CreateRequestAsync(record, CancellationToken.None);
        var github = new FakeGitHub();
        var channel = new FakeChannel();

        await Processor(github, channel, state).HandleMessageAsync(Chat, "looks good", null, CancellationToken.None);

        Assert.False(github.MergeCalled);
        Assert.Equal(0, github.CreateIssueCalls);
        Assert.Contains("Nothing is ready to publish yet", channel.Messages.Single().Message);
    }

    [Fact]
    public async Task RevisionIsInferredAndTheTargetIsNamedBack()
    {
        var state = new InMemoryStateStore();
        await state.CreateRequestAsync(PreviewRecord("QXWHUP"), CancellationToken.None);
        var github = new FakeGitHub();
        var channel = new FakeChannel();

        await Processor(github, channel, state).HandleMessageAsync(Chat, "looks good but make the logo bigger", null, CancellationToken.None);

        Assert.False(github.MergeCalled);
        Assert.Equal("looks good but make the logo bigger", github.Comment);
        var reply = channel.Messages.Single().Message;
        Assert.Contains("QXWHUP", reply);
        Assert.Contains("/new", reply);

        var saved = (await state.GetByCodeAsync("QXWHUP", CancellationToken.None))!;
        Assert.Equal(RequestStatus.ChangesRequested, saved.Status);
        Assert.Null(saved.ApprovalNonce);
        Assert.Null(saved.ReviewedSha);
    }

    [Fact]
    public async Task AmbiguousRevisionStartsANewRequestButSaysSo()
    {
        var state = new InMemoryStateStore();
        await state.CreateRequestAsync(PreviewRecord("QXWHUP"), CancellationToken.None);
        await state.CreateRequestAsync(PreviewRecord("K7MTZP"), CancellationToken.None);
        var github = new FakeGitHub();
        var channel = new FakeChannel();

        await Processor(github, channel, state).HandleMessageAsync(Chat, "make the logo bigger", null, CancellationToken.None);

        Assert.Null(github.Comment);
        Assert.Equal(1, github.CreateIssueCalls);
        var warning = channel.Messages.First().Message;
        Assert.Contains("couldn't tell which request", warning);
        Assert.Contains("QXWHUP", warning);
        Assert.Contains("K7MTZP", warning);
    }

    // ------------------------------------------------------------------ confirmation
    [Fact]
    public async Task VaguePraiseConfirmsFirstThenPublishesOnYes()
    {
        var state = new InMemoryStateStore();
        await state.CreateRequestAsync(PreviewRecord("QXWHUP"), CancellationToken.None);
        var github = new FakeGitHub();
        var channel = new FakeChannel();
        var processor = Processor(github, channel, state);

        await processor.HandleMessageAsync(Chat, "nice", null, CancellationToken.None);

        Assert.False(github.MergeCalled);
        Assert.Contains("Just to confirm", channel.Messages.Single().Message);
        Assert.True((await state.GetByCodeAsync("QXWHUP", CancellationToken.None))!.IsAwaitingConfirmation());

        await processor.HandleMessageAsync(Chat, "yes", null, CancellationToken.None);

        Assert.True(github.MergeCalled);
        Assert.Equal(RequestStatus.Merged, (await state.GetByCodeAsync("QXWHUP", CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task DecliningAConfirmationPublishesNothingAndKeepsTheRequestAlive()
    {
        var state = new InMemoryStateStore();
        await state.CreateRequestAsync(PreviewRecord("QXWHUP"), CancellationToken.None);
        var github = new FakeGitHub();
        var channel = new FakeChannel();
        var processor = Processor(github, channel, state);

        await processor.HandleMessageAsync(Chat, "nice", null, CancellationToken.None);
        await processor.HandleMessageAsync(Chat, "no", null, CancellationToken.None);

        Assert.False(github.MergeCalled);
        var saved = (await state.GetByCodeAsync("QXWHUP", CancellationToken.None))!;
        Assert.Equal(RequestStatus.PreviewDeployed, saved.Status);
        Assert.True(saved.IsApprovable());
        Assert.False(saved.IsAwaitingConfirmation());
        Assert.Contains("Holding off", channel.Messages.Last().Message);
    }

    [Fact]
    public async Task CancelIsDestructiveWhereDeclineIsNot()
    {
        var state = new InMemoryStateStore();
        await state.CreateRequestAsync(PreviewRecord("QXWHUP"), CancellationToken.None);
        var channel = new FakeChannel();

        await Processor(new FakeGitHub(), channel, state).HandleMessageAsync(Chat, "cancel", null, CancellationToken.None);

        var saved = (await state.GetByCodeAsync("QXWHUP", CancellationToken.None))!;
        Assert.Equal(RequestStatus.Cancelled, saved.Status);
        Assert.Null(saved.ApprovalNonce);
    }

    // ------------------------------------------------------------------ buttons

    [Fact]
    public async Task PreviewNotificationCarriesApproveAndChangesButtonsAndRemembersItsMessageId()
    {
        var state = new InMemoryStateStore();
        await state.CreateRequestAsync(new RequestRecord { Code = "QXWHUP", RequesterChatId = Chat, Status = RequestStatus.AgentStarted, IssueNumber = 5 }, CancellationToken.None);
        var channel = new FakeChannel();

        var notified = await Processor(new FakeGitHub(), channel, state).NotifyPreviewAsync(
            new NotifyRequest { Code = "QXWHUP", PrNumber = 42, PreviewUrl = "https://preview", DeployedSha = "reviewed-sha" },
            CancellationToken.None);

        Assert.True(notified);
        var sent = channel.ButtonMessages.Single();
        Assert.Equal(2, sent.Buttons.Count);
        Assert.Equal(CallbackActionKind.Approve, CallbackAction.Parse(sent.Buttons[0].Data)!.Kind);
        Assert.Equal(CallbackActionKind.Changes, CallbackAction.Parse(sent.Buttons[1].Data)!.Kind);

        var saved = (await state.GetByCodeAsync("QXWHUP", CancellationToken.None))!;
        Assert.NotNull(saved.PreviewMessageId);
        Assert.True(saved.IsApprovable());
    }

    [Fact]
    public async Task ApproveButtonMergesAndClearsItsKeyboard()
    {
        var state = new InMemoryStateStore();
        var record = PreviewRecord("QXWHUP");
        record.PreviewMessageId = 900;
        await state.CreateRequestAsync(record, CancellationToken.None);
        var github = new FakeGitHub();
        var channel = new FakeChannel();

        await Processor(github, channel, state).HandleCallbackAsync(
            Chat, "cb-1", CallbackAction.Format(CallbackActionKind.Approve, "QXWHUP", "nonce"), 900, CancellationToken.None);

        Assert.True(github.MergeCalled);
        Assert.Equal(1, channel.Acknowledged);
        Assert.Contains((Chat, 900L), channel.Cleared);
    }

    [Fact]
    public async Task ApproveButtonWithATamperedNonceIsRejected()
    {
        var state = new InMemoryStateStore();
        await state.CreateRequestAsync(PreviewRecord("QXWHUP"), CancellationToken.None);
        var github = new FakeGitHub();
        var channel = new FakeChannel();

        await Processor(github, channel, state).HandleCallbackAsync(
            Chat, "cb-1", "a:ABC123:not-the-nonce", 900, CancellationToken.None);

        Assert.False(github.MergeCalled);
        Assert.Contains("Approval rejected", channel.Messages.Last().Message);
    }

    /// <summary>A stale keyboard must not merge the same preview twice.</summary>
    [Fact]
    public async Task TappingApproveTwiceMergesOnlyOnce()
    {
        var state = new InMemoryStateStore();
        await state.CreateRequestAsync(PreviewRecord("QXWHUP"), CancellationToken.None);
        var github = new FakeGitHub();
        var channel = new FakeChannel();
        var processor = Processor(github, channel, state);
        var data = CallbackAction.Format(CallbackActionKind.Approve, "QXWHUP", "nonce");

        await processor.HandleCallbackAsync(Chat, "cb-1", data, 900, CancellationToken.None);
        await processor.HandleCallbackAsync(Chat, "cb-2", data, 900, CancellationToken.None);

        Assert.Equal(1, github.MergeCalls);
        Assert.Contains("Approval rejected", channel.Messages.Last().Message);
    }

    [Fact]
    public async Task ApproveButtonFromAnotherChatIsRejected()
    {
        var state = new InMemoryStateStore();
        await state.CreateRequestAsync(PreviewRecord("QXWHUP"), CancellationToken.None);
        var github = new FakeGitHub();
        var channel = new FakeChannel();

        await Processor(github, channel, state).HandleCallbackAsync(
            "222222222", "cb-1", CallbackAction.Format(CallbackActionKind.Approve, "QXWHUP", "nonce"), 900, CancellationToken.None);

        Assert.False(github.MergeCalled);
    }

    [Fact]
    public void ApproveCallbackDataWithoutANonceIsUnparseable()
    {
        Assert.Null(CallbackAction.Parse("a:ABC123"));
        Assert.Null(CallbackAction.Parse("x:ABC123:nonce"));
        Assert.Null(CallbackAction.Parse(null));
        Assert.Null(CallbackAction.Parse(new string('a', CallbackAction.MaxDataLength + 1)));
    }

    [Fact]
    public void CallbackDataStaysInsideTelegramsLimit()
    {
        var data = CallbackAction.Format(CallbackActionKind.Approve, "QXWHUP", "0123456789abcdef");

        Assert.True(data.Length <= CallbackAction.MaxDataLength);
    }

    // ------------------------------------------------------------------ helpers

    private static IntentResult Classify(string message, ConversationContext context) =>
        new RuleBasedIntentClassifier().Classify(message, context);

    private static ConversationContext ContextWithPreview() =>
        new(new[] { PreviewRecord("QXWHUP") }, null);

    private static ConversationContext ContextAwaitingConfirmation()
    {
        var record = PreviewRecord("QXWHUP");
        record.AwaitingConfirmationUntil = DateTimeOffset.UtcNow.AddMinutes(5);
        return new ConversationContext(new[] { record }, null);
    }

    private static RequestRecord PreviewRecord(string code) => new()
    {
        Code = code,
        RequesterChatId = Chat,
        OriginalMessage = $"change {code}",
        Status = RequestStatus.PreviewDeployed,
        IssueNumber = 5,
        PrNumber = 42,
        PreviewUrl = "https://preview",
        ReviewedSha = "reviewed-sha",
        ApprovalNonce = "nonce",
        ApprovalNonceExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
    };

    private static RequestProcessor Processor(FakeGitHub github, FakeChannel channel, InMemoryStateStore state)
    {
        var options = Microsoft.Extensions.Options.Options.Create(new TelegramOptions { Allowlist = Chat });
        return new RequestProcessor(github, channel, state, new FakeTokens(), new MessageClassifier(options), new RuleBasedIntentClassifier(), options, NullLogger<RequestProcessor>.Instance);
    }

    private sealed class FakeTokens : ITokenGenerator
    {
        public string NewRequestCode() => "NEW777";
        public string NewNonce(int bytes = 16) => "nonce";
    }

    private sealed class FakeChannel : IMessageChannel
    {
        private long _nextMessageId = 5000;

        public List<(string To, string Message)> Messages { get; } = new();
        public List<(string To, string Message, IReadOnlyList<MessageButton> Buttons)> ButtonMessages { get; } = new();
        public List<(string ChatId, long MessageId)> Cleared { get; } = new();
        public int Acknowledged { get; private set; }

        public Task<long?> SendAsync(string chatId, string message, IReadOnlyList<MessageButton>? buttons, CancellationToken cancellationToken)
        {
            Messages.Add((chatId, message));
            if (buttons is { Count: > 0 })
            {
                ButtonMessages.Add((chatId, message, buttons));
            }

            return Task.FromResult<long?>(_nextMessageId++);
        }

        public Task ClearButtonsAsync(string chatId, long messageId, CancellationToken cancellationToken)
        {
            Cleared.Add((chatId, messageId));
            return Task.CompletedTask;
        }

        public Task AcknowledgeAsync(string callbackQueryId, string? text, CancellationToken cancellationToken)
        {
            Acknowledged++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeGitHub : IGitHubClient
    {
        private bool _mergeAttempted;

        public GitHubPullRequest PullRequest { get; set; } = new(42, "reviewed-sha", "copilot/test", "copilot-swe-agent[bot]", "url");
        public GitHubPullRequest? PullRequestAfterMerge { get; set; }
        public CheckStatus Checks { get; set; } = new(CheckState.Passed, "ok");
        public MergeResult MergeResponse { get; set; } = new(true, "merged");
        public int MergeCalls { get; private set; }
        public bool MergeCalled => MergeCalls > 0;
        public int MarkReadyCalls { get; private set; }
        public string? ExpectedSha { get; private set; }
        public string? Comment { get; private set; }
        public int CreateIssueCalls { get; private set; }

        public Task EnsureCopilotAssignableAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<GitHubIssue> CreateIssueForCopilotAsync(string title, string body, CancellationToken cancellationToken)
        {
            CreateIssueCalls++;
            return Task.FromResult(new GitHubIssue(123, "issue", true));
        }

        public Task<GitHubPullRequest?> FindPullRequestAsync(RequestRecord record, CancellationToken cancellationToken) => Task.FromResult<GitHubPullRequest?>(PullRequest);

        public Task<GitHubPullRequest> GetPullRequestAsync(int prNumber, CancellationToken cancellationToken) =>
            Task.FromResult(_mergeAttempted && PullRequestAfterMerge is not null ? PullRequestAfterMerge : PullRequest);

        public Task<int?> GetLinkedIssueNumberForPullRequestAsync(int prNumber, CancellationToken cancellationToken) => Task.FromResult<int?>(null);
        public Task<CheckStatus> GetChecksAsync(string sha, CancellationToken cancellationToken) => Task.FromResult(Checks);

        public Task<MergeResult> MergePullRequestAsync(int prNumber, string expectedSha, CancellationToken cancellationToken)
        {
            MergeCalls++;
            _mergeAttempted = true;
            ExpectedSha = expectedSha;
            return Task.FromResult(MergeResponse);
        }

        public Task MarkPullRequestReadyForReviewAsync(string nodeId, CancellationToken cancellationToken)
        {
            MarkReadyCalls++;
            return Task.CompletedTask;
        }

        public Task PostCopilotPrCommentAsync(int prNumber, string text, CancellationToken cancellationToken)
        {
            Comment = text;
            return Task.CompletedTask;
        }
    }
}
