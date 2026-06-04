using Azure.Messaging.EventGrid;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Usmga.FunctionApp.Models;
using Usmga.FunctionApp.Services;

namespace Usmga.FunctionApp.Functions;

public sealed class SmsInbound
{
    private readonly MessageClassifier _classifier;
    private readonly IStateStore _state;
    private readonly ISmsClient _sms;
    private readonly RequestProcessor _processor;
    private readonly ILogger<SmsInbound> _logger;

    public SmsInbound(MessageClassifier classifier, IStateStore state, ISmsClient sms, RequestProcessor processor, ILogger<SmsInbound> logger)
    {
        _classifier = classifier;
        _state = state;
        _sms = sms;
        _processor = processor;
        _logger = logger;
    }

    [Function("SmsInbound")]
    public async Task Run([EventGridTrigger] EventGridEvent eventGridEvent, CancellationToken cancellationToken)
    {
        if (!StringComparer.OrdinalIgnoreCase.Equals(eventGridEvent.EventType, "Microsoft.Communication.SMSReceived"))
        {
            _logger.LogInformation("Ignoring Event Grid event type {EventType}", eventGridEvent.EventType);
            return;
        }

        var sms = eventGridEvent.Data.ToObjectFromJson<SmsReceivedPayload>();
        if (sms is null || string.IsNullOrWhiteSpace(sms.MessageId))
        {
            _logger.LogWarning("Ignoring malformed SMS event");
            return;
        }

        if (!await _state.TryClaimMessageAsync(sms.MessageId, cancellationToken))
        {
            _logger.LogInformation("Ignoring duplicate or in-flight SMS message {MessageId}", sms.MessageId);
            return;
        }

        try
        {
            if (!_classifier.IsAllowed(sms.From))
            {
                await _sms.SendAsync(sms.From, "This USMGA website SMS number only accepts requests from authorized board members.", cancellationToken);
                await _state.CompleteMessageAsync(sms.MessageId, cancellationToken);
                return;
            }

            var command = _classifier.Classify(sms.Message);
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

            await _state.CompleteMessageAsync(sms.MessageId, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SMS message {MessageId} failed; releasing idempotency claim for retry", sms.MessageId);
            await _state.ReleaseMessageAsync(sms.MessageId, cancellationToken);
            throw;
        }
    }
}
