use std::ffi::{CStr, c_char};
use std::fs::File;
use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::time::Instant;

use bytes::Bytes;
use datafusion::arrow::array::{Array, ArrayRef, Float32Array, UInt32Array};
use datafusion::arrow::buffer::{Buffer, ScalarBuffer};
use datafusion::arrow::datatypes::{DataType, Field, Schema, SchemaRef};
use datafusion::arrow::record_batch::RecordBatch;
use datafusion::datasource::MemTable;
use datafusion::execution::context::{SessionConfig, SessionContext};
use memmap2::MmapOptions;
use tokio::runtime::{Builder, Runtime};

mod lance_bench;

const DIMENSIONS: [&str; 4] = [
    "sample_share",
    "download_mbps",
    "upload_mbps",
    "latency_ms",
];

#[repr(C)]
pub struct NativeQueryMetrics {
    pub engine_ms: f64,
    pub rows: u64,
    pub checksum: u64,
    pub records_visited: u64,
}

struct ArrowTable {
    schema: SchemaRef,
    arrays: Vec<ArrayRef>,
    len: usize,
}

pub struct Engine {
    runtime: Runtime,
    threads: usize,
    facts: ArrowTable,
    covers: Vec<ArrowTable>,
}

impl ArrowTable {
    fn facts(directory: &Path) -> Result<Self, String> {
        let arrays: Vec<ArrayRef> = vec![
            Arc::new(map_u32(directory.join("fact-mark.u32"))?),
            Arc::new(map_f32(directory.join("fact-tests.f32"))?),
            Arc::new(map_f32(directory.join("fact-sample_share.f32"))?),
            Arc::new(map_f32(directory.join("fact-download_mbps.f32"))?),
            Arc::new(map_f32(directory.join("fact-upload_mbps.f32"))?),
            Arc::new(map_f32(directory.join("fact-latency_ms.f32"))?),
        ];
        Self::new(arrays)
    }

    fn cover(directory: &Path, ordered_dimension: &str) -> Result<Self, String> {
        let arrays: Vec<ArrayRef> = vec![
            Arc::new(map_u32(directory.join(format!("cover-{ordered_dimension}-mark.u32")))?),
            Arc::new(map_f32(directory.join(format!("cover-{ordered_dimension}-tests.f32")))?),
            Arc::new(map_f32(directory.join(format!("cover-{ordered_dimension}-sample_share.f32")))?),
            Arc::new(map_f32(directory.join(format!("cover-{ordered_dimension}-download_mbps.f32")))?),
            Arc::new(map_f32(directory.join(format!("cover-{ordered_dimension}-upload_mbps.f32")))?),
            Arc::new(map_f32(directory.join(format!("cover-{ordered_dimension}-latency_ms.f32")))?),
        ];
        Self::new(arrays)
    }

    fn new(arrays: Vec<ArrayRef>) -> Result<Self, String> {
        let len = arrays.first().ok_or("table has no columns")?.len();
        if arrays.iter().any(|array| array.len() != len) {
            return Err("column lengths differ".into());
        }
        let schema = Arc::new(Schema::new(vec![
            Field::new("mark_id", DataType::UInt32, false),
            Field::new("tests", DataType::Float32, false),
            Field::new("sample_share", DataType::Float32, false),
            Field::new("download_mbps", DataType::Float32, false),
            Field::new("upload_mbps", DataType::Float32, false),
            Field::new("latency_ms", DataType::Float32, false),
        ]));
        Ok(Self { schema, arrays, len })
    }

    fn provider(&self, start: usize, end: usize, partitions: usize) -> Result<MemTable, String> {
        if start > end || end > self.len {
            return Err(format!("invalid table slice {start}..{end} of {}", self.len));
        }
        let len = end - start;
        let partition_count = partitions.max(1).min(len.max(1));
        let partition_len = len.div_ceil(partition_count);
        let mut batches = Vec::with_capacity(partition_count);
        for partition in 0..partition_count {
            let offset = start + partition * partition_len;
            if offset >= end {
                break;
            }
            let batch_len = partition_len.min(end - offset);
            let arrays = self
                .arrays
                .iter()
                .map(|array| array.slice(offset, batch_len))
                .collect::<Vec<_>>();
            let batch = RecordBatch::try_new(Arc::clone(&self.schema), arrays)
                .map_err(|error| error.to_string())?;
            batches.push(vec![batch]);
        }
        MemTable::try_new(Arc::clone(&self.schema), batches).map_err(|error| error.to_string())
    }

    fn dimension(&self, dimension: usize) -> Result<&Float32Array, String> {
        self.arrays[dimension + 2]
            .as_any()
            .downcast_ref::<Float32Array>()
            .ok_or_else(|| "numeric column is not Float32".to_string())
    }
}

impl Engine {
    fn open(directory: &Path, threads: usize) -> Result<Self, String> {
        let runtime = Builder::new_multi_thread()
            .worker_threads(threads)
            .enable_all()
            .build()
            .map_err(|error| error.to_string())?;
        let facts = ArrowTable::facts(directory)?;
        let covers = DIMENSIONS
            .iter()
            .map(|dimension| ArrowTable::cover(directory, dimension))
            .collect::<Result<Vec<_>, _>>()?;
        Ok(Self { runtime, threads, facts, covers })
    }

    fn query(&self, mode: i32, low: &[f32; 4], high: &[f32; 4]) -> Result<NativeQueryMetrics, String> {
        let started = Instant::now();
        let (table, start, end) = if mode == 0 {
            (&self.facts, 0, self.facts.len)
        } else if mode == 1 {
            let mut driver = 0;
            let mut best = usize::MAX;
            let mut best_span = (0, 0);
            for dimension in 0..DIMENSIONS.len() {
                let values = self.covers[dimension].dimension(dimension)?;
                let span = span(values, low[dimension], high[dimension]);
                let count = span.1 - span.0;
                if count < best {
                    best = count;
                    driver = dimension;
                    best_span = span;
                }
            }
            (&self.covers[driver], best_span.0, best_span.1)
        } else {
            return Err(format!("unknown DataFusion mode {mode}"));
        };

        let provider = table.provider(start, end, self.threads)?;
        let config = SessionConfig::new()
            .with_target_partitions(self.threads)
            .with_batch_size(65_536);
        let context = SessionContext::new_with_config(config);
        context
            .register_table("facts", Arc::new(provider))
            .map_err(|error| error.to_string())?;
        let predicate = DIMENSIONS
            .iter()
            .enumerate()
            .map(|(index, name)| {
                format!(
                    "{name} BETWEEN {} AND {}",
                    sql_float(low[index]),
                    sql_float(high[index])
                )
            })
            .collect::<Vec<_>>()
            .join(" AND ");
        let sql = format!(
            "SELECT mark_id, \
             CAST(SUM(CAST(download_mbps * tests AS DOUBLE)) / \
                  SUM(CAST(tests AS DOUBLE)) AS REAL) AS value \
             FROM facts WHERE {predicate} GROUP BY mark_id"
        );

        let batches = self.runtime.block_on(async {
            let frame = context.sql(&sql).await.map_err(|error| error.to_string())?;
            frame.collect().await.map_err(|error| error.to_string())
        })?;
        let mut rows = 0u64;
        let mut checksum = 0u64;
        for batch in batches {
            let marks = batch
                .column(0)
                .as_any()
                .downcast_ref::<UInt32Array>()
                .ok_or("DataFusion mark result is not UInt32")?;
            let values = batch
                .column(1)
                .as_any()
                .downcast_ref::<Float32Array>()
                .ok_or("DataFusion value result is not Float32")?;
            for row in 0..batch.num_rows() {
                checksum ^= mark_value_hash(marks.value(row), values.value(row));
            }
            rows += batch.num_rows() as u64;
        }
        Ok(NativeQueryMetrics {
            engine_ms: started.elapsed().as_secs_f64() * 1000.0,
            rows,
            checksum,
            records_visited: (end - start) as u64,
        })
    }
}

fn map_f32(path: PathBuf) -> Result<Float32Array, String> {
    let bytes = map_bytes(&path)?;
    if bytes.len() % size_of::<f32>() != 0 {
        return Err(format!("misaligned Float32 plane: {}", path.display()));
    }
    let len = bytes.len() / size_of::<f32>();
    let buffer = Buffer::from(bytes);
    Ok(Float32Array::new(ScalarBuffer::new(buffer, 0, len), None))
}

fn map_u32(path: PathBuf) -> Result<UInt32Array, String> {
    let bytes = map_bytes(&path)?;
    if bytes.len() % size_of::<u32>() != 0 {
        return Err(format!("misaligned UInt32 plane: {}", path.display()));
    }
    let len = bytes.len() / size_of::<u32>();
    let buffer = Buffer::from(bytes);
    Ok(UInt32Array::new(ScalarBuffer::new(buffer, 0, len), None))
}

fn map_bytes(path: &Path) -> Result<Bytes, String> {
    let file = File::open(path).map_err(|error| format!("{}: {error}", path.display()))?;
    let mmap = unsafe { MmapOptions::new().map(&file) }
        .map_err(|error| format!("{}: {error}", path.display()))?;
    Ok(Bytes::from_owner(mmap))
}

fn span(values: &Float32Array, low: f32, high: f32) -> (usize, usize) {
    (lower_bound(values, low, false), lower_bound(values, high, true))
}

fn lower_bound(values: &Float32Array, value: f32, strict: bool) -> usize {
    let key = sortable(value);
    let mut low = 0;
    let mut high = values.len();
    while low < high {
        let middle = low + ((high - low) >> 1);
        let current = sortable(values.value(middle));
        if current < key || (strict && current == key) {
            low = middle + 1;
        } else {
            high = middle;
        }
    }
    low
}

fn sortable(value: f32) -> u32 {
    let bits = value.to_bits();
    if bits & 0x8000_0000 != 0 { !bits } else { bits ^ 0x8000_0000 }
}

fn sql_float(value: f32) -> String {
    if value.is_finite() {
        format!("CAST({value:?} AS REAL)")
    } else {
        "CAST(NULL AS REAL)".into()
    }
}

fn mark_value_hash(mark: u32, value: f32) -> u64 {
    let mut z = ((mark as u64) << 32) | value.to_bits() as u64;
    z = z.wrapping_add(0x9E3779B97F4A7C15);
    z = (z ^ (z >> 30)).wrapping_mul(0xBF58476D1CE4E5B9);
    z = (z ^ (z >> 27)).wrapping_mul(0x94D049BB133111EB);
    z ^ (z >> 31)
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn colossus_df_open(directory: *const c_char, threads: i32) -> *mut Engine {
    if directory.is_null() || threads <= 0 {
        return std::ptr::null_mut();
    }
    let result = (|| {
        let path = unsafe { CStr::from_ptr(directory) }
            .to_str()
            .map_err(|error| error.to_string())?;
        Engine::open(Path::new(path), threads as usize)
    })();
    match result {
        Ok(engine) => Box::into_raw(Box::new(engine)),
        Err(error) => {
            eprintln!("colossus_df_open: {error}");
            std::ptr::null_mut()
        }
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn colossus_df_query(
    engine: *mut Engine,
    mode: i32,
    low: *const f32,
    high: *const f32,
    metrics: *mut NativeQueryMetrics,
) -> i32 {
    if engine.is_null() || low.is_null() || high.is_null() || metrics.is_null() {
        return -1;
    }
    let low = unsafe { &*(low as *const [f32; 4]) };
    let high = unsafe { &*(high as *const [f32; 4]) };
    match unsafe { &*engine }.query(mode, low, high) {
        Ok(result) => {
            unsafe { metrics.write(result) };
            0
        }
        Err(error) => {
            eprintln!("colossus_df_query: {error}");
            -2
        }
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn colossus_df_close(engine: *mut Engine) {
    if !engine.is_null() {
        drop(unsafe { Box::from_raw(engine) });
    }
}
