using Colossus.Application;
using Colossus.Application.Abstractions.Baking;
using Colossus.Application.Abstractions.Catalog;
using Colossus.Application.Abstractions.Reduction;
using Colossus.Application.Abstractions.Sources;
using Colossus.Application.Catalog;
using Colossus.Application.Sources;
using Colossus.Infrastructure.Baking;
using Colossus.Infrastructure.Catalog;
using Colossus.Infrastructure.ClickHouse;
using Colossus.Infrastructure.Configuration;
using Colossus.Infrastructure.Reduction;
using Colossus.Infrastructure.Sources;
using Colossus.Infrastructure.Trino;
using Colossus.Infrastructure.Tiles;
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
            .Bind(configuration.GetSection(ClickHouseOptions.Section));
        services.AddOptions<DatabaseOptions>()
            .Bind(configuration.GetSection(DatabaseOptions.Section));
        services.AddOptions<StorageOptions>()
            .Bind(configuration.GetSection(StorageOptions.Section))
            .PostConfigure(o => o.ResolvePaths());
        services.AddOptions<RenderingOptions>()
            .Bind(configuration.GetSection(RenderingOptions.Section));
        services.AddOptions<ParquetSourceOptions>()
            .Bind(configuration.GetSection(ParquetSourceOptions.Section))
            .PostConfigure(options => options.ResolvePaths());
        services.AddOptions<TrinoOptions>()
            .Bind(configuration.GetSection(TrinoOptions.Section))
            .PostConfigure(options => options.ResolvePaths());
        var bakeResources = new BakeResourceOptions();
        configuration.GetSection(BakeResourceOptions.Section).Bind(bakeResources);
        bakeResources.Normalize();
        services.AddSingleton(bakeResources);

        // Ports → adapters. Catalogs and stores are stateless or cheap, so singletons.
        services.AddHttpClient("Colossus.ClickHouse");
        services.AddHttpClient("Colossus.Parquet");
        services.AddHttpClient("Colossus.Trino", client => client.Timeout = TimeSpan.FromMinutes(30));
        services.AddSingleton(sp => new ClickHouseClient(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient("Colossus.ClickHouse"),
            sp.GetRequiredService<IOptions<ClickHouseOptions>>().Value));
        services.AddSingleton<ISourceAdapter, ClickHouseAdapter>();
        services.AddSingleton<ISourceAdapter, ParquetSourceAdapter>();
        services.AddSingleton<ISourceAdapter, TrinoSourceAdapter>();
        services.AddSingleton<ISourceAdapterCatalog, SourceAdapterCatalog>();
        services.AddSingleton<IReductionCatalog, ReductionCatalog>();
        services.AddSingleton<ITileReader, ArrowTileReader>();
        services.AddSingleton<IStagingReader, DuckDbStagingReader>();
        services.AddSingleton<IBakeStore>(sp => new FileBakeStore(
            sp.GetRequiredService<IOptions<StorageOptions>>()));
        string catalogConnection = configuration.GetConnectionString(DatabaseOptions.ConnectionStringName)
            ?? throw new InvalidOperationException(
                $"ConnectionStrings:{DatabaseOptions.ConnectionStringName} is required.");
        services.AddSingleton(sp => new MySqlDatabase(
            catalogConnection,
            sp.GetRequiredService<IOptions<DatabaseOptions>>().Value));
        services.AddSingleton<IMapBakeLock>(_ => new MySqlMapBakeLock(catalogConnection));
        services.AddSingleton<IBakeScheduleLock>(_ => new MySqlBakeScheduleLock(catalogConnection));
        services.AddSingleton<IConnectionRepository>(sp => sp.GetRequiredService<MySqlDatabase>());
        services.AddSingleton<IDatasetRepository>(sp => sp.GetRequiredService<MySqlDatabase>());
        services.AddSingleton<IMapRepository>(sp => sp.GetRequiredService<MySqlDatabase>());
        services.AddSingleton<IBakePolicyRepository>(sp => sp.GetRequiredService<MySqlDatabase>());
        services.AddSingleton<IBakeRunRepository>(sp => sp.GetRequiredService<MySqlDatabase>());
        services.AddSingleton<IReleaseRepository>(sp => sp.GetRequiredService<MySqlDatabase>());
        services.AddSingleton<IChannelDomainScanner, DuckDbChannelDomainScanner>();
        services.AddSingleton<IFactGrouper, DuckDbFactGrouper>();
        // Transport precompression of render tiles (tile-transfer initiative, Phase 1): writes a .br sibling
        // per tile that the serve layer answers with Content-Encoding. Additive; the plain tile stays.
        services.AddSingleton<ITileCompressor, BrotliTileCompressor>();
        services.AddSingleton<ITilePacker, ArrowTilePacker>();
        services.AddSingleton<IBakePipeline, BakePipeline>();

        // R4 fold routing: a plain options record read from appsettings.
        var foldRouting = new FoldRoutingOptions();
        configuration.GetSection(FoldRoutingOptions.Section).Bind(foldRouting);
        services.AddSingleton(foldRouting);

        // R4 server fold executor (DuckDB over the baked facts Parquet). Stateless; used by the server's
        // fold endpoint, inert in the bake host.
        services.AddSingleton<Colossus.Infrastructure.Fold.DuckDbFoldExecutor>();

        // Use cases.
        services.AddSingleton(_ => new BakePlanner(
            targetTileWorkingSetBytes: bakeResources.TileWorkingSetBytes));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ProductCatalogService>();
        services.AddSingleton<MapManagementService>();
        services.AddSingleton<BakeSchedulingService>();
        var bakeExecution = new BakeRunExecutionOptions();
        configuration.GetSection(BakeRunExecutionOptions.Section).Bind(bakeExecution);
        bakeExecution.Normalize();
        services.AddSingleton(bakeExecution);
        services.AddSingleton<BakeRunExecutor>();
        services.AddSingleton<SourcePreviewService>();
        services.AddSingleton<LegacyViewImporter>();
        services.AddSingleton<DatabaseBootstrapper>();
        services.AddSingleton<IViewCatalog, DatabaseViewCompatibility>();
        services.AddSingleton<BakeViewUseCase>();
        services.AddSingleton<VerifyFidelityUseCase>();

        return services;
    }
}
