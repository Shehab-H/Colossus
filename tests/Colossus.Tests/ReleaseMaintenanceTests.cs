using Colossus.Application.Abstractions.Catalog;
using Colossus.Application.Catalog;
using Colossus.Domain.Catalog;
using Colossus.Domain.Model;
using Colossus.Infrastructure.Baking;
using Xunit;

namespace Colossus.Tests;

public sealed class ReleaseMaintenanceTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("colossus-retention-");

    public void Dispose() => _root.Delete(recursive: true);

    [Fact]
    public async Task Reconcile_ProtectsCurrentPinnedActiveLatestAndAge_AndIsIdempotent()
    {
        DateTimeOffset now = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);
        Guid mapId = Guid.NewGuid();
        Guid activeRunId = Guid.NewGuid();
        var store = new FileBakeStore(
            Path.Combine(_root.FullName, "tiles"),
            Path.Combine(_root.FullName, "staging"));
        var repository = new Repository(
            [Run(activeRunId, mapId, BakeRunStatus.Running, attempt: 1)],
            [
                Release(mapId, Guid.NewGuid(), "v1", now.AddDays(-30)),
                Release(mapId, Guid.NewGuid(), "v2", now.AddDays(-20)) with { Pinned = true },
                Release(mapId, activeRunId, "v3", now.AddDays(-15)),
                Release(mapId, Guid.NewGuid(), "v4", now.AddDays(-2)),
                Release(mapId, Guid.NewGuid(), "v5", now.AddHours(-36)),
                Release(mapId, Guid.NewGuid(), "v6", now.AddDays(-1)),
                Release(mapId, Guid.NewGuid(), "v7", now.AddDays(-30)) with
                {
                    State = ReleaseState.Prepared,
                    PreparedAt = now.AddDays(-30),
                },
                Release(mapId, Guid.NewGuid(), "v8", now.AddDays(-40)) with
                {
                    State = ReleaseState.Deleted,
                    DeletedAt = now.AddDays(-20),
                },
            ]);

        foreach (string version in new[] { "v1", "v2", "v3", "v4", "v5", "v6", "v7" })
            await AddressableAsync(store, version, "artifact-" + version);
        store.PublishLatest("retention-map", "v6");

        string activeWorkspace = $"{activeRunId:N}-a1";
        string staleWorkspace = "orphan-stale";
        string freshWorkspace = "orphan-fresh";
        TouchWorkspace(store, activeWorkspace, now.AddDays(-5));
        TouchWorkspace(store, staleWorkspace, now.AddDays(-5));
        TouchWorkspace(store, freshWorkspace, now.AddMinutes(-5));

        var options = new ReleaseMaintenanceOptions { WorkspaceGracePeriod = TimeSpan.FromHours(24) };
        var service = new ReleaseMaintenanceService(repository, repository, store, store, options, new FixedClock(now));

        var report = await service.ReconcileMapAsync(
            mapId, "retention-map", keepLatest: 2, keepFor: TimeSpan.FromDays(7));

        Assert.Contains(report.Decisions, value => value.Identity == "v1" && value.Action == RetentionAction.Deleted);
        Assert.Contains(report.Decisions, value => value.Identity == "v2" && value.Reason == "pinned");
        Assert.Contains(report.Decisions, value => value.Identity == "v3" && value.Reason == "active-run");
        Assert.Contains(report.Decisions, value => value.Identity == "v4" && value.Reason == "age-window");
        Assert.Contains(report.Decisions, value => value.Identity == "v5" && value.Reason == "latest-count");
        Assert.Contains(report.Decisions, value => value.Identity == "v6" && value.Reason == "current");
        Assert.Contains(report.Decisions, value => value.Identity == "v7" && value.Action == RetentionAction.Deleted);
        Assert.Contains(report.Decisions, value => value.Identity == "v8" && value.Reason == "already-deleted");
        Assert.Contains(report.Decisions, value => value.Identity == activeWorkspace && value.Reason == "active-run");
        Assert.Contains(report.Decisions, value => value.Identity == staleWorkspace && value.Action == RetentionAction.Deleted);
        Assert.Contains(report.Decisions, value => value.Identity == freshWorkspace && value.Reason == "grace-period");
        Assert.False(store.VersionExists("retention-map", "v1"));
        Assert.False(store.VersionExists("retention-map", "v7"));
        Assert.True(store.VersionExists("retention-map", "v6"));
        Assert.True(store.TryReadLatestVersion("retention-map", out string current));
        Assert.Equal("v6", current);

        var repeated = await service.ReconcileMapAsync(
            mapId, "retention-map", keepLatest: 2, keepFor: TimeSpan.FromDays(7));
        Assert.Equal(0, repeated.Deleted);
        Assert.Contains(repeated.Decisions, value => value.Identity == "v1" && value.Reason == "already-deleted");
    }

    [Fact]
    public async Task Reconcile_CompletesAVerifiedPublicationInterruptedAfterPreparation()
    {
        DateTimeOffset now = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);
        Guid mapId = Guid.NewGuid();
        Guid runId = Guid.NewGuid();
        var store = new FileBakeStore(
            Path.Combine(_root.FullName, "repair-tiles"),
            Path.Combine(_root.FullName, "repair-staging"));
        await AddressableAsync(store, "v-repair", $"{runId:N}-a3");
        var failedRun = Run(runId, mapId, BakeRunStatus.Failed, attempt: 3) with
        {
            FailureCategory = "Publication",
            Error = "Pointer commit was interrupted.",
        };
        var prepared = Release(mapId, runId, "v-repair", now.AddMinutes(-10)) with
        {
            State = ReleaseState.Prepared,
            AttemptId = $"{runId:N}-a3",
        };
        var repository = new Repository([failedRun], [prepared]);
        var service = new ReleaseMaintenanceService(
            repository, repository, store, store,
            new ReleaseMaintenanceOptions(), new FixedClock(now));

        var report = await service.ReconcileMapAsync(mapId, "retention-map", 1, null);

        Assert.Contains(report.Decisions, value =>
            value.Identity == "v-repair" && value.Reason == "repaired-publication");
        Assert.True(store.TryReadCurrent("retention-map", out string current));
        Assert.Equal("v-repair", current);
        Assert.Equal(BakeRunStatus.Succeeded, (await repository.FindAsync(runId))!.Status);
        Assert.Equal(ReleaseState.Published,
            Assert.Single(await repository.ListAllForMapAsync(mapId)).State);
    }

    private static async Task AddressableAsync(FileBakeStore store, string version, string attempt)
    {
        await store.WriteManifestAsync(Manifest(version), attempt);
        store.MakeVersionAddressable("retention-map", version, attempt);
    }

    private static void TouchWorkspace(FileBakeStore store, string attemptId, DateTimeOffset written)
    {
        string file = store.StagingPath("retention-map", attemptId);
        File.WriteAllText(file, "fixture");
        string workspace = Directory.GetParent(Directory.GetParent(file)!.FullName)!.FullName;
        Directory.SetLastWriteTimeUtc(workspace, written.UtcDateTime);
    }

    private static CatalogRelease Release(
        Guid mapId, Guid runId, string version, DateTimeOffset published) => new(
        Guid.NewGuid(), mapId, runId, version, $"tiles/retention-map/{version}", published, 100,
        ManifestHash: new string('A', 64), SourceRows: 10, TileCount: 1,
        VerificationPassed: true, VerifiedAt: published, PreparedAt: published,
        AttemptId: $"{runId:N}-a1");

    private static BakeRun Run(Guid id, Guid mapId, BakeRunStatus status, int attempt) => new(
        id, mapId, 1, View(), status, new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero),
        Attempt: attempt);

    private static Manifest Manifest(string version) => new()
    {
        Version = version,
        View = View(),
        Reduction = ReductionKind.RawPassthrough,
        Regime = "large",
        Root = new Bbox(0, 0, 1, 1),
        MinZoom = 0,
        MaxZoom = 0,
        TilePointBudget = 100,
        TotalPoints = 1,
        SourceRows = 1,
        Tiles = [new TileMeta(0, 0, 0, 1, true)],
    };

    private static ViewConfig View() => new()
    {
        Id = "retention-map",
        Viewport = Viewport.Geo,
        Mark = Mark.Point,
        Source = new SourceSpec
        {
            Query = "SELECT x, y FROM fixture",
            Geometry = new GeometrySpec { Kind = GeometryKind.Xy, X = "x", Y = "y" },
        },
    };

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class Repository(
        IEnumerable<BakeRun> initialRuns,
        IEnumerable<CatalogRelease> initialReleases) : IBakeRunRepository, IReleaseRepository
    {
        private readonly List<BakeRun> _runs = [.. initialRuns];
        private readonly List<CatalogRelease> _releases = [.. initialReleases];

        public Task<BakeRun> CreateAsync(BakeRun run, CancellationToken ct = default)
        {
            _runs.Add(run);
            return Task.FromResult(run);
        }

        public Task<BakeRun?> FindAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult(_runs.FirstOrDefault(value => value.Id == id));

        public Task<IReadOnlyList<BakeRun>> ListForMapAsync(Guid mapId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<BakeRun>>([.. _runs.Where(value => value.MapId == mapId)]);

        public Task<IReadOnlyList<BakeRun>> ListIncompleteAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<BakeRun>>([.. _runs.Where(value => !value.IsTerminal)]);

        public Task<BakeRun> UpdateAsync(BakeRun run, CancellationToken ct = default)
        {
            int index = _runs.FindIndex(value => value.Id == run.Id);
            _runs[index] = run;
            return Task.FromResult(run);
        }

        public Task<BakeRun> RequestCancellationAsync(Guid id, DateTimeOffset requestedAt, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<BakeRun> SupersedeQueuedAsync(
            Guid id, long supersededByRevision, DateTimeOffset supersededAt, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<BakeRun> AttachBackgroundJobAsync(Guid id, string backgroundJobId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<CatalogRelease> CreateAsync(CatalogRelease release, CancellationToken ct = default)
        {
            _releases.Add(release);
            return Task.FromResult(release);
        }

        public Task<CatalogRelease> UpdateAsync(CatalogRelease release, CancellationToken ct = default)
        {
            int index = _releases.FindIndex(value => value.Id == release.Id);
            _releases[index] = release;
            return Task.FromResult(release);
        }

        public Task<CatalogRelease?> FindForRunAsync(Guid runId, CancellationToken ct = default) =>
            Task.FromResult(_releases.FirstOrDefault(value => value.BakeRunId == runId));

        Task<IReadOnlyList<CatalogRelease>> IReleaseRepository.ListForMapAsync(Guid mapId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<CatalogRelease>>([.. _releases.Where(value =>
                value.MapId == mapId && value.State == ReleaseState.Published)]);

        public Task<IReadOnlyList<CatalogRelease>> ListAllForMapAsync(Guid mapId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CatalogRelease>>([.. _releases.Where(value => value.MapId == mapId)]);
    }
}
