using System.Text.Json;
using CoffeeNChill.Functions.Models;
using CoffeeNChill.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace CoffeeNChill.Functions.Functions;

/// <summary>
/// Queue CONSUMER. Fires automatically when a message lands on order-processing-queue.
/// Logs the order to the Orders table and walks it through the barista lifecycle:
/// Received -> Preparing -> Ready -> Collected.
/// </summary>
public class ProcessOrderQueueFunction
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IOrderRepository _orders;
    private readonly ILogger<ProcessOrderQueueFunction> _logger;

    public ProcessOrderQueueFunction(IOrderRepository orders, ILogger<ProcessOrderQueueFunction> logger)
    {
        _orders = orders;
        _logger = logger;
    }

    [Function("ProcessOrderQueue")]
    public async Task Run(
        [QueueTrigger("order-processing-queue", Connection = "AzureWebJobsStorage")] string message)
    {
        // Any exception thrown here makes the host retry. After maxDequeueCount (host.json = 3)
        // the message is moved to "order-processing-queue-poison" automatically.
        Order order;
        try
        {
            order = JsonSerializer.Deserialize<Order>(message, JsonOptions)
                    ?? throw new InvalidOperationException("Message deserialised to null.");
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Malformed order message (will be retried, then poisoned): {Message}", message);
            throw;
        }

        if (string.IsNullOrWhiteSpace(order.OrderId) || order.SelectedItemSKUs.Count == 0)
        {
            _logger.LogError("Order message is missing OrderId or items: {Message}", message);
            throw new InvalidOperationException("Order message is missing required fields.");
        }

        var date = OrderRepository.DateKey(order.OrderTimestamp);

        // Step 1: write the order with Status = Received.
        await _orders.CreateAsync(order);
        _logger.LogInformation("Order {OrderId} [{Date}] -> {Status}", order.OrderId, date, OrderStatus.Received);

        // Step 2: simulate the barista lifecycle. Delay is configurable for quick demos.
        var delaySeconds = int.TryParse(Environment.GetEnvironmentVariable("ORDER_STATUS_DELAY_SECONDS"), out var d) ? d : 5;

        foreach (var status in OrderStatus.Lifecycle.Skip(1))   // Preparing, Ready, Collected
        {
            await Task.Delay(TimeSpan.FromSeconds(delaySeconds));
            await _orders.UpdateStatusAsync(date, order.OrderId, status);
            _logger.LogInformation("Order {OrderId} [{Date}] -> {Status}", order.OrderId, date, status);
        }
    }
}
