using Microsoft.Extensions.Options;
using Twilio;
using Twilio.Rest.Api.V2010.Account;
using Twilio.Types;
using Usmga.FunctionApp.Options;

namespace Usmga.FunctionApp.Services;

public sealed class TwilioSmsClient : ISmsClient
{
    private readonly TwilioOptions _options;

    public TwilioSmsClient(IOptions<TwilioOptions> options)
    {
        _options = options.Value;
        TwilioClient.Init(_options.AccountSid, _options.AuthToken);
    }

    public async Task SendAsync(string to, string message, CancellationToken cancellationToken)
    {
        await MessageResource.CreateAsync(
            to: new PhoneNumber(to),
            from: new PhoneNumber(_options.FromNumber),
            body: message);
    }
}
