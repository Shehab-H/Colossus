import * as duckdb from '@duckdb/duckdb-wasm';
import mvpWasm from '@duckdb/duckdb-wasm/dist/duckdb-mvp.wasm?url';
import mvpWorker from '@duckdb/duckdb-wasm/dist/duckdb-browser-mvp.worker.js?url';
import ehWasm from '@duckdb/duckdb-wasm/dist/duckdb-eh.wasm?url';
import ehWorker from '@duckdb/duckdb-wasm/dist/duckdb-browser-eh.worker.js?url';
import type { Table } from 'apache-arrow';

// A single browser-side DuckDB, instantiated from locally-bundled wasm/workers (no CDN — on-prem).
// This is the query engine of R4: tiles are Parquet, and the client filters/projects them here,
// handing an Arrow result straight to the GPU layers.

let dbPromise: Promise<duckdb.AsyncDuckDB> | null = null;

async function open(): Promise<duckdb.AsyncDuckDB> {
  const bundle = await duckdb.selectBundle({
    mvp: { mainModule: mvpWasm, mainWorker: mvpWorker },
    eh: { mainModule: ehWasm, mainWorker: ehWorker },
  });
  const worker = new Worker(bundle.mainWorker!);
  const db = new duckdb.AsyncDuckDB(new duckdb.ConsoleLogger(duckdb.LogLevel.WARNING), worker);
  await db.instantiate(bundle.mainModule, bundle.pthreadWorker);
  return db;
}

export function duck(): Promise<duckdb.AsyncDuckDB> {
  return (dbPromise ??= open());
}

let seq = 0;

/**
 * Fetch a Parquet tile, register it under a unique name (so parallel tile queries don't collide),
 * run one query built from the table expression, and return the Arrow result.
 */
export async function queryParquet(url: string, sql: (table: string) => string): Promise<Table> {
  const db = await duck();
  const buf = new Uint8Array(await (await fetch(url)).arrayBuffer());
  const name = `t${seq++}.parquet`;
  await db.registerFileBuffer(name, buf);
  const conn = await db.connect();
  try {
    // duckdb-wasm bundles its own apache-arrow; bridge to the app's copy (identical runtime API).
    return (await conn.query(sql(`read_parquet('${name}')`))) as unknown as Table;
  } finally {
    await conn.close();
    await db.dropFile(name);
  }
}
