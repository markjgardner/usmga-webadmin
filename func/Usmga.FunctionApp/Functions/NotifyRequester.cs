using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Options;
using Usmga.FunctionApp.Options;
using Usmga.FunctionApp.Services;

namespace Usmga.FunctionApp.Functions;

public sealed class NotifyRequester
{
    private readonly RequestProcessor _processor;
    private readonly NotifyOptions _options;

    public NotifyRequester(RequestProcessor processor, IOptions<NotifyOptions> options)
    {
        _processor = processor;
        _options = options.Value;
    }

    [Function("NotifyRequester")]
    public async Task<HttpResponseData> Run([HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequestData request, CancellationToken cancellationToken)
    {
        if (!IsAuthorized(request))
        {
            var unauthorized = request.CreateResponse(HttpStatusCode.Unauthorized);
            await unauthorized.WriteStringAsync("Unauthorized", cancellationToken);
            return unauthorized;
        }

        var notify = await JsonSerializer.DeserializeAsync<NotifyRequest>(request.Body, new JsonSerializerOptions(JsonSerializerDefaults.Web), cancellationToken);
        if (notify is null || string.IsNullOrWhiteSpace(notify.PreviewUrl) || string.IsNullOrWhiteSpace(notify.DeployedSha))
        {
            var bad = request.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync("previewUrl and deployedSha are required", cancellationToken);
            return bad;
        }

        await _processor.NotifyPreviewAsync(notify, cancellationToken);
        var ok = request.CreateResponse(HttpStatusCode.OK);
        await ok.WriteStringAsync("notified", cancellationToken);
        return ok;
    }

    private bool IsAuthorized(HttpRequestData request)
    {
        if (string.IsNullOrWhiteSpace(_options.SharedSecret)) return false;
        if (!request.Headers.TryGetValues(_options.HeaderName, out var values)) return false;

        var expected = Encoding.UTF8.GetBytes(_options.SharedSecret);
        return values.Any(value => FixedTimeEquals(value, expected));
    }

    private static bool FixedTimeEquals(string? value, byte[] expected)
    {
        if (value is null) return false;
        var actual = Encoding.UTF8.GetBytes(value);
        return actual.Length == expected.Length && CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
