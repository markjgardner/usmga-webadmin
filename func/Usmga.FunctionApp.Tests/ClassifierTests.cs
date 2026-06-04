using Microsoft.Extensions.Options;
using Usmga.FunctionApp.Models;
using Usmga.FunctionApp.Options;
using Usmga.FunctionApp.Services;

namespace Usmga.FunctionApp.Tests;

public sealed class ClassifierTests
{
    private static MessageClassifier Classifier() => new(Microsoft.Extensions.Options.Options.Create(new SmsOptions { Allowlist = "+15550000001,+15550000002" }));

    [Fact]
    public void ParsesApproveWithCodeAndNonce()
    {
        var command = Classifier().Classify(" APPROVE ab12cd nonce_123-XYZ ");
        Assert.Equal(InboundCommandKind.Approve, command.Kind);
        Assert.Equal("AB12CD", command.Code);
        Assert.Equal("nonce_123-XYZ", command.ApprovalNonce);
    }

    [Fact]
    public void ParsesChangesWithText()
    {
        var command = Classifier().Classify("CHANGES abc123: Please make the header blue");
        Assert.Equal(InboundCommandKind.Changes, command.Kind);
        Assert.Equal("ABC123", command.Code);
        Assert.Equal("Please make the header blue", command.Text);
    }

    [Fact]
    public void FallsBackToNewRequest()
    {
        var command = Classifier().Classify("Please update the homepage copy");
        Assert.Equal(InboundCommandKind.NewRequest, command.Kind);
        Assert.Equal("Please update the homepage copy", command.Text);
    }

    [Fact]
    public void RejectsMalformedApproveInsteadOfNewRequest()
    {
        var command = Classifier().Classify("APPROVE abc123");
        Assert.Equal(InboundCommandKind.Invalid, command.Kind);
    }

    [Theory]
    [InlineData("+15550000001", true)]
    [InlineData("+1 555 000 0001", true)]
    [InlineData("1 (555) 000-0001", true)]
    [InlineData("0015550000001", true)]
    [InlineData("+15559999999", false)]
    public void EnforcesAllowlistWithE164Normalization(string phone, bool expected)
    {
        Assert.Equal(expected, Classifier().IsAllowed(phone));
    }
}
