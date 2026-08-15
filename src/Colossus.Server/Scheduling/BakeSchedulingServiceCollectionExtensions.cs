using Colossus.Application.Abstractions.Baking;
using Hangfire;
using Hangfire.MySql;
using System.Transactions;

namespace Colossus.Server.Scheduling;

public static class BakeSchedulingServiceCollectionExtensions
{
    public static IServiceCollection AddBakeScheduling(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var options = new BakeSchedulingOptions();
        configuration.GetSection(BakeSchedulingOptions.Section).Bind(options);
        if (!options.Enabled)
        {
            services.AddSingleton<IBakeJobScheduler, DisabledBakeJobScheduler>();
            return services;
        }

        string connection = configuration.GetConnectionString("Database")
            ?? throw new InvalidOperationException("ConnectionStrings:Database is required when scheduling is enabled.");
        var storage = new MySqlStorage(connection, new MySqlStorageOptions
        {
            TransactionIsolationLevel = IsolationLevel.ReadCommitted,
            QueuePollInterval = TimeSpan.FromSeconds(5),
            JobExpirationCheckInterval = TimeSpan.FromHours(1),
            CountersAggregateInterval = TimeSpan.FromMinutes(5),
            PrepareSchemaIfNecessary = true,
            DashboardJobListLimit = 50_000,
            TransactionTimeout = TimeSpan.FromMinutes(1),
            TablesPrefix = "Hangfire"
        });
        services.AddHangfire(config => config
            .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
            .UseSimpleAssemblyNameTypeSerializer()
            .UseRecommendedSerializerSettings()
            .UseStorage(storage));
        services.AddHangfireServer(server =>
        {
            server.WorkerCount = Math.Max(1, options.WorkerCount);
            server.Queues = ["bakes"];
            server.CancellationCheckInterval = TimeSpan.FromMilliseconds(500);
            server.ShutdownTimeout = TimeSpan.FromSeconds(15);
        });
        services.AddSingleton<IBakeJobScheduler, HangfireBakeScheduler>();
        services.AddHostedService<BakeScheduleReconciler>();
        services.AddHostedService<BakeScheduleTicker>();
        return services;
    }
}
