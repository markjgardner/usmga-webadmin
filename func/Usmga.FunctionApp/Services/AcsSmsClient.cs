using Azure.Communication.Sms;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Options;
using Usmga.FunctionApp.Options;

namespace Usmga.FunctionApp.Services;

public sealed class AcsSmsClient : ISmsClient
{
    private readonly SmsClient _client;
    private readonly SmsOptions _options;

    public AcsSmsClient(IOptions<SmsOptions> options)
    {
        _options = options.Value;
        _client = string.IsNullOrWhiteSpace(_options.ConnectionString)
            ? new SmsClient(new Uri(_options.Endpoint), (TokenCredential)new DefaultAzureCredential())
            : new SmsClient(_options.ConnectionString);
    }

    public async Task SendAsync(string to, string message, CancellationToken cancellationToken)
    {
        await _client.SendAsync(_options.FromNumber, to, message, cancellationToken: cancellationToken);
    }
}
