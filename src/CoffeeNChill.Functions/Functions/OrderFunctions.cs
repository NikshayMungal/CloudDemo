using CoffeeNChill.Functions.Helpers;
using CoffeeNChill.Functions.Models;
using CoffeeNChill.Functions.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace CoffeeNChill.Functions.Functions;

/// <summary>
/// PART 2 - HTTP endpoints for the asynchronous order workflow.
/// The producer never waits for the barista: it validates the order, drops it on
/// <c>order-processing-queue</c> and returns 202 Accepted straight away.
/// </summary>
public class OrderFunctions
{
    private readonly IOrderQueueService _queue;
    private readonly IOrderRepository _orders;
    private readonly IMenuRepository _menu;
    private readonly AppSettings _settings;
    private readonly ILogger<OrderFunctions> _logger;

    public OrderFunctions(
        IOrderQueueService queue,
        IOrderRepository orders,
        IMenuRepository menu,
        AppSettings settings,
        ILogger<OrderFunctions> logger)
    {
        _queue = queue;
        _orders = orders;
        _menu = menu;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>POST /api/orders/queue - validates an order and pushes it to order-processing-queue (202 Accepted).</summary>
    [Function("QueueOrder")]
    public Task<IActionResult> QueueOrder(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "orders/queue")] HttpRequest req,
        CancellationToken ct) =>
        ApiResults.GuardAsync(_logger, async () =>
        {
            var (body, error) = await RequestReader.ReadJsonAsync<QueueOrderRequest>(req, ct);
            if (error is not null) return ApiResults.BadRequest(error);

            var errors = ModelValidator.Validate(body!);
            if (errors.Count > 0) return ApiResults.ValidationFailed(errors);

            var message = body!.ToMessage();

            // Business validation: every SKU must exist on the menu and be available.
            var unknown = new List<string>();
            var unavailable = new List<string>();
            foreach (var sku in message.SelectedItemSKUs.Distinct())
            {
                var item = await _menu.FindBySkuAsync(sku, ct);
                if (item is null) unknown.Add(sku);
                else if (!item.IsAvailable) unavailable.Add(sku);
            }

            if (unknown.Count > 0)
                return ApiResults.Error(400, "Unknown menu items", "Some SKUs do not exist on the menu.",
                    new Dictionary<string, string[]> { ["selectedItemSKUs"] = unknown.Select(s => $"'{s}' is not on the menu.").ToArray() });

            if (unavailable.Count > 0)
                return ApiResults.Error(409, "Items unavailable", "Some items are currently unavailable.",
                    new Dictionary<string, string[]> { ["selectedItemSKUs"] = unavailable.Select(s => $"'{s}' is currently unavailable.").ToArray() });

            var messageId = await _queue.EnqueueAsync(message, ct);
            _logger.LogInformation("Order {OrderId} placed on {Queue} (message {MessageId})",
                message.OrderId, AppSettings.OrderQueueName, messageId);

            return new ObjectResult(new QueuedOrderResponse
            {
                OrderId = message.OrderId,
                Status = "Queued",
                Queue = AppSettings.OrderQueueName,
                MessageId = messageId,
                OrderTimestamp = message.OrderTimestamp,
                StatusUrl = $"/api/orders/{Uri.EscapeDataString(message.OrderId)}"
            })
            { StatusCode = StatusCodes.Status202Accepted };
        });

    /// <summary>GET /api/orders?status=Ready - lists orders from the Orders table (optionally filtered by status).</summary>
    [Function("GetOrders")]
    public Task<IActionResult> GetOrders(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "orders")] HttpRequest req,
        CancellationToken ct) =>
        ApiResults.GuardAsync(_logger, async () =>
        {
            string? status = null;
            var raw = req.Query["status"].ToString();
            if (!string.IsNullOrWhiteSpace(raw))
            {
                status = OrderStatus.Normalize(raw);
                if (status is null)
                    return ApiResults.BadRequest($"Invalid status '{raw}'. Valid values: {string.Join(", ", OrderStatus.Lifecycle)}.");
            }

            var orders = await _orders.GetAllAsync(status, ct);
            return new OkObjectResult(orders.Select(OrderResponse.From).ToList());
        });

    /// <summary>GET /api/orders/{orderId} - returns one order including its status history.</summary>
    [Function("GetOrderById")]
    public Task<IActionResult> GetOrderById(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "orders/{orderId}")] HttpRequest req,
        string orderId,
        CancellationToken ct) =>
        ApiResults.GuardAsync(_logger, async () =>
        {
            var order = await _orders.FindAsync(orderId.Trim().ToUpperInvariant(), ct);
            return order is null
                ? ApiResults.NotFound($"Order '{orderId}' was not found. It may still be waiting in the queue.")
                : new OkObjectResult(OrderResponse.From(order));
        });

    /// <summary>PUT /api/orders/{orderId}/status - staff move an order forward (never backwards).</summary>
    [Function("UpdateOrderStatus")]
    public Task<IActionResult> UpdateOrderStatus(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "orders/{orderId}/status")] HttpRequest req,
        string orderId,
        CancellationToken ct) =>
        ApiResults.GuardAsync(_logger, async () =>
        {
            var (body, error) = await RequestReader.ReadJsonAsync<UpdateOrderStatusRequest>(req, ct);
            if (error is not null) return ApiResults.BadRequest(error);

            var errors = ModelValidator.Validate(body!);
            if (errors.Count > 0) return ApiResults.ValidationFailed(errors);

            var status = OrderStatus.Normalize(body!.Status);
            if (status is null)
                return ApiResults.BadRequest($"Invalid status '{body.Status}'. Valid values: {string.Join(", ", OrderStatus.Lifecycle)}.");

            var result = await _orders.TryUpdateStatusAsync(orderId.Trim().ToUpperInvariant(), status, ct);
            return result.Outcome switch
            {
                StatusUpdateOutcome.NotFound => ApiResults.NotFound($"Order '{orderId}' was not found."),
                StatusUpdateOutcome.InvalidTransition => ApiResults.Conflict(
                    $"Order is already '{result.Order!.Status}'. Status can only move forward: {string.Join(" -> ", OrderStatus.Lifecycle)}."),
                _ => new OkObjectResult(OrderResponse.From(result.Order!))
            };
        });

    // ------------------------------------------------------------------
    // Diagnostics (poison-queue demo). Disabled when ENABLE_DIAGNOSTICS=false.
    // ------------------------------------------------------------------

    /// <summary>POST /api/diagnostics/malformed-order - drops a deliberately broken message on the queue to prove poison handling.</summary>
    [Function("DiagnosticsEnqueueMalformedOrder")]
    public Task<IActionResult> EnqueueMalformedOrder(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "diagnostics/malformed-order")] HttpRequest req,
        CancellationToken ct) =>
        ApiResults.GuardAsync(_logger, async () =>
        {
            if (!_settings.EnableDiagnostics) return ApiResults.NotFound("Diagnostics endpoints are disabled.");

            await _queue.EnqueueRawAsync("{ this is not valid order json", ct);
            return new ObjectResult(new { message = "Malformed message queued. It should appear in the poison queue shortly.", poisonQueue = AppSettings.PoisonQueueName })
            { StatusCode = StatusCodes.Status202Accepted };
        });

    /// <summary>GET /api/diagnostics/poison-orders - peeks (does not remove) messages on the poison queue.</summary>
    [Function("DiagnosticsGetPoisonOrders")]
    public Task<IActionResult> GetPoisonOrders(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "diagnostics/poison-orders")] HttpRequest req,
        CancellationToken ct) =>
        ApiResults.GuardAsync(_logger, async () =>
        {
            if (!_settings.EnableDiagnostics) return ApiResults.NotFound("Diagnostics endpoints are disabled.");

            var messages = await _queue.PeekPoisonAsync(32, ct);
            return new OkObjectResult(new { queue = AppSettings.PoisonQueueName, count = messages.Count, messages });
        });
}
