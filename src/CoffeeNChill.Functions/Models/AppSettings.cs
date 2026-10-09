namespace CoffeeNChill.Functions.Models;

/// <summary>Strongly typed configuration read once at start-up (see Program.cs).</summary>
public class AppSettings
{
    /// <summary>Connection string for Tables, Queues and (fallback) Blobs. Same as AzureWebJobsStorage.</summary>
    public string StorageConnectionString { get; init; } = string.Empty;

    /// <summary>Optional real Azure Files connection string. Azurite does not emulate Azure Files.</summary>
    public string? FileShareConnectionString { get; init; }

    public bool UseAzureFiles => !string.IsNullOrWhiteSpace(FileShareConnectionString);

    /// <summary>Seconds between simulated barista status changes.</summary>
    public int StatusStepDelaySeconds { get; init; } = 3;

    /// <summary>When true the queue function walks Received -> Preparing -> Ready -> Collected automatically.</summary>
    public bool SimulateTransitions { get; init; } = true;

    /// <summary>Enables the /api/diagnostics endpoints (poison queue demo). Disable in production.</summary>
    public bool EnableDiagnostics { get; init; } = true;

    public const string ShareName = "staff-docs";
    public const string MenuTableName = "MenuItems";
    public const string OrdersTableName = "Orders";
    public const string OrderQueueName = "order-processing-queue";
    public const string PoisonQueueName = "order-processing-queue-poison";
}
