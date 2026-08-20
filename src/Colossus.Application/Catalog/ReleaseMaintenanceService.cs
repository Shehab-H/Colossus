using Colossus.Application.Abstractions.Baking;
using Colossus.Application.Abstractions.Catalog;
using Colossus.Domain.Catalog;

namespace Colossus.Application.Catalog;

public sealed class ReleaseMaintenanceOptions
{
    public const string Section = "ReleaseMaintenance";
    public TimeSpan WorkspaceGracePeriod { get; set; } = TimeSpan.FromHours(24);

    public void Normalize()
    {
        if (WorkspaceGracePeriod < TimeSpan.FromMinutes(5))
            WorkspaceGracePeriod = TimeSpan.FromMinutes(5);
    }
}

public enum RetentionAction
{
    Retained,
    Deleted,
    Skipped,
}

public sealed record RetentionDecision(
    string Kind, string Identity, RetentionAction Action, string Reason);

public sealed record ReleaseMaintenanceReport(IReadOnlyList<RetentionDecision> Decisions)
{
    public int Deleted => Decisions.Count(value => value.Action == RetentionAction.Deleted);
}

/// <summary>Idempotent release retention and abandoned-workspace reconciliation. A candidate must fall
/// outside every configured retention window before it can be deleted.</summary>
public sealed class ReleaseMaintenanceService(
    IReleaseRepository releases,
    IBakeRunRepository runs,
    IBakeStore workspaces,
    IReleaseArtifactStore artifacts,
    ReleaseMaintenanceOptions options,
    TimeProvider clock)
{
    public async Task<ReleaseMaintenanceReport> ReconcileMapAsync(
        Guid mapId,
        string viewId,
        int keepLatest,
        TimeSpan? keepFor,
        CancellationToken ct = default)
    {
        keepLatest = Math.Max(1, keepLatest);
        DateTimeOffset now = clock.GetUtcNow();
        var decisions = new List<RetentionDecision>();
        var all = (await releases.ListAllForMapAsync(mapId, ct)).ToList();
        foreach (var prepared in all.Where(value => value.State == ReleaseState.Prepared).ToArray())
        {
            BakeRun? run = await runs.FindAsync(prepared.BakeRunId, ct);
            if (run is not { Status: BakeRunStatus.Failed, FailureCategory: "Publication" } ||
                !prepared.VerificationPassed || !artifacts.Exists(viewId, prepared.Version))
                continue;

            artifacts.PublishCurrent(viewId, prepared.Version);
            var repaired = await releases.UpdateAsync(prepared with
            {
                State = ReleaseState.Published,
                PublishedAt = now,
            }, ct);
            int index = all.FindIndex(value => value.Id == repaired.Id);
            all[index] = repaired;
            _ = await runs.UpdateAsync(run with
            {
                Status = BakeRunStatus.Succeeded,
                Stage = BakeRunStage.Succeeded,
                Progress = 1,
                StatusMessage = "Publication completed by reconciliation.",
                SourceRows = repaired.SourceRows,
                TileCount = repaired.TileCount,
                ArtifactBytes = repaired.ArtifactBytes,
                ReleaseId = repaired.Id,
                FailureCategory = null,
                FailureDetails = null,
                Error = null,
                CompletedAt = now,
                UpdatedAt = now,
            }, ct);
            decisions.Add(new("release", repaired.Version, RetentionAction.Retained, "repaired-publication"));
        }
        var active = (await runs.ListIncompleteAsync(ct))
            .Where(run => run.MapId == mapId)
            .Select(run => run.Id)
            .ToHashSet();
        string? current = artifacts.TryReadCurrent(viewId, out string currentVersion)
            ? currentVersion
            : null;
        var newest = all
            .Where(release => release.State == ReleaseState.Published)
            .OrderByDescending(release => release.PublishedAt)
            .Take(keepLatest)
            .Select(release => release.Id)
            .ToHashSet();

        foreach (var release in all.OrderByDescending(value => value.PublishedAt))
        {
            ct.ThrowIfCancellationRequested();
            string reason;
            if (release.State == ReleaseState.Deleted)
            {
                decisions.Add(new("release", release.Version, RetentionAction.Skipped, "already-deleted"));
                continue;
            }
            if (string.Equals(release.Version, current, StringComparison.Ordinal)) reason = "current";
            else if (release.Pinned) reason = "pinned";
            else if (active.Contains(release.BakeRunId)) reason = "active-run";
            else if (release.State == ReleaseState.Prepared &&
                     now - (release.PreparedAt ?? release.PublishedAt) <= options.WorkspaceGracePeriod)
                reason = "preparation-grace";
            else if (newest.Contains(release.Id)) reason = "latest-count";
            else if (keepFor is { } age && now - release.PublishedAt <= age) reason = "age-window";
            else
            {
                try
                {
                    artifacts.Delete(viewId, release.Version);
                    _ = await releases.UpdateAsync(release with
                    {
                        State = ReleaseState.Deleted,
                        DeletedAt = now,
                    }, ct);
                    decisions.Add(new("release", release.Version, RetentionAction.Deleted, "expired"));
                }
                catch (InvalidOperationException) when (
                    artifacts.TryReadCurrent(viewId, out string latest) &&
                    string.Equals(latest, release.Version, StringComparison.Ordinal))
                {
                    decisions.Add(new("release", release.Version, RetentionAction.Skipped, "current-race"));
                }
                continue;
            }

            decisions.Add(new("release", release.Version, RetentionAction.Retained, reason));
        }

        decisions.AddRange(await ScavengeWorkspacesAsync(ct));
        return new ReleaseMaintenanceReport(decisions);
    }

    public async Task<IReadOnlyList<RetentionDecision>> ScavengeWorkspacesAsync(
        CancellationToken ct = default)
    {
        DateTimeOffset cutoff = clock.GetUtcNow() - options.WorkspaceGracePeriod;
        var active = (await runs.ListIncompleteAsync(ct))
            .Select(run => $"{run.Id:N}-a{run.Attempt}")
            .ToHashSet(StringComparer.Ordinal);
        var decisions = new List<RetentionDecision>();
        foreach (var workspace in workspaces.ListWorkspaces())
        {
            ct.ThrowIfCancellationRequested();
            if (active.Contains(workspace.AttemptId))
            {
                decisions.Add(new("workspace", workspace.AttemptId, RetentionAction.Retained, "active-run"));
                continue;
            }
            if (workspace.LastWriteTime > cutoff)
            {
                decisions.Add(new("workspace", workspace.AttemptId, RetentionAction.Retained, "grace-period"));
                continue;
            }
            workspaces.CleanupWorkspace(workspace.AttemptId);
            decisions.Add(new("workspace", workspace.AttemptId, RetentionAction.Deleted, "abandoned"));
        }
        return decisions;
    }
}
