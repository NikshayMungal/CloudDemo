using System.Globalization;
using Azure;
using CoffeeNChill.Functions.Helpers;
using CoffeeNChill.Functions.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace CoffeeNChill.Functions.Functions;

/// <summary>Read-only endpoints so clients (and Postman) can check order status in the Orders table.</summary>
public class OrderStatusFunctions
{
    private readonly IOrderRepository _orders;
    private readonly ILogger<OrderStatusFunctions> _logger;

    public OrderStatusFunctions(IOrderRepository orders, ILogger<OrderStatusFunctions> logger)
    {
        _orders = orders;
        _logger = logger;
    }

    private static bool IsValidDate(string? d) =>
        d is not null && DateTime.TryParseExact(d, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    // GET /api/orders/{orderDate}/{orderId}
    [Function("GetOrderStatus")]
    public async Task<IActionResult> Get(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "orders/{orderDate}/{orderId}")] HttpRequest req,
        string orderDate, string orderId)
    {
        if (!IsValidDate(orderDate))
            return ApiHelpers.Error(400, "orderDate must be in the format yyyy-MM-dd.");

        try
        {
            var order = await _orders.GetAsync(orderDate, orderId);
            return order is null
                ? ApiHelpers.Error(404, $"Order '{orderId}' was not found for {orderDate}.")
                : new OkObjectResult(order);
        }
        catch (RequestFailedException ex)
        {
            _logger.LogError(ex, "Orders table error");
            return ApiHelpers.Error(503, "Storage is currently unavailable.");
        }
    }

    // GET /api/orders?date=yyyy-MM-dd   (date optional)
    [Function("ListOrders")]
    public async Task<IActionResult> List(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "orders")] HttpRequest req)
    {
        string? date = req.Query["date"];
        if (date is not null && !IsValidDate(date))
            return ApiHelpers.Error(400, "date must be in the format yyyy-MM-dd.");

        try
        {
            return new OkObjectResult(await _orders.ListAsync(date));
        }
        catch (RequestFailedException ex)
        {
            _logger.LogError(ex, "Orders table error");
            return ApiHelpers.Error(503, "Storage is currently unavailable.");
        }
    }
}
