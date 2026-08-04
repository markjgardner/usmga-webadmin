using Microsoft.Extensions.Options;
using Usmga.FunctionApp.Options;
using Usmga.FunctionApp.Services;

namespace Usmga.FunctionApp.Tests;

public sealed class ClassifierTests
{
    private static MessageClassifier Classifier() => new(Microsoft.Extensions.Options.Options.Create(new TelegramOptions { Allowlist = "111111111,222222222" }));

    [Theory]
    [InlineData("111111111", true)]
    [InlineData("222222222", true)]
    [InlineData("333333333", false)]
    public void EnforcesTelegramUserIdAllowlist(string userId, bool expected)
    {
        Assert.Equal(expected, Classifier().IsAllowed(userId));
    }
}
