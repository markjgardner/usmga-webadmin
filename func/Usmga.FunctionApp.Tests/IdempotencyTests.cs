namespace Usmga.FunctionApp.Tests;

public sealed class IdempotencyTests
{
    [Fact]
    public async Task DedupReturnsFalseForRepeatedMessageId()
    {
        var store = new InMemoryStateStore();
        Assert.True(await store.TryClaimMessageAsync("msg-1", CancellationToken.None));
        await store.CompleteMessageAsync("msg-1", CancellationToken.None);
        Assert.False(await store.TryClaimMessageAsync("msg-1", CancellationToken.None));
    }
}
