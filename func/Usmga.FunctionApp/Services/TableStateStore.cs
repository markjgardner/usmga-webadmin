using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Options;
using Usmga.FunctionApp.Models;
using Usmga.FunctionApp.Options;

namespace Usmga.FunctionApp.Services;

public sealed class TableStateStore : IStateStore
{
    private const string RequestPartition = "request";
    private const string MessagePartition = "message";
    private const string UploadPartition = "upload";

    /// <summary>How far back <see cref="ListActiveForChatAsync"/> looks for in-flight work.</summary>
    private static readonly TimeSpan ActiveWindow = TimeSpan.FromDays(30);

    private readonly TableClient _table;
    private readonly ITokenGenerator _tokens;

    public TableStateStore(IOptions<StorageOptions> options, ITokenGenerator tokens)
    {
        _tokens = tokens;
        _table = new TableClient(options.Value.ConnectionString, options.Value.TableName);
        _table.CreateIfNotExists();
    }

    public async Task<bool> TryClaimMessageAsync(string messageId, CancellationToken cancellationToken)
    {
        var entity = new TableEntity(MessagePartition, messageId)
        {
            ["SeenAt"] = DateTimeOffset.UtcNow,
            ["Status"] = "Processing"
        };
        try
        {
            await _table.AddEntityAsync(entity, cancellationToken);
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status == 409)
        {
            return false;
        }
    }

    public async Task CompleteMessageAsync(string messageId, CancellationToken cancellationToken)
    {
        var entity = new TableEntity(MessagePartition, messageId)
        {
            ["SeenAt"] = DateTimeOffset.UtcNow,
            ["Status"] = "Processed"
        };
        await _table.UpsertEntityAsync(entity, TableUpdateMode.Replace, cancellationToken);
    }

    public async Task ReleaseMessageAsync(string messageId, CancellationToken cancellationToken)
    {
        try
        {
            await _table.DeleteEntityAsync(MessagePartition, messageId, ETag.All, cancellationToken);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
        }
    }

    public async Task CreateRequestAsync(RequestRecord record, CancellationToken cancellationToken)
    {
        await _table.AddEntityAsync(ToEntity(record), cancellationToken);
    }

    public async Task<RequestRecord?> GetByCodeAsync(string code, CancellationToken cancellationToken)
    {
        try
        {
            var entity = await _table.GetEntityAsync<TableEntity>(RequestPartition, code.ToUpperInvariant(), cancellationToken: cancellationToken);
            return FromEntity(entity.Value);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task<RequestRecord?> FindByIssueOrPrAsync(int? issueNumber, int? prNumber, CancellationToken cancellationToken)
    {
        if (issueNumber is null && prNumber is null) return null;
        var filters = new List<string>();
        if (issueNumber is not null) filters.Add($"IssueNumber eq {issueNumber.Value}");
        if (prNumber is not null) filters.Add($"PrNumber eq {prNumber.Value}");
        var filter = $"PartitionKey eq '{RequestPartition}' and ({string.Join(" or ", filters)})";
        await foreach (var entity in _table.QueryAsync<TableEntity>(filter, maxPerPage: 1, cancellationToken: cancellationToken))
        {
            return FromEntity(entity);
        }
        return null;
    }

    public async Task<IReadOnlyList<RequestRecord>> ListActiveForChatAsync(string chatId, CancellationToken cancellationToken)
    {
        // Bounded by a recency window so this can never degrade into a full-table scan as
        // history accumulates. Anything older than the window is not conversationally
        // "in flight" anyway, and the approval nonce would have expired long before.
        var since = DateTimeOffset.UtcNow - ActiveWindow;
        var filter = $"PartitionKey eq '{RequestPartition}' and RequesterChatId eq '{Sanitize(chatId)}' and UpdatedAt gt datetime'{since:yyyy-MM-ddTHH:mm:ssZ}'";

        var records = new List<RequestRecord>();
        await foreach (var entity in _table.QueryAsync<TableEntity>(filter, cancellationToken: cancellationToken))
        {
            var record = FromEntity(entity);
            if (!RequestStatus.IsTerminal(record.Status))
            {
                records.Add(record);
            }
        }

        return records.OrderByDescending(r => r.UpdatedAt).ToArray();
    }

    /// <summary>
    /// Escapes single quotes so a chat id cannot break out of the OData string literal.
    /// Chat ids are numeric in practice; this is defence in depth.
    /// </summary>
    private static string Sanitize(string value) => value.Replace("'", "''");

    public async Task SaveRequestAsync(RequestRecord record, CancellationToken cancellationToken)
    {
        record.UpdatedAt = DateTimeOffset.UtcNow;
        await _table.UpsertEntityAsync(ToEntity(record), TableUpdateMode.Replace, cancellationToken);
    }

    public async Task<string> CreateUploadTokenAsync(string code, string requesterChatId, CancellationToken cancellationToken)
    {
        var token = _tokens.NewNonce(24);
        var entity = new TableEntity(UploadPartition, token)
        {
            ["Code"] = code,
            ["RequesterChatId"] = requesterChatId,
            ["CreatedAt"] = DateTimeOffset.UtcNow,
            ["Used"] = false
        };
        await _table.AddEntityAsync(entity, cancellationToken);
        return token;
    }

    private static TableEntity ToEntity(RequestRecord record)
    {
        var entity = new TableEntity(RequestPartition, record.Code.ToUpperInvariant())
        {
            ["Code"] = record.Code.ToUpperInvariant(),
            ["CorrelationNonce"] = record.CorrelationNonce,
            ["RequesterChatId"] = record.RequesterChatId,
            ["OriginalMessage"] = record.OriginalMessage,
            ["Status"] = record.Status,
            ["CreatedAt"] = record.CreatedAt,
            ["UpdatedAt"] = record.UpdatedAt
        };
        SetNullable(entity, "IssueNumber", record.IssueNumber);
        SetNullable(entity, "PrNumber", record.PrNumber);
        SetNullable(entity, "PreviewUrl", record.PreviewUrl);
        SetNullable(entity, "ReviewedSha", record.ReviewedSha);
        SetNullable(entity, "DeployedSha", record.DeployedSha);
        SetNullable(entity, "ApprovalNonce", record.ApprovalNonce);
        SetNullable(entity, "ApprovalNonceExpiresAt", record.ApprovalNonceExpiresAt);
        SetNullable(entity, "PreviewMessageId", record.PreviewMessageId);
        SetNullable(entity, "AwaitingConfirmationUntil", record.AwaitingConfirmationUntil);
        SetNullable(entity, "LastError", record.LastError);
        return entity;
    }

    private static RequestRecord FromEntity(TableEntity entity) => new()
    {
        Code = entity.GetString("Code") ?? entity.RowKey,
        CorrelationNonce = entity.GetString("CorrelationNonce") ?? string.Empty,
        RequesterChatId = entity.GetString("RequesterChatId") ?? string.Empty,
        OriginalMessage = entity.GetString("OriginalMessage") ?? string.Empty,
        Status = entity.GetString("Status") ?? RequestStatus.New,
        IssueNumber = entity.TryGetValue("IssueNumber", out var issue) ? Convert.ToInt32(issue) : null,
        PrNumber = entity.TryGetValue("PrNumber", out var pr) ? Convert.ToInt32(pr) : null,
        PreviewUrl = entity.GetString("PreviewUrl"),
        ReviewedSha = entity.GetString("ReviewedSha"),
        DeployedSha = entity.GetString("DeployedSha"),
        ApprovalNonce = entity.GetString("ApprovalNonce"),
        ApprovalNonceExpiresAt = entity.TryGetValue("ApprovalNonceExpiresAt", out var expires) ? (DateTimeOffset?)expires : null,
        PreviewMessageId = entity.TryGetValue("PreviewMessageId", out var previewMessage) ? Convert.ToInt64(previewMessage) : null,
        AwaitingConfirmationUntil = entity.TryGetValue("AwaitingConfirmationUntil", out var awaiting) ? (DateTimeOffset?)awaiting : null,
        CreatedAt = entity.TryGetValue("CreatedAt", out var created) ? (DateTimeOffset)created : DateTimeOffset.UtcNow,
        UpdatedAt = entity.TryGetValue("UpdatedAt", out var updated) ? (DateTimeOffset)updated : DateTimeOffset.UtcNow,
        LastError = entity.GetString("LastError")
    };

    private static void SetNullable(TableEntity entity, string key, object? value)
    {
        if (value is not null) entity[key] = value;
    }
}
