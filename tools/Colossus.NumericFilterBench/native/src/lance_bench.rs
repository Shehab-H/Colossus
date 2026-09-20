use std::ffi::{CStr, c_char};
use std::path::Path;
use std::sync::Arc;
use std::time::Instant;

use datafusion::arrow::array::{Array, Float32Array, Int64Array};
use datafusion::datasource::file_format::options::ParquetReadOptions;
use datafusion::execution::context::{SessionConfig, SessionContext};
use datafusion::physical_plan::{ExecutionPlan, collect, displayable};
use lance::Dataset;
use lance::datafusion::LanceTableProvider;
use lance::dataset::WriteParams;
use lance::dataset::write::InsertBuilder;
use lance::index::DatasetIndexExt;
use lance_index::IndexType;
use lance_index::scalar::ScalarIndexParams;
use tokio::runtime::{Builder, Runtime};

const QUARTER_SECONDS: i64 = 7_889_400;

#[repr(C)]
pub struct LanceQueryMetrics {
    pub engine_ms: f64,
    pub rows: u64,
    pub checksum: u64,
    pub rows_scanned: u64,
    pub fragments_scanned: u64,
    pub ranges_scanned: u64,
    pub bytes_read: u64,
    pub indices_loaded: u64,
    pub index_parts_loaded: u64,
    pub index_comparisons: u64,
    pub index_cache_hits: u64,
    pub index_cache_misses: u64,
}

pub struct LanceEngine {
    runtime: Runtime,
    threads: usize,
    dataset: Arc<Dataset>,
}

impl LanceEngine {
    fn open(directory: &Path, threads: usize) -> Result<Self, String> {
        let runtime = runtime(threads)?;
        let uri = directory.to_string_lossy().into_owned();
        let dataset = runtime
            .block_on(Dataset::open(&uri))
            .map_err(|error| error.to_string())?;
        Ok(Self {
            runtime,
            threads,
            dataset: Arc::new(dataset),
        })
    }

    fn query(
        &self,
        operator: Option<&str>,
        quarter: i32,
        low_timestamp: i64,
        high_timestamp: i64,
        explain: bool,
        mut output: Option<&mut [f32]>,
    ) -> Result<LanceQueryMetrics, String> {
        let started = Instant::now();
        let config = SessionConfig::new()
            .with_target_partitions(self.threads)
            .with_batch_size(65_536);
        let context = SessionContext::new_with_config(config);
        context
            .register_table(
                "facts",
                Arc::new(LanceTableProvider::new(
                    Arc::clone(&self.dataset),
                    false,
                    false,
                )),
            )
            .map_err(|error| error.to_string())?;

        let mut predicates = vec![format!(
            "timestamp_seconds BETWEEN {low_timestamp} AND {high_timestamp}"
        )];
        if let Some(value) = operator {
            predicates.push(format!("operator = '{}'", value.replace('\'', "''")));
        }
        if quarter >= 0 {
            predicates.push(format!("quarter = {quarter}"));
        }
        let sql = format!(
            "SELECT x, y, \
             CAST(SUM(CAST(ROUND(download_mbps * 1000.0) AS BIGINT) * \
                      CAST(ROUND(tests * 1000.0) AS BIGINT)) AS BIGINT) AS numerator, \
             CAST(SUM(CAST(ROUND(tests * 1000.0) AS BIGINT)) AS BIGINT) AS denominator \
             FROM facts WHERE {} GROUP BY x, y",
            predicates.join(" AND ")
        );

        let (batches, plan) = self.runtime.block_on(async {
            let frame = context.sql(&sql).await.map_err(|error| error.to_string())?;
            let plan = frame
                .create_physical_plan()
                .await
                .map_err(|error| error.to_string())?;
            let batches = collect(Arc::clone(&plan), context.task_ctx())
                .await
                .map_err(|error| error.to_string())?;
            Ok::<_, String>((batches, plan))
        })?;

        let mut rows = 0u64;
        let mut checksum = 0u64;
        let mut output_row = 0usize;
        for batch in batches {
            let x = batch
                .column(0)
                .as_any()
                .downcast_ref::<Float32Array>()
                .ok_or("Lance result x is not Float32")?;
            let y = batch
                .column(1)
                .as_any()
                .downcast_ref::<Float32Array>()
                .ok_or("Lance result y is not Float32")?;
            let numerator = batch
                .column(2)
                .as_any()
                .downcast_ref::<Int64Array>()
                .ok_or("Lance result numerator is not Int64")?;
            let denominator = batch
                .column(3)
                .as_any()
                .downcast_ref::<Int64Array>()
                .ok_or("Lance result denominator is not Int64")?;
            for row in 0..batch.num_rows() {
                let numerator_value = numerator.value(row);
                let denominator_value = denominator.value(row);
                checksum ^= aggregate_hash(
                    x.value(row),
                    y.value(row),
                    numerator_value,
                    denominator_value,
                );
                if let Some(values) = output.as_deref_mut() {
                    if output_row < values.len() {
                        values[output_row] = if denominator_value == 0 {
                            f32::NAN
                        } else {
                            (numerator_value as f64 / denominator_value as f64 / 1000.0) as f32
                        };
                    }
                    output_row += 1;
                }
            }
            rows += batch.num_rows() as u64;
        }
        if let Some(values) = output.as_deref_mut() {
            let filled = output_row.min(values.len());
            values[filled..].fill(f32::NAN);
        }

        if explain {
            eprintln!("LANCE_PHYSICAL_PLAN\n{}", displayable(plan.as_ref()).indent(true));
        }

        Ok(LanceQueryMetrics {
            engine_ms: started.elapsed().as_secs_f64() * 1000.0,
            rows,
            checksum,
            rows_scanned: sum_metric(plan.as_ref(), "rows_scanned"),
            fragments_scanned: sum_metric(plan.as_ref(), "fragments_scanned"),
            ranges_scanned: sum_metric(plan.as_ref(), "ranges_scanned"),
            bytes_read: sum_metric(plan.as_ref(), "bytes_read"),
            indices_loaded: sum_metric(plan.as_ref(), "indices_loaded"),
            index_parts_loaded: sum_metric(plan.as_ref(), "parts_loaded"),
            index_comparisons: sum_metric(plan.as_ref(), "index_comparisons"),
            index_cache_hits: sum_metric(plan.as_ref(), "index_cache_hits"),
            index_cache_misses: sum_metric(plan.as_ref(), "index_cache_misses"),
        })
    }
}

async fn build_dataset(parquet: &Path, directory: &Path, threads: usize) -> Result<(), String> {
    if directory.exists() {
        return Err(format!(
            "refusing to overwrite Lance dataset: {}",
            directory.display()
        ));
    }
    let config = SessionConfig::new()
        .with_target_partitions(threads)
        .with_batch_size(65_536);
    let context = SessionContext::new_with_config(config);
    context
        .register_parquet(
            "source",
            parquet.to_string_lossy().as_ref(),
            ParquetReadOptions::default(),
        )
        .await
        .map_err(|error| error.to_string())?;

    let frame = context
        .sql(&format!(
            "SELECT x, y, operator, quarter, tests, download_mbps, \
             CAST(CAST(quarter AS BIGINT) * {QUARTER_SECONDS} + \
                  ABS(CAST(download_mbps * 1000.0 AS BIGINT) * 31 + \
                      CAST(upload_mbps * 1000.0 AS BIGINT) * 17 + \
                      CAST(latency_ms * 1000.0 AS BIGINT) * 13 + \
                      CAST(tests * 1000.0 AS BIGINT) * 7) % {QUARTER_SECONDS} \
                  AS BIGINT) AS timestamp_seconds \
             FROM source"
        ))
        .await
        .map_err(|error| error.to_string())?;
    let stream = frame
        .execute_stream()
        .await
        .map_err(|error| error.to_string())?;
    let params = WriteParams {
        max_rows_per_file: 1_000_000,
        max_rows_per_group: 65_536,
        ..Default::default()
    };
    let uri = directory.to_string_lossy().into_owned();
    let dataset = InsertBuilder::new(uri.as_str())
        .with_params(&params)
        .execute_stream(stream)
        .await
        .map_err(|error| error.to_string())?;
    println!(
        "lanceRows={} lanceVersion={}",
        dataset.count_rows(None).await.map_err(|error| error.to_string())?,
        dataset.version().version
    );

    let profile = context
        .sql(
            "SELECT operator, quarter, count(*) AS facts \
             FROM source GROUP BY operator, quarter ORDER BY operator, quarter",
        )
        .await
        .map_err(|error| error.to_string())?
        .collect()
        .await
        .map_err(|error| error.to_string())?;
    for batch in profile {
        println!("lanceSourceProfile={batch:?}");
    }
    Ok(())
}

async fn build_shard_dataset(
    parquet: &Path,
    directory: &Path,
    operator: &str,
    quarter: i32,
    threads: usize,
) -> Result<(), String> {
    if directory.exists() {
        return Err(format!(
            "refusing to overwrite Lance shard: {}",
            directory.display()
        ));
    }
    let config = SessionConfig::new()
        .with_target_partitions(threads)
        .with_batch_size(65_536);
    let context = SessionContext::new_with_config(config);
    context
        .register_parquet(
            "source",
            parquet.to_string_lossy().as_ref(),
            ParquetReadOptions::default(),
        )
        .await
        .map_err(|error| error.to_string())?;

    let escaped_operator = operator.replace('\'', "''");
    let frame = context
        .sql(&format!(
            "SELECT x, y, tests, download_mbps, \
             CAST(CAST(quarter AS BIGINT) * {QUARTER_SECONDS} + \
                  ABS(CAST(download_mbps * 1000.0 AS BIGINT) * 31 + \
                      CAST(upload_mbps * 1000.0 AS BIGINT) * 17 + \
                      CAST(latency_ms * 1000.0 AS BIGINT) * 13 + \
                      CAST(tests * 1000.0 AS BIGINT) * 7) % {QUARTER_SECONDS} \
                  AS BIGINT) AS timestamp_seconds \
             FROM source WHERE operator = '{escaped_operator}' AND quarter = {quarter}"
        ))
        .await
        .map_err(|error| error.to_string())?;
    let stream = frame
        .execute_stream()
        .await
        .map_err(|error| error.to_string())?;
    let params = WriteParams {
        max_rows_per_file: 1_000_000,
        max_rows_per_group: 65_536,
        ..Default::default()
    };
    let uri = directory.to_string_lossy().into_owned();
    let dataset = InsertBuilder::new(uri.as_str())
        .with_params(&params)
        .execute_stream(stream)
        .await
        .map_err(|error| error.to_string())?;
    println!(
        "lanceShardRows={} lanceVersion={} operator={} quarter={}",
        dataset.count_rows(None).await.map_err(|error| error.to_string())?,
        dataset.version().version,
        operator,
        quarter
    );
    Ok(())
}

async fn create_indexes(directory: &Path) -> Result<(), String> {
    let uri = directory.to_string_lossy().into_owned();
    let mut dataset = Dataset::open(&uri)
        .await
        .map_err(|error| error.to_string())?;
    for (column, index_type) in [
        ("operator", IndexType::Bitmap),
        ("quarter", IndexType::Bitmap),
        ("timestamp_seconds", IndexType::BTree),
    ] {
        let started = Instant::now();
        dataset
            .create_index(
                &[column],
                index_type,
                None,
                &ScalarIndexParams::default(),
                true,
            )
            .await
            .map_err(|error| format!("creating {index_type:?} on {column}: {error}"))?;
        println!(
            "lanceIndexColumn={column} lanceIndexType={index_type:?} buildSeconds={:.3}",
            started.elapsed().as_secs_f64()
        );
    }
    Ok(())
}

async fn create_shard_index(directory: &Path) -> Result<(), String> {
    let uri = directory.to_string_lossy().into_owned();
    let mut dataset = Dataset::open(&uri)
        .await
        .map_err(|error| error.to_string())?;
    let started = Instant::now();
    dataset
        .create_index(
            &["timestamp_seconds"],
            IndexType::BTree,
            None,
            &ScalarIndexParams::default(),
            true,
        )
        .await
        .map_err(|error| format!("creating BTree on timestamp_seconds: {error}"))?;
    println!(
        "lanceIndexColumn=timestamp_seconds lanceIndexType=BTree buildSeconds={:.3}",
        started.elapsed().as_secs_f64()
    );
    Ok(())
}

fn runtime(threads: usize) -> Result<Runtime, String> {
    Builder::new_multi_thread()
        .worker_threads(threads.max(1))
        .enable_all()
        .build()
        .map_err(|error| error.to_string())
}

fn sum_metric(plan: &dyn ExecutionPlan, name: &str) -> u64 {
    let own = plan
        .metrics()
        .and_then(|metrics| metrics.sum_by_name(name))
        .map(|value| value.as_usize() as u64)
        .unwrap_or_default();
    own + plan
        .children()
        .iter()
        .map(|child| sum_metric(child.as_ref(), name))
        .sum::<u64>()
}

fn aggregate_hash(x: f32, y: f32, numerator: i64, denominator: i64) -> u64 {
    let mark = ((x.to_bits() as u64) << 32) | y.to_bits() as u64;
    mix64(mark ^ mix64(numerator as u64) ^ mix64(denominator as u64))
}

fn mix64(mut z: u64) -> u64 {
    z = z.wrapping_add(0x9E3779B97F4A7C15);
    z = (z ^ (z >> 30)).wrapping_mul(0xBF58476D1CE4E5B9);
    z = (z ^ (z >> 27)).wrapping_mul(0x94D049BB133111EB);
    z ^ (z >> 31)
}

fn path_from_ptr<'a>(value: *const c_char) -> Result<&'a Path, String> {
    if value.is_null() {
        return Err("path pointer is null".into());
    }
    let value = unsafe { CStr::from_ptr(value) }
        .to_str()
        .map_err(|error| error.to_string())?;
    Ok(Path::new(value))
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn colossus_lance_build(
    parquet: *const c_char,
    directory: *const c_char,
    threads: i32,
) -> i32 {
    let result = (|| {
        if threads <= 0 {
            return Err("thread count must be positive".into());
        }
        let parquet = path_from_ptr(parquet)?;
        let directory = path_from_ptr(directory)?;
        runtime(threads as usize)?.block_on(build_dataset(parquet, directory, threads as usize))
    })();
    match result {
        Ok(()) => 0,
        Err(error) => {
            eprintln!("colossus_lance_build: {error}");
            -1
        }
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn colossus_lance_build_shard(
    parquet: *const c_char,
    directory: *const c_char,
    operator: *const c_char,
    quarter: i32,
    threads: i32,
) -> i32 {
    let result = (|| {
        if threads <= 0 {
            return Err("thread count must be positive".into());
        }
        let parquet = path_from_ptr(parquet)?;
        let directory = path_from_ptr(directory)?;
        let operator = unsafe { CStr::from_ptr(operator) }
            .to_str()
            .map_err(|error| error.to_string())?;
        runtime(threads as usize)?.block_on(build_shard_dataset(
            parquet,
            directory,
            operator,
            quarter,
            threads as usize,
        ))
    })();
    match result {
        Ok(()) => 0,
        Err(error) => {
            eprintln!("colossus_lance_build_shard: {error}");
            -1
        }
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn colossus_lance_create_indexes(
    directory: *const c_char,
    threads: i32,
) -> i32 {
    let result = (|| {
        if threads <= 0 {
            return Err("thread count must be positive".into());
        }
        let directory = path_from_ptr(directory)?;
        runtime(threads as usize)?.block_on(create_indexes(directory))
    })();
    match result {
        Ok(()) => 0,
        Err(error) => {
            eprintln!("colossus_lance_create_indexes: {error}");
            -1
        }
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn colossus_lance_create_shard_index(
    directory: *const c_char,
    threads: i32,
) -> i32 {
    let result = (|| {
        if threads <= 0 {
            return Err("thread count must be positive".into());
        }
        let directory = path_from_ptr(directory)?;
        runtime(threads as usize)?.block_on(create_shard_index(directory))
    })();
    match result {
        Ok(()) => 0,
        Err(error) => {
            eprintln!("colossus_lance_create_shard_index: {error}");
            -1
        }
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn colossus_lance_open(
    directory: *const c_char,
    threads: i32,
) -> *mut LanceEngine {
    let result = (|| {
        if threads <= 0 {
            return Err("thread count must be positive".into());
        }
        LanceEngine::open(path_from_ptr(directory)?, threads as usize)
    })();
    match result {
        Ok(engine) => Box::into_raw(Box::new(engine)),
        Err(error) => {
            eprintln!("colossus_lance_open: {error}");
            std::ptr::null_mut()
        }
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn colossus_lance_query(
    engine: *mut LanceEngine,
    operator: *const c_char,
    quarter: i32,
    low_timestamp: i64,
    high_timestamp: i64,
    explain: i32,
    metrics: *mut LanceQueryMetrics,
) -> i32 {
    if engine.is_null() || metrics.is_null() {
        return -1;
    }
    let operator = if operator.is_null() {
        Ok(None)
    } else {
        unsafe { CStr::from_ptr(operator) }
            .to_str()
            .map(Some)
            .map_err(|error| error.to_string())
    };
    let result = operator.and_then(|operator| {
        unsafe { &*engine }.query(
            operator,
            quarter,
            low_timestamp,
            high_timestamp,
            explain != 0,
            None,
        )
    });
    match result {
        Ok(result) => {
            unsafe { metrics.write(result) };
            0
        }
        Err(error) => {
            eprintln!("colossus_lance_query: {error}");
            -2
        }
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn colossus_lance_query_values(
    engine: *mut LanceEngine,
    operator: *const c_char,
    quarter: i32,
    low_timestamp: i64,
    high_timestamp: i64,
    output: *mut f32,
    output_len: usize,
    metrics: *mut LanceQueryMetrics,
) -> i32 {
    if engine.is_null() || metrics.is_null() || (output.is_null() && output_len != 0) {
        return -1;
    }
    let operator = if operator.is_null() {
        Ok(None)
    } else {
        unsafe { CStr::from_ptr(operator) }
            .to_str()
            .map(Some)
            .map_err(|error| error.to_string())
    };
    let output = if output_len == 0 {
        &mut []
    } else {
        unsafe { std::slice::from_raw_parts_mut(output, output_len) }
    };
    let result = operator.and_then(|operator| {
        unsafe { &*engine }.query(
            operator,
            quarter,
            low_timestamp,
            high_timestamp,
            false,
            Some(output),
        )
    });
    match result {
        Ok(result) => {
            unsafe { metrics.write(result) };
            0
        }
        Err(error) => {
            eprintln!("colossus_lance_query_values: {error}");
            -2
        }
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn colossus_lance_close(engine: *mut LanceEngine) {
    if !engine.is_null() {
        drop(unsafe { Box::from_raw(engine) });
    }
}
