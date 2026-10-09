using System.Text.Json;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using CoffeeNChill.Functions.Models;

namespace CoffeeNChill.Functions.Services;

public interface IOrderQueueService
{
    /// <summary>Serializes the order to JSON and pushes it to order-processing-queue. Returns the queue message id.</summary>
    Task<string> EnqueueAsync(OrderMessage order, CancellationToken ct);
    /// <summary>Writes a diagnostic envelope for an unprocessable message to the poison queue.</summary>
    Task SendToPoisonAsync(string originalMessage, string reason, CancellationToken ct);
    /// <summary>Pushes an arbitrary raw string (used only by the diagnostics endpoint to demonstrate poison handling).</summary>
    Task EnqueueRawAsync(string rawMessage, CancellationToken ct);
    Task<IReadOnlyList<PoisonEnvelope>> PeekPoisonAsync(int max, CancellationToken ct);
}

/// <summary>
/// Queue producer. All messages are Base64 encoded (the default expected by the Functions queue trigger,
/// and set explicitly in host.json), so producer and consumer always agree on the wire format.
/// </summary>
public sealed class OrderQueueService : IOrderQueueService
{
    private readonly QueueClient _queue;
    private readonly QueueClient _poison;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _ready;

    public OrderQueueService(AppSettings settings)
    {
        var options = new QueueClientOptions { MessageEncoding = QueueMessageEncoding.Base64 };
        _queue = new QueueClient(settings.StorageConnectionString, AppSettings.OrderQueueName, options);

        // Separate client for the poison queue: ignore (rather than throw on) messages that are not valid Base64.
        var poisonOptions = new QueueClientOptions { MessageEncoding = QueueMessageEncoding.Base64 };
        poisonOptions.MessageDecodingFailed += _ => Task.CompletedTask;
        _poison = new QueueClient(settings.StorageConnectionString, AppSettings.PoisonQueueName, poisonOptions);
    }

    private async Task EnsureQueuesAsync(CancellationToken ct)
    {
        if (_ready) return;
        await _gate.WaitAsync(ct);
        try
        {
            if (!_ready)
            {
                await _queue.CreateIfNotExistsAsync(cancellationToken: ct);
                await _poison.CreateIfNotExistsAsync(cancellationToken: ct);
                _ready = true;
            }
        }
        finally { _gate.Release(); }
    }

    public async Task<string> EnqueueAsync(OrderMessage order, CancellationToken ct)
    {
        await EnsureQueuesAsync(ct);
        var json = JsonSerializer.Serialize(order); // PascalCase, matches the brief
        var receipt = await _queue.SendMessageAsync(json);
        return receipt.Value.MessageId;
    }

    public async Task EnqueueRawAsync(string rawMessage, CancellationToken ct)
    {
        await EnsureQueuesAsync(ct);
        await _queue.SendMessageAsync(rawMessage);
    }

    public async Task SendToPoisonAsync(string originalMessage, string reason, CancellationToken ct)
    {
        await EnsureQueuesAsync(ct);
        var envelope = new PoisonEnvelope { FailureReason = reason, OriginalMessage = originalMessage };
        await _poison.SendMessageAsync(JsonSerializer.Serialize(envelope));
    }

    public async Task<IReadOnlyList<PoisonEnvelope>> PeekPoisonAsync(int max, CancellationToken ct)
    {
        await EnsureQueuesAsync(ct);
        var peeked = await _poison.PeekMessagesAsync(maxMessages: Math.Clamp(max, 1, 32));
        var result = new List<PoisonEnvelope>();

        foreach (var message in peeked.Value)
        {
            var text = message.MessageText;
            try
            {
                // Messages we wrote ourselves are envelopes.
                var env = JsonSerializer.Deserialize<PoisonEnvelope>(text);
                if (env is not null && !string.IsNullOrEmpty(env.FailureReason))
                {
                    result.Add(env);
                    continue;
                }
            }
            catch (JsonException) { /* fall through: raw copy made by the Functions host */ }

            // Messages moved automatically by the host after maxDequeueCount failures are raw copies of the original.
            result.Add(new PoisonEnvelope
            {
                FailureReason = $"Moved to poison queue by the Functions host after {message.DequeueCount} failed attempt(s).",
                FailedAt = message.InsertedOn ?? DateTimeOffset.UtcNow,
                OriginalMessage = text
            });
        }
        return result;
    }
}
