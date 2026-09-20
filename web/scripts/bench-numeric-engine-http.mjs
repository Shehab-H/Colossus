import http from 'node:http';
import { brotliDecompressSync } from 'node:zlib';
import { performance } from 'node:perf_hooks';

const base = new URL(process.argv[2] ?? 'http://127.0.0.1:5099');
const backgroundUsers = Number(process.argv[3] ?? 32);
const iterations = Number(process.argv[4] ?? 100);

function request(path, method = 'GET') {
  return new Promise((resolve, reject) => {
    const started = performance.now();
    const req = http.request(new URL(path, base), {
      method,
      headers: { 'accept-encoding': 'br' }
    }, response => {
      const headersAt = performance.now();
      const chunks = [];
      let wireBytes = 0;
      response.on('data', chunk => { chunks.push(chunk); wireBytes += chunk.length; });
      response.on('end', () => {
        const bodyAt = performance.now();
        try {
          const wire = Buffer.concat(chunks);
          const decompressStarted = performance.now();
          const body = response.headers['content-encoding'] === 'br'
            ? brotliDecompressSync(wire)
            : wire;
          const decompressedAt = performance.now();
          resolve({
            body,
            wireBytes,
            headers: response.headers,
            status: response.statusCode,
            totalMs: decompressedAt - started,
            headersMs: headersAt - started,
            bodyMs: bodyAt - headersAt,
            decompressMs: decompressedAt - decompressStarted
          });
        } catch (error) { reject(error); }
      });
    });
    req.on('error', reject);
    req.end();
  });
}

function decodeDelta(buffer, values) {
  const started = performance.now();
  const count = buffer.readUInt32LE(4);
  let offset = 8;
  let mark = 0;
  for (let i = 0; i < count; i++) {
    let delta = 0;
    let shift = 0;
    let byte;
    do {
      byte = buffer[offset++];
      delta |= (byte & 127) << shift;
      shift += 7;
    } while (byte & 128);
    mark += delta;
    values[mark] = buffer.readFloatLE(offset);
    offset += 4;
  }
  return { count, milliseconds: performance.now() - started };
}

function percentile(rows, name, p) {
  const values = rows.map(row => row[name]).sort((a, b) => a - b);
  return values[Math.max(0, Math.ceil(values.length * p) - 1)];
}

const sessionResponse = await request('/session', 'POST');
const session = JSON.parse(sessionResponse.body.toString('utf8')).id;
await request(`/load/start?users=${backgroundUsers}`, 'POST');
await request('/metrics/reset', 'POST');

const values = new Float32Array(2_008_081);
const rows = [];
let lower = 10;
for (let i = 0; i < iterations + 8; i++) {
  lower = lower === 10 ? 10.25 : 10;
  const response = await request(`/delta/${session}?lower=${lower}&sequence=${i}`);
  if (response.status !== 200) throw new Error(`delta failed: HTTP ${response.status}`);
  const decoded = decodeDelta(response.body, values);
  if (i >= 8) {
    rows.push({
      totalMs: response.totalMs + decoded.milliseconds,
      headersMs: response.headersMs,
      bodyMs: response.bodyMs,
      decompressMs: response.decompressMs,
      decodeMs: decoded.milliseconds,
      wireBytes: response.wireBytes,
      uncompressedBytes: response.body.length,
      changed: decoded.count,
      queueMs: Number(response.headers['x-queue-ms']),
      computeMs: Number(response.headers['x-compute-ms']),
      encodeMs: Number(response.headers['x-encode-ms'])
    });
  }
}

const load = JSON.parse((await request('/load/stop', 'POST')).body.toString('utf8'));
const server = JSON.parse((await request('/metrics')).body.toString('utf8'));
const summary = {
  backgroundUsers,
  backgroundRequests: load.requests,
  iterations,
  server,
  contentEncoding: rows[0]?.wireBytes === rows[0]?.uncompressedBytes ? 'plain' : 'br',
  changed: rows.reduce((sum, row) => sum + row.changed, 0) / rows.length
};
for (const name of [
  'totalMs', 'headersMs', 'bodyMs', 'decompressMs', 'decodeMs',
  'queueMs', 'computeMs', 'encodeMs', 'wireBytes', 'uncompressedBytes'
]) {
  summary[name] = {
    p50: percentile(rows, name, .50),
    p95: percentile(rows, name, .95),
    p99: percentile(rows, name, .99)
  };
}
console.log(JSON.stringify(summary, null, 2));
