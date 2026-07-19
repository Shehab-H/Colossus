using System.Text.Json.Serialization;

namespace Colossus.Domain.Model;

public readonly record struct TileMeta(int Z, int X, int Y, long Count, bool IsLeaf)
{
    [JsonIgnore] // derived — serializing it doubled every tile entry in manifest.json
    public TileId Id => new(Z, X, Y);
}

/// <summary>The boot document a client reads (via latest.json) to render a baked view.</summary>
public sealed record Manifest
{
    public int SchemaVersion { get; init; } = 1;
    /// <summary>Tile binary format. 2 = the zero-copy contract (single record batch, no nulls,
    /// tile-global triangle indices, canonical dictionaries, f32 measures) the client decodes as
    /// typed-array views over the one fetched buffer. Absent/0/1 on older manifests selects the
    /// copy-based format-1 decode, which the client keeps until every view is re-baked.</summary>
    public int TileFormat { get; init; }
    public required string Version { get; init; }
    public required ViewConfig View { get; init; }
    /// <summary>The reduction the planner chose for this bake (derived from data shape, not authored).
    /// The client reads it to label fidelity — e.g. an aggregate pyramid marks its coarse levels.</summary>
    public required ReductionKind Reduction { get; init; }
    public required string Regime { get; init; }
    public required Bbox Root { get; init; }
    public required int MinZoom { get; init; }
    public required int MaxZoom { get; init; }
    public required int TilePointBudget { get; init; }
    public required long TotalPoints { get; init; }
    /// <summary>Rows the source reported at bake time. <see cref="TotalPoints"/> is *defined* as the leaf
    /// sum, so only this — a number the reducer never produced — can witness that the tiling partitioned
    /// the source rather than duplicating or dropping rows. Null on manifests from older bakes.</summary>
    public long? SourceRows { get; init; }
    public required IReadOnlyList<TileMeta> Tiles { get; init; }
    /// <summary>Per-channel data domains scanned from the full staged extract at bake time, keyed by
    /// channel name. Spares the client a root-tile scan at load and — unlike that tile, which is a
    /// sample — sees every row, so no category can be missing. Null on manifests from older bakes;
    /// the client falls back to its tile scan.</summary>
    public IReadOnlyDictionary<string, ChannelDomain>? ChannelDomains { get; init; }

    /// <summary>Group regime only: the derived perMark/perFact split so the client can
    /// route a filter as a GPU predicate (perMark) or a fold context (perFact). Null in the row regime.</summary>
    public FactChannels? FactChannels { get; init; }

    /// <summary>True when the bake wrote a <c>z/x/y.facts.arrow</c> companion beside every tile.</summary>
    public bool CompanionTiles { get; init; }

    /// <summary>The companion grain columns (perFact dict + temporal channels), so the client knows what
    /// dimensions its fact partials are keyed by. Null in the row regime.</summary>
    public IReadOnlyList<string>? GrainChannels { get; init; }

    /// <summary>Companion packaging (companion-scale R2, extended by R1/R5). Every companion tile's blocks
    /// live in one per-version archive, fetched by HTTP range. Null selects the per-file layout (older
    /// bakes). A slab bake sets <see cref="CompanionPack.Format"/> = "slab" and packs both leaf and internal
    /// levels (internal compression, R5); a row-form R2 bake leaves it "row" with leaf-only entries.</summary>
    public CompanionPack? CompanionPack { get; init; }

    /// <summary>Group-regime slab companion metadata (companion-scale R1). Null on row-form bakes — the
    /// client keeps the row-form decode/fold. Present ⇒ the pack's blocks are slab planes.</summary>
    public CompanionSlab? CompanionSlab { get; init; }

    /// <summary>Render-tile packaging (tile-transfer Phase 3). Every tile's columns live as independently
    /// compressed blocks in one per-version archive, fetched by HTTP range, so a first paint ships geometry
    /// and the active colour channel alone and never the measure planes it will not read. Null selects the
    /// per-tile file layout (formats 1/2 and older bakes) — the same gating pattern
    /// <see cref="CompanionPack"/> uses. Present ⇒ no per-tile <c>.arrow</c>/<c>.arrow.br</c> exists.</summary>
    public RenderPack? RenderPack { get; init; }

    /// <summary>Group-regime only (companion-scale R4): the baked facts Parquet, relative to the version
    /// directory, that the server fold reads (DuckDB over the baked artifact, never the source DB — RULES
    /// R5). Retained per version so a fold is reproducible against exactly the bake it prices. Null in the
    /// row regime and on older group bakes that predate R4.</summary>
    public string? FactsParquet { get; init; }

    /// <summary>Group-regime only (companion-scale R4): the planner's fold-execution route, priced at bake
    /// from measured companion bytes. The client reads <see cref="FoldRoute.Execution"/> to pick the local
    /// fold or the remote fold endpoint behind the same seam. Null in the row regime.</summary>
    public FoldRoute? FoldRoute { get; init; }
}

/// <summary>The planner's fold-execution decision for a group-regime view (companion-scale R4). Priced at
/// bake from the measured per-interaction companion transfer (plane-split bytes, R5): a view whose worst
/// leaf tile exceeds the budget routes its fold to the server, shipping folded columns instead of facts.
/// The extra fields are diagnostics — the client obeys only <see cref="Execution"/>.</summary>
public sealed record FoldRoute
{
    /// <summary>"client" (fold in the browser over fetched companion planes) or "remote" (fold on the
    /// server over the baked facts Parquet, ship folded columns).</summary>
    public required string Execution { get; init; }
    /// <summary>Measured worst single-tile per-interaction transfer (the color measure's plane-split bytes)
    /// — the dense-leaf number the budget guards.</summary>
    public long WorstTileBytes { get; init; }
    /// <summary>Measured estimate for a dense screenful (sum of the largest N tiles' per-interaction bytes).</summary>
    public long ViewportEstimateBytes { get; init; }
    /// <summary>The configured budget the worst tile was compared against.</summary>
    public long BudgetBytes { get; init; }
    /// <summary>True when the route was forced remote by config regardless of price (benchmark/testing).</summary>
    public bool Forced { get; init; }
}

/// <summary>The companion archive and its directory. <see cref="File"/> is relative to the version
/// directory; <see cref="Codec"/> is an encoding the browser-native <c>DecompressionStream</c> accepts;
/// <see cref="Entries"/> maps a tile key (<c>z/x/y</c>) to the <c>[offset, length]</c> byte range of its
/// whole region — compression lives inside the archive because <c>Content-Encoding</c> doesn't compose
/// with ranges. A slab bake (<see cref="Format"/> = "slab") additionally fills <see cref="PlaneEntries"/>
/// with per-plane ranges so a fold fetches only the planes its active measures need (R5 plane split).</summary>
public sealed record CompanionPack
{
    public required string File { get; init; }
    /// <summary>Block codec: "gzip" (browser-native <c>DecompressionStream</c>) or "zstd" (companion-scale
    /// Work Item C — high-level zstd with a trained shared dictionary; the client uses a WASM zstd decoder).</summary>
    public required string Codec { get; init; }
    public required IReadOnlyDictionary<string, long[]> Entries { get; init; }

    /// <summary>Trained shared dictionary path (relative to the version directory), for the "zstd" codec: one
    /// dictionary per (view, version) over sampled blocks, so the small sliced cell-row blocks compress well
    /// (companion-scale R5). The client fetches it once and loads it into its zstd decoder. Null when the
    /// codec needs no dictionary (gzip, or a bake with too few blocks to train one).</summary>
    public string? Dict { get; init; }

    /// <summary>SHA-256 (hex) of the dictionary file — recorded so the client can key/verify it. Null with
    /// <see cref="Dict"/>.</summary>
    public string? DictHash { get; init; }

    /// <summary>"row" (R2 leaf pack of row-form companions) or "slab" (R1). Absent ⇒ "row" (older bakes).</summary>
    public string? Format { get; init; }

    /// <summary>Slab only: <c>tileKey → (planeName → [offset, length])</c>. Plane <c>"@idx"</c> is the CSR
    /// structure block (sparse layout); the rest are partial planes. A dense plane's region is the
    /// concatenation of its per-cell-row blocks (<see cref="SliceEntries"/>); a whole-plane fetch ranges the
    /// region and inflates each block. Null ⇒ whole-tile fetch only.</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, long[]>>? PlaneEntries { get; init; }

    /// <summary>Cell-run slice directory (companion-scale R5 second half): <c>tileKey → (planeName → per-cell
    /// compressed block lengths)</c>, present only for **dense** tiles (sparse opts out — SLAB-FORMAT §5). A
    /// dense plane is stored as one independently compressed block per cell row (all marks at one cell — the
    /// slice unit, §4b); cell <c>c</c>'s block is at <c>PlaneEntries[tile][plane][0] + Σ_{i&lt;c} lengths[i]</c>
    /// for <c>lengths[c]</c> bytes, so only the block lengths are recorded. The client fetches only the cell
    /// rows the active context reads; absence of this directory (or a sparse tile) selects the whole-block
    /// fetch. Blocks are raw little-endian typed-array bytes (f32/i32 per the partial), not Arrow — per-row
    /// Arrow framing would swamp a small tile's payload.</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, int[]>>? SliceEntries { get; init; }
}

/// <summary>The render-tile archive and its directory (tile-transfer Phase 3). One archive per (view,
/// version) replacing the per-tile render files; <see cref="File"/> is relative to the version directory.
/// Compression lives inside the archive, per block, because <c>Content-Encoding</c> does not compose with
/// Range requests.
///
/// <para><b>Block order is the design.</b> A tile's blocks are laid down in first-paint order — the
/// geometry group, then the default colour channel, then the filter-slot channels, then everything else —
/// so the default first paint is ONE contiguous byte range per tile and a whole-tile read is one range over
/// the tile's full span. The order is derived from the view's declared channel roles at bake time, never
/// authored and never keyed on channel names. Future formats must preserve this property the way the slab
/// format's cell order is preserved.</para></summary>
public sealed record RenderPack
{
    public required string File { get; init; }

    /// <summary>Block codec: "zstd" (with the trained shared <see cref="Dict"/>) or "gzip" (browser-native
    /// <c>DecompressionStream</c>).</summary>
    public required string Codec { get; init; }

    /// <summary><c>tileKey → (group → [offset, length])</c>. A group is one column, except the geometry
    /// group (<see cref="GeomGroup"/>) which carries the encoded geom3 payload for area marks or the
    /// representative x/y columns for point marks. Each block is a standalone single-batch Arrow IPC
    /// stream, so a column decodes alone and stays a zero-copy view over its own inflated buffer.</summary>
    public required IReadOnlyDictionary<string, IReadOnlyDictionary<string, long[]>> Entries { get; init; }

    /// <summary>The groups a first paint needs, in pack order: geometry, the default colour channel, and the
    /// filter-slot channels. Recorded rather than re-derived so the client's request set is exactly the run
    /// the writer laid out contiguously. Every other group is fetched lazily — on a colour switch or a
    /// click.</summary>
    public required IReadOnlyList<string> FirstPaint { get; init; }

    /// <summary>Trained shared dictionary path (relative to the version directory), for the "zstd" codec —
    /// one per (view, version) over sampled blocks, so small per-column blocks still compress well. Null
    /// when the codec needs none (gzip, or too few blocks to train one).</summary>
    public string? Dict { get; init; }

    /// <summary>SHA-256 (hex) of the dictionary file, so the client can key/verify it. Null with
    /// <see cref="Dict"/>.</summary>
    public string? DictHash { get; init; }

    /// <summary>The geometry group's reserved name. Not a channel name — the one group whose contents are
    /// decided by the mark, not by the view's channel list.</summary>
    public const string GeomGroup = "@geom";

    /// <summary>The channels a first paint needs, in block order, derived purely from the view's declared
    /// roles: the colour channel (what the default paint reads) followed by the filter-slot channels (the
    /// GPU filter attribute is built per tile at decode, so its columns must be resident too). Everything
    /// else — the other measures, the identity/inspect channels — is lazy. No channel name and no dataset
    /// shape appears here; a view that declares neither role simply has a geometry-only first paint.</summary>
    public static IReadOnlyList<string> FirstPaintChannels(ViewConfig view)
    {
        var ordered = new List<string>();
        void Add(string? name)
        {
            if (!string.IsNullOrEmpty(name) && !ordered.Contains(name, StringComparer.Ordinal)) ordered.Add(name);
        }
        Add(view.Encoding?.Color?.Channel);
        foreach (var f in view.Filters ?? []) Add(f.Channel);
        return ordered;
    }
}

/// <summary>The derived channel split of a group-regime view.</summary>
public sealed record FactChannels(IReadOnlyList<string> PerMark, IReadOnlyList<string> PerFact);

/// <summary>One channel's observed domain. Numeric channels carry min/max plus a quantile grid (the
/// client derives quantile-scale breaks from it); non-numeric channels carry their distinct values,
/// capped — a capped list sets <see cref="ValuesTruncated"/> and the client treats it as absent.</summary>
public sealed record ChannelDomain
{
    public IReadOnlyList<string>? Values { get; init; }
    public bool? ValuesTruncated { get; init; }
    public double? Min { get; init; }
    public double? Max { get; init; }
    public IReadOnlyList<double>? Quantiles { get; init; }
}

/// <summary>The single mutable pointer, flipped atomically once a bake commits.</summary>
public sealed record LatestPointer(string Version);
