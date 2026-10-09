using System.Text.Json;
using Azure;
using Azure.Storage.Queues;
using CoffeeNChill.Functions.Helpers;
using CoffeeNChill.Functions.Models;
using CoffeeNChill.Functions.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace CoffeeNChill.Functions.Functions;

/// <summary>
/// Queue PRODUCER. Accepts an order, validates it and puts it on order-processing-queue
/// so the client is never blocked while the barista works.
/// </summary>
public class QueueOrderFunction
{
    public const string QueueName = "order-processing-queue";

    private readonly QueueServiceClient _queueService;
    private readonly ILogger<QueueOrderFunction> _logger;

    public QueueOrderFunction(QueueServiceClient queueService, ILogger<QueueOrderFunction> logger)
    {
        _queueService = queueService;
        _logger = logger;
    }

    // POST /api/orders/queue
    [Function("QueueOrder")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "orders/queue")] HttpRequest req)
    {
        var (body, error) = await ApiHelpers.ReadBodyAsync<OrderRequest>(req);
        if (error is not null) return error;

        // Extra validation DataAnnotations cannot express: every SKU must look like a SKU.
        var badSkus = body!.SelectedItemSKUs!.Where(s => !ApiHelpers.IsValidSku(s)).ToList();
        if (badSkus.Count > 0)
            return ApiHelpers.Error(400, "Validation failed.", new[] { $"Invalid SKU(s): {string.Join(", ", badSkus)}" });

        var order = new Order
        {
            OrderId = body.OrderId!.Trim(),
            CustomerName = body.CustomerName!.Trim(),
            SelectedItemSKUs = body.SelectedItemSKUs!.Select(s => s.Trim().ToUpperInvariant()).ToList(),
            TotalPrice = body.TotalPrice,
            // Timestamp generation: use the client's if supplied, otherwise the server clock (UTC).
            OrderTimestamp = (body.OrderTimestamp ?? DateTime.UtcNow).ToUniversalTime()
        };

        try
        {
            var queue = _queueService.GetQueueClient(QueueName);
            await queue.CreateIfNotExistsAsync();
            // Serialised to a JSON string; the client options Base64-encode it.
            await queue.SendMessageAsync(JsonSerializer.Serialize(order));
        }
        catch (RequestFailedException ex)
        {
            _logger.LogError(ex, "Could not enqueue order {OrderId}", order.OrderId);
            return ApiHelpers.Error(503, "The order queue is unavailable. Please try again shortly.");
        }

        _logger.LogInformation("Order {OrderId} queued for {Customer}", order.OrderId, order.CustomerName);
        return new ObjectResult(new
        {
            message = "Order accepted and queued.",
            orderId = order.OrderId,
            orderDate = OrderRepository.DateKey(order.OrderTimestamp),
            status = "Queued",
            orderTimestamp = order.OrderTimestamp
        })
        { StatusCode = 202 };
    }

    // POST /api/orders/queue/poison-test   (DEMO ONLY: remove before production / Part 3)
    // Puts a deliberately broken message on the queue so you can show the poison-queue handling.
    [Function("QueuePoisonTestMessage")]
    public async Task<IActionResult> PoisonTest(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "orders/queue/poison-test")] HttpRequest req)
    {
        try
        {
            var queue = _queueService.GetQueueClient(QueueName);
            await queue.CreateIfNotExistsAsync();
            await queue.SendMessageAsync("this is not valid order json");
            return new ObjectResult(new { message = "Broken message queued. After 3 failed attempts it moves to order-processing-queue-poison." })
            { StatusCode = 202 };
        }
        catch (RequestFailedException ex)
        {
            _logger.LogError(ex, "Could not enqueue poison test message");
            return ApiHelpers.Error(503, "The order queue is unavailable.");
        }
    }
}
