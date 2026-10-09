using Azure.Data.Tables;
using CoffeeNChill.Functions.Models;
using CoffeeNChill.Functions.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// HostBuilder is compatible with this project's .NET 8 / Worker 1.x packages.
// Core Tools and the Functions container supply settings as environment variables.
var host = new HostBuilder()
    .ConfigureFunctionsWebApplication()
    .ConfigureServices((context, services) =>
    {
        var config = context.Configuration;

        // AzureWebJobsStorage drives Tables, Queues and (locally) Blobs. Locally it is "UseDevelopmentStorage=true" (Azurite).
        var storageConnection = config["AzureWebJobsStorage"]
            ?? throw new InvalidOperationException("AzureWebJobsStorage is not configured. Set it in local.settings.json or as an environment variable.");

        var settings = new AppSettings
        {
            StorageConnectionString = storageConnection,
            FileShareConnectionString = config["FileShareConnectionString"],
            StatusStepDelaySeconds = int.TryParse(config["ORDER_STATUS_STEP_DELAY_SECONDS"], out var d) && d >= 0 ? d : 3,
            SimulateTransitions = !string.Equals(config["ORDER_SIMULATE_TRANSITIONS"], "false", StringComparison.OrdinalIgnoreCase),
            EnableDiagnostics = !string.Equals(config["ENABLE_DIAGNOSTICS"], "false", StringComparison.OrdinalIgnoreCase)
        };

        services.AddSingleton(settings);
        services.AddSingleton(new TableServiceClient(storageConnection));

        // ----------------------------- PART 1 services -----------------------------
        services.AddSingleton<IMenuRepository, MenuRepository>();

        // Azurite does not emulate Azure Files, so locally the staff-docs "share" is backed by a Blob container.
        // Supply FileShareConnectionString (a real storage account) to use a genuine Azure File Share.
        services.AddSingleton<IDocumentStore>(_ => settings.UseAzureFiles
            ? new FileShareDocumentStore(settings.FileShareConnectionString!)
            : new BlobDocumentStore(settings.StorageConnectionString));

        // ----------------------------- PART 2 services -----------------------------
        services.AddSingleton<IOrderRepository, OrderRepository>();
        services.AddSingleton<IOrderQueueService, OrderQueueService>();
    })
    .Build();

host.Run();
