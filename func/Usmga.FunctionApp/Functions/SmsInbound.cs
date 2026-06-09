using System.Net;
using System.Web;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Twilio.Security;
using Usmga.FunctionApp.Models;
using Usmga.FunctionApp.Options;
using Usmga.FunctionApp.Services;

namespace Usmga.FunctionApp.Functions;

public sealed class SmsInbound
{
    private readonly MessageClassifier _classifier;
    private readonly IStateStore _state;
    private readonly ISmsClient _sms;
    private readonly RequestProcessor _processor;
    private readonly TwilioOptions _twilioOptions;
    private readonly ILogger<SmsInbound> _logger;

    public SmsInbound(MessageClassifier classifier, IStateStore state, ISmsClient sms, RequestProcessor processor, IOptions<TwilioOptions> twilioOptions, ILogger<SmsInbound> logger)
    {
        _classifier = classifier;
        _state = state;
        _sms = sms;
        _processor = processor;
        _twilioOptions = twilioOptions.Value;
        _logger = logger;
    }

    [Function("SmsInbound")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "sms/inbound")] HttpRequestData req,
        CancellationToken cancellationToken)
    {
        var body = await req.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(body))
        {
            _logger.LogWarning("Empty request body");
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }

        // Validate Twilio request signature
        var signature = req.Headers.TryGetValues("X-Twilio-Signature", out var sigValues)
            ? sigValues.FirstOrDefault() ?? string.Empty
            : string.Empty;

        var formParams = ParseFormBody(body);
        var validator = new RequestValidator(_twilioOptions.AuthToken);
        var requestUrl = req.Url.ToString();

        if (!validator.Validate(requestUrl, formParams, signature))
        {
            _logger.LogWarning("Twilio signature validation failed");
            return req.CreateResponse(HttpStatusCode.Forbidden);
        }

        var sms = new SmsReceivedPayload
        {
            MessageSid = formParams.GetValueOrDefault("MessageSid", string.Empty),
            From = formParams.GetValueOrDefault("From", string.Empty),
            To = formParams.GetValueOrDefault("To", string.Empty),
            Body = formParams.GetValueOrDefault("Body", string.Empty),
        };

        if (string.IsNullOrWhiteSpace(sms.MessageSid))
        {
            _logger.LogWarning("Ignoring malformed Twilio webhook (missing MessageSid)");
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }

        if (!await _state.TryClaimMessageAsync(sms.MessageSid, cancellationToken))
        {
            _logger.LogInformation("Ignoring duplicate or in-flight SMS message {MessageSid}", sms.MessageSid);
            return req.CreateResponse(HttpStatusCode.OK);
        }

        try
        {
            if (!_classifier.IsAllowed(sms.From))
            {
                await _sms.SendAsync(sms.From, "This USMGA website SMS number only accepts requests from authorized board members.", cancellationToken);
                await _state.CompleteMessageAsync(sms.MessageSid, cancellationToken);
                return req.CreateResponse(HttpStatusCode.OK);
            }

            var command = _classifier.Classify(sms.Body);
            switch (command.Kind)
            {
                case InboundCommandKind.Approve:
                    await _processor.HandleApproveAsync(sms.From, command.Code!, command.ApprovalNonce!, cancellationToken);
                    break;
                case InboundCommandKind.Changes:
                    await _processor.HandleChangesAsync(sms.From, command.Code!, command.Text, cancellationToken);
                    break;
                case InboundCommandKind.Invalid:
                    await _sms.SendAsync(sms.From, command.Text, cancellationToken);
                    break;
                default:
                    await _processor.HandleNewRequestAsync(sms.From, command.Text, cancellationToken);
                    break;
            }

            await _state.CompleteMessageAsync(sms.MessageSid, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SMS message {MessageSid} failed; releasing idempotency claim for retry", sms.MessageSid);
            await _state.ReleaseMessageAsync(sms.MessageSid, cancellationToken);
            throw;
        }

        return req.CreateResponse(HttpStatusCode.OK);
    }

    private static Dictionary<string, string> ParseFormBody(string body)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pairs = body.Split('&', StringSplitOptions.RemoveEmptyEntries);
        foreach (var pair in pairs)
        {
            var parts = pair.Split('=', 2);
            var key = HttpUtility.UrlDecode(parts[0]);
            var value = parts.Length > 1 ? HttpUtility.UrlDecode(parts[1]) : string.Empty;
            result[key] = value;
        }
        return result;
    }
}
