# Seeds colossus.mobile_coverage: Ookla open-data mobile tiles (real geometry + densities) expanded
# with a deterministic synthetic operator dimension. This is the reference dataset for the dynamic-
# aggregation model (docs/VIEW_CONFIG.md): geometry is NOT unique — each z14 quadkey repeats per
# (quarter, operator), so per-mark values only exist as filter-dependent aggregates.
#
# Synthetic model (all deterministic — reseeding reproduces byte-identical data):
#   - 4 operators; regional base share from a z8-quadkey hash (dominance forms spatial clusters)
#   - temporal drift: apex gains ~6%/quarter, pulse loses ~5% — dominance flips over the range
#   - per-tile noise + per-operator quality factors on speed/latency
#
# Usage: pwsh scripts/seed-mobile-coverage.ps1   (idempotent: skips quarters already loaded)

$ErrorActionPreference = 'Stop'
$ch = $env:COLOSSUS_CH_URL; if (-not $ch) { $ch = 'http://localhost:8123' }
$user = $env:COLOSSUS_CH_USER; if (-not $user) { $user = 'colossus' }
$pass = $env:COLOSSUS_CH_PASSWORD; if (-not $pass) { $pass = 'colossus' }
$headers = @{ 'X-ClickHouse-User' = $user; 'X-ClickHouse-Key' = $pass }

# Global: every populated Ookla tile worldwide (data only exists where tests were run, so coverage
# follows population — no rectangular clip). z14 keeps tiles ~2.4km.
$zoom = 14
$quarters = @(
  @{ y = 2024; q = 1; d = '2024-01-01' }, @{ y = 2024; q = 2; d = '2024-04-01' },
  @{ y = 2024; q = 3; d = '2024-07-01' }, @{ y = 2024; q = 4; d = '2024-10-01' },
  @{ y = 2025; q = 1; d = '2025-01-01' }, @{ y = 2025; q = 2; d = '2025-04-01' },
  @{ y = 2025; q = 3; d = '2025-07-01' }, @{ y = 2025; q = 4; d = '2025-10-01' }
)

function Invoke-CH([string]$sql) {
  (Invoke-RestMethod -Method Post -Uri $ch -Headers $headers -Body $sql -TimeoutSec 3600)
}

Invoke-CH @"
CREATE TABLE IF NOT EXISTS colossus.mobile_coverage (
  quadkey       String,
  quarter       Date,
  operator      LowCardinality(String),
  tests         UInt32,
  sample_share  Float32,
  download_mbps Float32,
  upload_mbps   Float32,
  latency_ms    Float32
) ENGINE = MergeTree ORDER BY (quadkey, quarter, operator)
"@ | Out-Null

for ($t = 0; $t -lt $quarters.Count; $t++) {
  $qt = $quarters[$t]
  $existing = Invoke-CH "SELECT count() FROM colossus.mobile_coverage WHERE quarter = toDate('$($qt.d)')"
  if ([int64]$existing -gt 0) { Write-Host "skip $($qt.d) ($existing rows)"; continue }

  $url = "https://ookla-open-data.s3.amazonaws.com/parquet/performance/type=mobile/year=$($qt.y)/quarter=$($qt.q)/$($qt.d)_performance_mobile_tiles.parquet"
  Write-Host "loading $($qt.d) from $url"

  Invoke-CH @"
INSERT INTO colossus.mobile_coverage
WITH
  ['apex', 'nimbus', 'orbit', 'pulse'] AS ops,
  [1.15, 1.00, 0.90, 0.80] AS quality,
  [pow(1.06, $t), 1.0, pow(1.01, $t), pow(0.95, $t)] AS drift,
  arrayMap(i -> (
      (0.5 + bitAnd(bitShiftRight(cityHash64(substring(qk, 1, 8)), (i - 1) * 8), 255) / 255.0)
      * drift[i]
      * (0.8 + (cityHash64(qk, ops[i]) % 1000) / 2500.0)
    ), [1, 2, 3, 4]) AS w,
  arrayMap(i -> w[i] / arraySum(w), [1, 2, 3, 4]) AS shares
SELECT
  qk AS quadkey,
  toDate('$($qt.d)') AS quarter,
  op.1 AS operator,
  toUInt32(round(tests_sum * op.2)) AS tests,
  toFloat32(op.2) AS sample_share,
  toFloat32(d_mbps * op.3 * (0.90 + (cityHash64(qk, op.1, 'd') % 100) / 500.0)) AS download_mbps,
  toFloat32(u_mbps * op.3 * (0.90 + (cityHash64(qk, op.1, 'u') % 100) / 500.0)) AS upload_mbps,
  toFloat32(lat_ms * (2.0 - op.3) * (0.90 + (cityHash64(qk, op.1, 'l') % 100) / 500.0)) AS latency_ms
FROM (
  SELECT
    assumeNotNull(substring(quadkey, 1, $zoom)) AS qk,
    sum(assumeNotNull(tests)) AS tests_sum,
    sum(assumeNotNull(avg_d_kbps) * assumeNotNull(tests)) / sum(assumeNotNull(tests)) / 1000.0 AS d_mbps,
    sum(assumeNotNull(avg_u_kbps) * assumeNotNull(tests)) / sum(assumeNotNull(tests)) / 1000.0 AS u_mbps,
    sum(assumeNotNull(avg_lat_ms) * assumeNotNull(tests)) / sum(assumeNotNull(tests)) AS lat_ms
  FROM s3('$url', 'NOSIGN', 'Parquet')
  WHERE quadkey IS NOT NULL AND tests > 0
  GROUP BY qk
)
ARRAY JOIN arrayZip(ops, shares, quality) AS op
WHERE toUInt32(round(tests_sum * op.2)) >= 1
"@ | Out-Null

  $count = Invoke-CH "SELECT count() FROM colossus.mobile_coverage WHERE quarter = toDate('$($qt.d)')"
  Write-Host "  -> $count rows"
}

Invoke-CH "SELECT concat('total: ', toString(count()), ' rows, ', toString(uniqExact(quadkey)), ' distinct geometries') FROM colossus.mobile_coverage" | Write-Host
