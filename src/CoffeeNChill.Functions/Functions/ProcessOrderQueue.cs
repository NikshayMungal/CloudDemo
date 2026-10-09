using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using CoffeeNChill.Functions.Helpers;
using CoffeeNChill.Functions.Models;
using CoffeeNChill.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace CoffeeNChill.Functions.Functions;

/// <summary>
/// PART 2 - Queue-triggered function. Runs automatically as soon as a message lands on
/// <c>order-processing-queue</c>.
///
/// Workflow:
///  1. Parse the JSON payload into an <see cref="OrderMessage"/>.
///  2. Write an entry to the Orders table (PartitionKey = order date, RowKey = order id, Status = "Received").
///  3. Simulate the barista lifecycle: Received -> Preparing -> Ready -> Collected.
///
/// Failure handling:
///  - Permanently bad messages (invalid JSON / failed validation) are copied to the poison queue with the
///    reason and then acknowledged, so they are not retried pointlessly.
///  - Transient failures (e.g. storage temporarily down) throw, so the host retries and, after
///    maxDequeueCount (5, see host.json) attempts, moves the message to order-processing-queue-poison itself.
/// </summary>
public class ProcessOrderQueue
{
    private readonly IOrderRepository _orders;
    private readonly IOrderQueueService _queue;
    private readonly AppSettings _settings;
    private readonly ILogger<ProcessOrderQueue> _logger;

    public ProcessOrderQueue(
        IOrderRepository orders,
        IOrderQueueService queue,
        AppSettings settings,
        ILogger<ProcessOrderQueue> logger)
    {
        _orders = orders;
        _queue = queue;
        _settings = settings;
        _logger = logger;
    }

    [Function("ProcessOrderQueue")]
    public async Task Run(
        [QueueTrigger(AppSettings.OrderQueueName, Connection = "AzureWebJobsStorage")] string message,
        FunctionContext context,
        CancellationToken ct)
    {
        context.BindingContext.BindingData.TryGetValue("DequeueCount", out var dequeueCount);
        _logger.LogInformation("ProcessOrderQueue triggered (dequeue count {DequeueCount})", dequeueCount ?? "?");

        // ---- 1. Parse JSON payload ----
        OrderMessage? order;
        try
        {
            order = JsonSerializer.Deserialize<OrderMessage>(message, RequestReader.JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Order message is not valid JSON - moving to poison queue");
            await _queue.SendToPoisonAsync(message, $"Invalid JSON: {ex.Message}", ct);
            return;
        }

        if (order is null)
        {
            await _queue.SendToPoisonAsync(message, "Message deserialized to null.", ct);
            return;
        }

        // ---- Validate the parsed order ----
        var errors = ModelValidator.Validate(order);
        if (order.OrderTimestamp == default)
            errors["orderTimestamp"] = new[] { "OrderTimestamp is required." };

        if (errors.Count > 0)
        {
            var reason = "Validation failed: " + string.Join("; ", errors.Select(e => $"{e.Key}: {string.Join(", ", e.Value)}"));
            _logger.LogError("Order message failed validation - moving to poison queue. {Reason}", reason);
            await _queue.SendToPoisonAsync(message, reason, ct);
            return;
        }

        // ---- 2. Log the order in the Orders table with status "Received" ----
        // (Storage exceptions are deliberately NOT caught: the host will retry, then poison automatically.)
        var (entity, created) = await _orders.AddIfNotExistsAsync(OrderEntity.From(order), ct);
        if (created)
            _logger.LogInformation("Order {OrderId} stored in Orders table with status Received", order.OrderId);
        else
            _logger.LogWarning("Order {OrderId} already exists (duplicate delivery) - resuming at status {Status}", order.OrderId, entity.Status);

        // ---- 3. Simulate barista status transitions ----
        if (!_settings.SimulateTransitions) return;

        var delay = TimeSpan.FromSeconds(_settings.StatusStepDelaySeconds);
        var startIndex = OrderStatus.IndexOf(entity.Status);

        for (var i = startIndex + 1; i < OrderStatus.Lifecycle.Length; i++)
        {
            await Task.Delay(delay, ct);
            var next = OrderStatus.Lifecycle[i];
            var result = await _orders.TryUpdateStatusAsync(order.OrderId, next, ct);

            _logger.LogInformation("Order {OrderId}: {Outcome} -> {Status}", order.OrderId, result.Outcome, next);
            // InvalidTransition simply means staff already moved the order further along - that is fine.
        }
    }
}
