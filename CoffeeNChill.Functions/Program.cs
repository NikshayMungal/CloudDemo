using Azure.Data.Tables;
using Azure.Storage.Queues;
using CoffeeNChill.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// Entry point: registers the storage clients and repositories used by the functions.
var host = new HostBuilder()
    .ConfigureFunctionsWebApplication()
    .ConfigureServices(services =>
    {
        // "UseDevelopmentStorage=true" points at Azurite when running locally.
        var conn = Environment.GetEnvironmentVariable("AzureWebJobsStorage") ?? "UseDevelopmentStorage=true";

        services.AddSingleton(new TableServiceClient(conn));

        // Base64 so messages written here can be decoded by the QueueTrigger.
        services.AddSingleton(new QueueServiceClient(conn,
            new QueueClientOptions { MessageEncoding = QueueMessageEncoding.Base64 }));

        services.AddSingleton<IMenuRepository, MenuRepository>();
        services.AddSingleton<IOrderRepository, OrderRepository>();
        services.AddSingleton<IDocumentStore>(_ => DocumentStoreFactory.Create(conn));
    })
    .Build();

host.Run();
