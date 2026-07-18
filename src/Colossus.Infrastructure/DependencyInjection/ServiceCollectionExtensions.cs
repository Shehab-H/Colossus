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
        // Transport precompression of render tiles (tile-transfer initiative, Phase 1): writes a .br sibling
        // per tile that the serve layer answers with Content-Encoding. Additive; the plain tile stays.
        services.AddSingleton<ITileCompressor, BrotliTileCompressor>();

        // R4 fold routing: a plain options record read from the FoldRouting config section (documented
        // default in docs/DEPLOY.md), plus env override for the force flag so a bake/CI can flip it.
        var foldRouting = new FoldRoutingOptions();
        configuration.GetSection(FoldRoutingOptions.Section).Bind(foldRouting);
        if (Environment.GetEnvironmentVariable("COLOSSUS_FOLD_FORCE_REMOTE") is { } f)
            foldRouting.ForceRemote = f is "1" or "true" or "TRUE";
        services.AddSingleton(foldRouting);

        // R4 server fold executor (DuckDB over the baked facts Parquet). Stateless; used by the server's
        // fold endpoint, inert in the bake host.
        services.AddSingleton<Colossus.Infrastructure.Fold.DuckDbFoldExecutor>();

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
