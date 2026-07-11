using Colossus.Application;
using Colossus.Domain.Baking;
using Colossus.Domain.Reduction;
using Colossus.Domain.Sources;
using Colossus.Infrastructure.Baking;
using Colossus.Infrastructure.ClickHouse;
using Colossus.Infrastructure.Configuration;
using Colossus.Infrastructure.Reduction;
using Colossus.Infrastructure.Sources;
using Colossus.Infrastructure.Tiles;
using Colossus.Infrastructure.Views;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Colossus.Infrastructure.DependencyInjection;

/// <summary>The one composition root for Colossus services — shared by the Server and the Bake host so
/// the object graph is wired in exactly one place.</summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddColossus(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ClickHouseOptions>()
            .Bind(configuration.GetSection(ClickHouseOptions.Section))
            .PostConfigure(ApplyClickHouseEnvOverrides);

        // Ports → adapters. Catalogs and stores are stateless or cheap, so singletons.
        services.AddSingleton(sp => new ClickHouseClient(sp.GetRequiredService<IOptions<ClickHouseOptions>>().Value));
        services.AddSingleton<ISourceAdapterCatalog, SourceAdapterCatalog>();
        services.AddSingleton<IReductionCatalog, ReductionCatalog>();
        services.AddSingleton<ITileReader, ArrowTileReader>();
        services.AddSingleton<IStagingReader, DuckDbStagingReader>();
        services.AddSingleton<IBakeStore>(_ => new FileBakeStore());
        services.AddSingleton<IViewCatalog>(_ => new ViewRegistry());
        services.AddSingleton<IChannelDomainScanner, DuckDbChannelDomainScanner>();
        services.AddSingleton<IFactGrouper, DuckDbFactGrouper>();

        // Use cases.
        services.AddSingleton<BakePlanner>();
        services.AddSingleton<BakeViewUseCase>();
        services.AddSingleton<VerifyFidelityUseCase>();

        return services;
    }

    // The legacy COLOSSUS_CH_* env vars win over config, preserving the docker-compose dev workflow.
    private static void ApplyClickHouseEnvOverrides(ClickHouseOptions o)
    {
        o.BaseUrl = Environment.GetEnvironmentVariable("COLOSSUS_CH_URL") ?? o.BaseUrl;
        o.User = Environment.GetEnvironmentVariable("COLOSSUS_CH_USER") ?? o.User;
        o.Password = Environment.GetEnvironmentVariable("COLOSSUS_CH_PASSWORD") ?? o.Password;
    }
}
