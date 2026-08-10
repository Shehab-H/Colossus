using Colossus.Application.Abstractions.Catalog;
using Colossus.Application.Catalog;
using Colossus.Domain.Catalog;
using Colossus.Infrastructure.Configuration;
using Colossus.Infrastructure.Serialization;
using MySqlConnector;

namespace Colossus.Infrastructure.Catalog;

/// <summary>MySQL implementation of the provider-independent catalog repositories. Records cross the
/// boundary as domain values; SQL rows and JSON persistence details remain private to Infrastructure.</summary>
public sealed class MySqlDatabase :
    IConnectionRepository,
    IDatasetRepository,
    IMapRepository,
    IBakePolicyRepository,
    IBakeRunRepository,
    IReleaseRepository
{
    private readonly string _connectionString;

    public MySqlDatabase(string connectionString, DatabaseOptions options)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("A MySQL catalog connection string is required.", nameof(connectionString));
        _connectionString = connectionString;
        CommandTimeoutSeconds = options.CommandTimeoutSeconds;
        EnsureCreated();
    }

    private int CommandTimeoutSeconds { get; }

    public async Task<CatalogConnection> CreateAsync(
        CatalogConnection connection,
        IReadOnlyDictionary<string, string> secrets,
        CancellationToken ct = default)
    {
        connection.Draft.Validate();
        const string sql = """
            INSERT INTO connections
                (id, workspace_key, slug, draft_json, secrets_json, revision, archived, created_at, updated_at)
            VALUES
                ($id, $workspace, $slug, $draft, $secrets, $revision, $archived, $created, $updated);
            """;
        try
        {
            await using var db = await OpenAsync(ct);
            await using var cmd = Command(db, sql,
                ("$id", connection.Id), ("$workspace", connection.WorkspaceKey),
                ("$slug", connection.Draft.Slug), ("$draft", Json(connection.Draft)),
                ("$secrets", Json(secrets)), ("$revision", connection.Revision),
                ("$archived", connection.Archived), ("$created", Timestamp(connection.CreatedAt)),
                ("$updated", Timestamp(connection.UpdatedAt)));
            await cmd.ExecuteNonQueryAsync(ct);
            return connection;
        }
        catch (MySqlException e) when (IsConstraint(e))
        {
            throw new CatalogConflictException(
                $"Connection slug '{connection.Draft.Slug}' already exists in workspace '{connection.WorkspaceKey}'.", e);
        }
    }

    async Task<CatalogConnection?> IConnectionRepository.FindAsync(Guid id, CancellationToken ct) =>
        await FindConnectionAsync("id = $value", id, ct);

    public async Task<CatalogConnection?> FindBySlugAsync(
        string workspaceKey, string slug, CancellationToken ct = default) =>
        await FindConnectionAsync("workspace_key = $workspace AND slug = $value", slug, ct, workspaceKey);

    async Task<IReadOnlyList<CatalogConnection>> IConnectionRepository.ListAsync(
        string workspaceKey, bool includeArchived, CancellationToken ct) =>
        await ListAsync("connections", ReadConnection, workspaceKey, includeArchived, ct);

    public async Task<IReadOnlyDictionary<string, string>> ReadSecretsAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        await using var cmd = Command(db, "SELECT secrets_json FROM connections WHERE id = $id;", ("$id", id));
        object? json = await cmd.ExecuteScalarAsync(ct);
        if (json is not string value) throw new CatalogNotFoundException($"Unknown connection '{id}'.");
        return FromJson<Dictionary<string, string>>(value);
    }

    public async Task<CatalogDataset> CreateAsync(CatalogDataset dataset, CancellationToken ct = default)
    {
        dataset.Draft.Validate();
        const string sql = """
            INSERT INTO datasets
                (id, workspace_key, connection_id, slug, draft_json, revision, archived, created_at, updated_at)
            VALUES
                ($id, $workspace, $connection, $slug, $draft, $revision, $archived, $created, $updated);
            """;
        try
        {
            await using var db = await OpenAsync(ct);
            await using var cmd = Command(db, sql,
                ("$id", dataset.Id), ("$workspace", dataset.WorkspaceKey),
                ("$connection", dataset.Draft.ConnectionId), ("$slug", dataset.Draft.Slug),
                ("$draft", Json(dataset.Draft)), ("$revision", dataset.Revision),
                ("$archived", dataset.Archived), ("$created", Timestamp(dataset.CreatedAt)),
                ("$updated", Timestamp(dataset.UpdatedAt)));
            await cmd.ExecuteNonQueryAsync(ct);
            return dataset;
        }
        catch (MySqlException e) when (IsConstraint(e))
        {
            throw new CatalogConflictException(
                $"Dataset slug '{dataset.Draft.Slug}' already exists or its connection is invalid.", e);
        }
    }

    async Task<CatalogDataset?> IDatasetRepository.FindAsync(Guid id, CancellationToken ct) =>
        await FindDatasetAsync("id = $value", id, ct);

    async Task<CatalogDataset?> IDatasetRepository.FindBySlugAsync(
        string workspaceKey, string slug, CancellationToken ct) =>
        await FindDatasetAsync("workspace_key = $workspace AND slug = $value", slug, ct, workspaceKey);

    async Task<IReadOnlyList<CatalogDataset>> IDatasetRepository.ListAsync(
        string workspaceKey, bool includeArchived, CancellationToken ct) =>
        await ListAsync("datasets", ReadDataset, workspaceKey, includeArchived, ct);

    public async Task<CatalogMap> CreateAsync(CatalogMap map, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO maps
                (id, workspace_key, dataset_id, slug, draft_json, revision, archived, created_at, updated_at)
            VALUES
                ($id, $workspace, $dataset, $slug, $draft, $revision, $archived, $created, $updated);
            """;
        try
        {
            await using var db = await OpenAsync(ct);
            await using var cmd = Command(db, sql,
                ("$id", map.Id), ("$workspace", map.WorkspaceKey), ("$dataset", map.Draft.DatasetId),
                ("$slug", map.Draft.Slug), ("$draft", Json(map.Draft)), ("$revision", map.Revision),
                ("$archived", map.Archived), ("$created", Timestamp(map.CreatedAt)),
                ("$updated", Timestamp(map.UpdatedAt)));
            await cmd.ExecuteNonQueryAsync(ct);
            return map;
        }
        catch (MySqlException e) when (IsConstraint(e))
        {
            throw new CatalogConflictException($"Map slug '{map.Draft.Slug}' already exists or its dataset is invalid.", e);
        }
    }

    async Task<CatalogMap?> IMapRepository.FindAsync(Guid id, CancellationToken ct) =>
        await FindMapAsync("id = $value", id, ct);

    async Task<CatalogMap?> IMapRepository.FindBySlugAsync(
        string workspaceKey, string slug, CancellationToken ct) =>
        await FindMapAsync("workspace_key = $workspace AND slug = $value", slug, ct, workspaceKey);

    async Task<IReadOnlyList<CatalogMap>> IMapRepository.ListAsync(
        string workspaceKey, bool includeArchived, CancellationToken ct) =>
        await ListAsync("maps", ReadMap, workspaceKey, includeArchived, ct);

    public async Task<CatalogMap> UpdateAsync(CatalogMap map, long expectedRevision, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE maps
            SET dataset_id = $dataset, slug = $slug, draft_json = $draft,
                revision = $revision, updated_at = $updated
            WHERE id = $id AND revision = $expected AND archived = 0;
            """;
        try
        {
            await using var db = await OpenAsync(ct);
            await using var cmd = Command(db, sql,
                ("$dataset", map.Draft.DatasetId), ("$slug", map.Draft.Slug), ("$draft", Json(map.Draft)),
                ("$revision", map.Revision), ("$updated", Timestamp(map.UpdatedAt)),
                ("$id", map.Id), ("$expected", expectedRevision));
            if (await cmd.ExecuteNonQueryAsync(ct) == 0) await ThrowMapWriteFailure(map.Id, expectedRevision, ct);
            return map;
        }
        catch (MySqlException e) when (IsConstraint(e))
        {
            throw new CatalogConflictException($"Map slug '{map.Draft.Slug}' already exists or its dataset is invalid.", e);
        }
    }

    public async Task<CatalogMap> ArchiveAsync(
        Guid id, long expectedRevision, DateTimeOffset updatedAt, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE maps
            SET archived = 1, revision = revision + 1, updated_at = $updated
            WHERE id = $id AND revision = $expected AND archived = 0;
            """;
        await using var db = await OpenAsync(ct);
        await using var cmd = Command(db, sql,
            ("$updated", Timestamp(updatedAt)), ("$id", id), ("$expected", expectedRevision));
        if (await cmd.ExecuteNonQueryAsync(ct) == 0) await ThrowMapWriteFailure(id, expectedRevision, ct);
        return await FindMapAsync("id = $value", id, ct)
            ?? throw new CatalogNotFoundException($"Unknown map '{id}'.");
    }

    public async Task<BakePolicy?> FindAsync(Guid mapId, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        await using var cmd = Command(db, "SELECT * FROM bake_policies WHERE map_id = $id;", ("$id", mapId));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadPolicy(reader) : null;
    }

    public async Task<BakePolicy> UpsertAsync(
        BakePolicy policy, long? expectedRevision, CancellationToken ct = default)
    {
        policy.Validate();
        await using var db = await OpenAsync(ct);
        if (expectedRevision is null)
        {
            const string insert = """
                INSERT INTO bake_policies
                    (map_id, enabled, interval_ticks, time_zone_id, allow_overlap,
                     retain_successful_releases, revision, updated_at)
                VALUES ($map, $enabled, $interval, $zone, $overlap, $retain, $revision, $updated);
                """;
            try
            {
                await using var cmd = PolicyCommand(db, insert, policy);
                await cmd.ExecuteNonQueryAsync(ct);
                return policy;
            }
            catch (MySqlException e) when (IsConstraint(e))
            {
                throw new CatalogConflictException($"Bake policy for map '{policy.MapId}' already exists.", e);
            }
        }

        const string update = """
            UPDATE bake_policies
            SET enabled = $enabled, interval_ticks = $interval, time_zone_id = $zone,
                allow_overlap = $overlap, retain_successful_releases = $retain,
                revision = $revision, updated_at = $updated
            WHERE map_id = $map AND revision = $expected;
            """;
        await using var updateCommand = PolicyCommand(db, update, policy, expectedRevision);
        if (await updateCommand.ExecuteNonQueryAsync(ct) == 0)
            throw new CatalogConcurrencyException(
                $"Bake policy '{policy.MapId}' was changed after revision {expectedRevision}.");
        return policy;
    }

    public async Task<BakeRun> CreateAsync(BakeRun run, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO bake_runs
                (id, map_id, draft_revision, definition_json, status, created_at, started_at, completed_at, error)
            VALUES
                ($id, $map, $revision, $definition, $status, $created, $started, $completed, $error);
            """;
        try
        {
            await using var db = await OpenAsync(ct);
            await using var cmd = Command(db, sql,
                ("$id", run.Id), ("$map", run.MapId), ("$revision", run.DraftRevision),
                ("$definition", Json(run.Definition)), ("$status", run.Status.ToString()),
                ("$created", Timestamp(run.CreatedAt)), ("$started", NullableTimestamp(run.StartedAt)),
                ("$completed", NullableTimestamp(run.CompletedAt)), ("$error", run.Error));
            await cmd.ExecuteNonQueryAsync(ct);
            return run;
        }
        catch (MySqlException e) when (IsConstraint(e))
        {
            throw new CatalogConflictException($"Bake run '{run.Id}' already exists or its map is invalid.", e);
        }
    }

    async Task<BakeRun?> IBakeRunRepository.FindAsync(Guid id, CancellationToken ct)
    {
        await using var db = await OpenAsync(ct);
        await using var cmd = Command(db, "SELECT * FROM bake_runs WHERE id = $id;", ("$id", id));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadRun(reader) : null;
    }

    async Task<IReadOnlyList<BakeRun>> IBakeRunRepository.ListForMapAsync(Guid mapId, CancellationToken ct)
    {
        await using var db = await OpenAsync(ct);
        await using var cmd = Command(db,
            "SELECT * FROM bake_runs WHERE map_id = $map ORDER BY created_at DESC;", ("$map", mapId));
        return await ReadAllAsync(cmd, ReadRun, ct);
    }

    public async Task<CatalogRelease> CreateAsync(CatalogRelease release, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO releases (id, map_id, bake_run_id, version, artifact_location, published_at)
            VALUES ($id, $map, $run, $version, $artifact, $published);
            """;
        try
        {
            await using var db = await OpenAsync(ct);
            await using var cmd = Command(db, sql,
                ("$id", release.Id), ("$map", release.MapId), ("$run", release.BakeRunId),
                ("$version", release.Version), ("$artifact", release.ArtifactLocation),
                ("$published", Timestamp(release.PublishedAt)));
            await cmd.ExecuteNonQueryAsync(ct);
            return release;
        }
        catch (MySqlException e) when (IsConstraint(e))
        {
            throw new CatalogConflictException(
                $"Release '{release.Version}' already exists or its map/run relationship is invalid.", e);
        }
    }

    async Task<IReadOnlyList<CatalogRelease>> IReleaseRepository.ListForMapAsync(
        Guid mapId, CancellationToken ct)
    {
        await using var db = await OpenAsync(ct);
        await using var cmd = Command(db,
            "SELECT * FROM releases WHERE map_id = $map ORDER BY published_at DESC;", ("$map", mapId));
        return await ReadAllAsync(cmd, ReadRelease, ct);
    }

    private async Task<CatalogConnection?> FindConnectionAsync(
        string predicate, object value, CancellationToken ct, string? workspace = null)
    {
        await using var db = await OpenAsync(ct);
        await using var cmd = Command(db, $"SELECT * FROM connections WHERE {predicate};",
            ("$value", value), ("$workspace", workspace));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadConnection(reader) : null;
    }

    private async Task<CatalogDataset?> FindDatasetAsync(
        string predicate, object value, CancellationToken ct, string? workspace = null)
    {
        await using var db = await OpenAsync(ct);
        await using var cmd = Command(db, $"SELECT * FROM datasets WHERE {predicate};",
            ("$value", value), ("$workspace", workspace));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadDataset(reader) : null;
    }

    private async Task<CatalogMap?> FindMapAsync(
        string predicate, object value, CancellationToken ct, string? workspace = null)
    {
        await using var db = await OpenAsync(ct);
        await using var cmd = Command(db, $"SELECT * FROM maps WHERE {predicate};",
            ("$value", value), ("$workspace", workspace));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadMap(reader) : null;
    }

    private async Task<IReadOnlyList<T>> ListAsync<T>(
        string table,
        Func<MySqlDataReader, T> materialize,
        string workspace,
        bool includeArchived,
        CancellationToken ct)
    {
        await using var db = await OpenAsync(ct);
        string sql = $"SELECT * FROM {table} WHERE workspace_key = $workspace" +
            (includeArchived ? "" : " AND archived = 0") + " ORDER BY slug;";
        await using var cmd = Command(db, sql, ("$workspace", workspace));
        return await ReadAllAsync(cmd, materialize, ct);
    }

    private async Task ThrowMapWriteFailure(Guid id, long expectedRevision, CancellationToken ct)
    {
        var current = await FindMapAsync("id = $value", id, ct);
        if (current is null) throw new CatalogNotFoundException($"Unknown map '{id}'.");
        throw new CatalogConcurrencyException(
            $"Map '{current.Draft.Slug}' was changed after revision {expectedRevision}.");
    }

    private async Task<MySqlConnection> OpenAsync(CancellationToken ct)
    {
        var db = new MySqlConnection(_connectionString);
        await db.OpenAsync(ct);
        return db;
    }

    private void EnsureCreated()
    {
        using var db = new MySqlConnection(_connectionString);
        db.Open();
        ExecuteSchemaStatement(db, """
            CREATE TABLE IF NOT EXISTS catalog_schema (
                singleton_id TINYINT PRIMARY KEY,
                version INT NOT NULL,
                CONSTRAINT ck_catalog_schema_singleton CHECK (singleton_id = 1)
            ) ENGINE=InnoDB;
            """);
        using (var versionCommand = db.CreateCommand())
        {
            versionCommand.CommandText = "SELECT version FROM catalog_schema WHERE singleton_id = 1;";
            object? storedVersion = versionCommand.ExecuteScalar();
            long version = storedVersion is null ? 0 : Convert.ToInt64(storedVersion);
            if (version > 1)
                throw new InvalidOperationException(
                    $"Database schema version {version} is newer than this application's supported version 1.");
            if (version == 0)
                ExecuteSchemaStatement(db, "INSERT INTO catalog_schema (singleton_id, version) VALUES (1, 1);");
        }
        string[] schema =
        [
            """
            CREATE TABLE IF NOT EXISTS connections (
                id CHAR(36) CHARACTER SET ascii PRIMARY KEY,
                workspace_key VARCHAR(191) NOT NULL,
                slug VARCHAR(191) NOT NULL,
                draft_json LONGTEXT NOT NULL,
                secrets_json LONGTEXT NOT NULL,
                revision BIGINT NOT NULL,
                archived BOOLEAN NOT NULL,
                created_at VARCHAR(40) NOT NULL,
                updated_at VARCHAR(40) NOT NULL,
                CONSTRAINT ck_connections_revision CHECK (revision > 0),
                UNIQUE KEY ux_connections_workspace_slug (workspace_key, slug)
            ) ENGINE=InnoDB;
            """,
            """
            CREATE TABLE IF NOT EXISTS datasets (
                id CHAR(36) CHARACTER SET ascii PRIMARY KEY,
                workspace_key VARCHAR(191) NOT NULL,
                connection_id CHAR(36) CHARACTER SET ascii NOT NULL,
                slug VARCHAR(191) NOT NULL,
                draft_json LONGTEXT NOT NULL,
                revision BIGINT NOT NULL,
                archived BOOLEAN NOT NULL,
                created_at VARCHAR(40) NOT NULL,
                updated_at VARCHAR(40) NOT NULL,
                CONSTRAINT ck_datasets_revision CHECK (revision > 0),
                CONSTRAINT fk_datasets_connection FOREIGN KEY (connection_id) REFERENCES connections(id) ON DELETE RESTRICT,
                UNIQUE KEY ux_datasets_workspace_slug (workspace_key, slug),
                KEY ix_datasets_connection (connection_id)
            ) ENGINE=InnoDB;
            """,
            """
            CREATE TABLE IF NOT EXISTS maps (
                id CHAR(36) CHARACTER SET ascii PRIMARY KEY,
                workspace_key VARCHAR(191) NOT NULL,
                dataset_id CHAR(36) CHARACTER SET ascii NOT NULL,
                slug VARCHAR(191) NOT NULL,
                draft_json LONGTEXT NOT NULL,
                revision BIGINT NOT NULL,
                archived BOOLEAN NOT NULL,
                created_at VARCHAR(40) NOT NULL,
                updated_at VARCHAR(40) NOT NULL,
                CONSTRAINT ck_maps_revision CHECK (revision > 0),
                CONSTRAINT fk_maps_dataset FOREIGN KEY (dataset_id) REFERENCES datasets(id) ON DELETE RESTRICT,
                UNIQUE KEY ux_maps_workspace_slug (workspace_key, slug),
                KEY ix_maps_dataset (dataset_id)
            ) ENGINE=InnoDB;
            """,
            """
            CREATE TABLE IF NOT EXISTS bake_policies (
                map_id CHAR(36) CHARACTER SET ascii PRIMARY KEY,
                enabled BOOLEAN NOT NULL,
                interval_ticks BIGINT NULL,
                time_zone_id VARCHAR(191) NOT NULL,
                allow_overlap BOOLEAN NOT NULL,
                retain_successful_releases INT NOT NULL,
                revision BIGINT NOT NULL,
                updated_at VARCHAR(40) NOT NULL,
                CONSTRAINT ck_bake_policies_retention CHECK (retain_successful_releases > 0),
                CONSTRAINT ck_bake_policies_revision CHECK (revision > 0),
                CONSTRAINT fk_bake_policies_map FOREIGN KEY (map_id) REFERENCES maps(id) ON DELETE CASCADE
            ) ENGINE=InnoDB;
            """,
            """
            CREATE TABLE IF NOT EXISTS bake_runs (
                id CHAR(36) CHARACTER SET ascii PRIMARY KEY,
                map_id CHAR(36) CHARACTER SET ascii NOT NULL,
                draft_revision BIGINT NOT NULL,
                definition_json LONGTEXT NOT NULL,
                status VARCHAR(32) NOT NULL,
                created_at VARCHAR(40) NOT NULL,
                started_at VARCHAR(40) NULL,
                completed_at VARCHAR(40) NULL,
                error LONGTEXT NULL,
                CONSTRAINT fk_bake_runs_map FOREIGN KEY (map_id) REFERENCES maps(id) ON DELETE RESTRICT,
                UNIQUE KEY ux_bake_runs_map_id (map_id, id),
                KEY ix_bake_runs_map_created (map_id, created_at DESC)
            ) ENGINE=InnoDB;
            """,
            """
            CREATE TABLE IF NOT EXISTS releases (
                id CHAR(36) CHARACTER SET ascii PRIMARY KEY,
                map_id CHAR(36) CHARACTER SET ascii NOT NULL,
                bake_run_id CHAR(36) CHARACTER SET ascii NOT NULL,
                version VARCHAR(191) NOT NULL,
                artifact_location TEXT NOT NULL,
                published_at VARCHAR(40) NOT NULL,
                CONSTRAINT fk_releases_map FOREIGN KEY (map_id) REFERENCES maps(id) ON DELETE RESTRICT,
                CONSTRAINT fk_releases_run FOREIGN KEY (map_id, bake_run_id) REFERENCES bake_runs(map_id, id) ON DELETE RESTRICT,
                UNIQUE KEY ux_releases_map_version (map_id, version),
                KEY ix_releases_map_published (map_id, published_at DESC)
            ) ENGINE=InnoDB;
            """,
        ];
        foreach (string statement in schema) ExecuteSchemaStatement(db, statement);
    }

    private static void ExecuteSchemaStatement(MySqlConnection db, string sql)
    {
        using var command = db.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private MySqlCommand Command(
        MySqlConnection db, string sql, params (string Name, object? Value)[] parameters)
    {
        var cmd = db.CreateCommand();
        cmd.CommandText = sql.Replace('$', '@');
        cmd.CommandTimeout = CommandTimeoutSeconds;
        foreach (var (name, value) in parameters)
        {
            if (value is null && !sql.Contains(name, StringComparison.Ordinal)) continue;
            cmd.Parameters.AddWithValue(name.Replace('$', '@'), DbValue(value));
        }
        return cmd;
    }

    private MySqlCommand PolicyCommand(
        MySqlConnection db, string sql, BakePolicy policy, long? expected = null) => Command(db, sql,
        ("$map", policy.MapId), ("$enabled", policy.Enabled),
        ("$interval", policy.Interval?.Ticks), ("$zone", policy.TimeZoneId),
        ("$overlap", policy.AllowOverlap), ("$retain", policy.RetainSuccessfulReleases),
        ("$revision", policy.Revision), ("$updated", Timestamp(policy.UpdatedAt)), ("$expected", expected));

    private static object DbValue(object? value) => value switch
    {
        null => DBNull.Value,
        Guid guid => guid.ToString("D"),
        bool boolean => boolean ? 1 : 0,
        _ => value,
    };

    private static CatalogConnection ReadConnection(MySqlDataReader r) => new(
        r.GetGuid(r.GetOrdinal("id")),
        r.GetString(r.GetOrdinal("workspace_key")),
        FromJson<ConnectionDraft>(r.GetString(r.GetOrdinal("draft_json"))),
        r.GetInt64(r.GetOrdinal("revision")),
        r.GetBoolean(r.GetOrdinal("archived")),
        ParseTimestamp(r.GetString(r.GetOrdinal("created_at"))),
        ParseTimestamp(r.GetString(r.GetOrdinal("updated_at"))));

    private static CatalogDataset ReadDataset(MySqlDataReader r) => new(
        r.GetGuid(r.GetOrdinal("id")),
        r.GetString(r.GetOrdinal("workspace_key")),
        FromJson<DatasetDraft>(r.GetString(r.GetOrdinal("draft_json"))),
        r.GetInt64(r.GetOrdinal("revision")),
        r.GetBoolean(r.GetOrdinal("archived")),
        ParseTimestamp(r.GetString(r.GetOrdinal("created_at"))),
        ParseTimestamp(r.GetString(r.GetOrdinal("updated_at"))));

    private static CatalogMap ReadMap(MySqlDataReader r) => new(
        r.GetGuid(r.GetOrdinal("id")),
        r.GetString(r.GetOrdinal("workspace_key")),
        FromJson<MapDraft>(r.GetString(r.GetOrdinal("draft_json"))),
        r.GetInt64(r.GetOrdinal("revision")),
        r.GetBoolean(r.GetOrdinal("archived")),
        ParseTimestamp(r.GetString(r.GetOrdinal("created_at"))),
        ParseTimestamp(r.GetString(r.GetOrdinal("updated_at"))));

    private static BakePolicy ReadPolicy(MySqlDataReader r) => new(
        r.GetGuid(r.GetOrdinal("map_id")),
        r.GetBoolean(r.GetOrdinal("enabled")),
        r.IsDBNull(r.GetOrdinal("interval_ticks")) ? null : TimeSpan.FromTicks(r.GetInt64(r.GetOrdinal("interval_ticks"))),
        r.GetString(r.GetOrdinal("time_zone_id")),
        r.GetBoolean(r.GetOrdinal("allow_overlap")),
        r.GetInt32(r.GetOrdinal("retain_successful_releases")),
        r.GetInt64(r.GetOrdinal("revision")),
        ParseTimestamp(r.GetString(r.GetOrdinal("updated_at"))));

    private static BakeRun ReadRun(MySqlDataReader r) => new(
        r.GetGuid(r.GetOrdinal("id")),
        r.GetGuid(r.GetOrdinal("map_id")),
        r.GetInt64(r.GetOrdinal("draft_revision")),
        FromJson<Domain.Model.ViewConfig>(r.GetString(r.GetOrdinal("definition_json"))),
        Enum.Parse<BakeRunStatus>(r.GetString(r.GetOrdinal("status"))),
        ParseTimestamp(r.GetString(r.GetOrdinal("created_at"))),
        NullableTimestamp(r, "started_at"),
        NullableTimestamp(r, "completed_at"),
        NullableString(r, "error"));

    private static CatalogRelease ReadRelease(MySqlDataReader r) => new(
        r.GetGuid(r.GetOrdinal("id")),
        r.GetGuid(r.GetOrdinal("map_id")),
        r.GetGuid(r.GetOrdinal("bake_run_id")),
        r.GetString(r.GetOrdinal("version")),
        r.GetString(r.GetOrdinal("artifact_location")),
        ParseTimestamp(r.GetString(r.GetOrdinal("published_at"))));

    private static async Task<IReadOnlyList<T>> ReadAllAsync<T>(
        MySqlCommand cmd, Func<MySqlDataReader, T> materialize, CancellationToken ct)
    {
        var result = new List<T>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(materialize(reader));
        return result;
    }

    private static string Json<T>(T value) => ColossusJson.Serialize(value);
    private static T FromJson<T>(string value) => ColossusJson.Deserialize<T>(value);
    private static string Timestamp(DateTimeOffset value) => value.ToUniversalTime().ToString("O");
    private static object NullableTimestamp(DateTimeOffset? value) => value is null ? DBNull.Value : Timestamp(value.Value);
    private static DateTimeOffset ParseTimestamp(string value) => DateTimeOffset.Parse(value).ToUniversalTime();
    private static DateTimeOffset? NullableTimestamp(MySqlDataReader reader, string name) =>
        reader.IsDBNull(reader.GetOrdinal(name)) ? null : ParseTimestamp(reader.GetString(reader.GetOrdinal(name)));
    private static string? NullableString(MySqlDataReader reader, string name) =>
        reader.IsDBNull(reader.GetOrdinal(name)) ? null : reader.GetString(reader.GetOrdinal(name));
    private static bool IsConstraint(MySqlException e) => e.Number is 1062 or 1451 or 1452 or 3819;
}
