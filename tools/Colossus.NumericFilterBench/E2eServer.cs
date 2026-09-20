using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Colossus.NumericFilterBench;

internal static class E2eServer
{
    public static async Task RunAsync(string directory, int port, bool compress, bool stateless)
    {
        ProcessMetrics.PinToSixCores();
        ThreadPool.SetMinThreads(128, 128);
        var artifact = new MappedArtifact(directory);
        var engine = new ExactQueryEngine(artifact);
        RangeBox startBox = RangeBox.Quantile(artifact.Manifest, 5, 95)
            .WithLower(Fields.Download, 10f, "download-10.00");
        QueryWorkspace? template = null;
        if (!stateless)
        {
            template = new QueryWorkspace(artifact.Manifest.MarkCount);
            engine.ScanState(startBox, template);
        }

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        if (compress)
        {
            builder.Services.AddResponseCompression(options =>
            {
                options.EnableForHttps = false;
                options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(["application/octet-stream"]);
                options.Providers.Add<BrotliCompressionProvider>();
            });
            builder.Services.Configure<BrotliCompressionProviderOptions>(options =>
                options.Level = CompressionLevel.Fastest);
        }

        WebApplication app = builder.Build();
        if (compress)
            app.UseResponseCompression();
        var sessions = new ConcurrentDictionary<int, Session>();
        // Deliberately allow the 64-user saturation run to fill all six physical
        // cores (12 SMT processors). Production admission control should use a
        // lower, latency-driven limit; this endpoint measures the saturation case.
        var gate = new SemaphoreSlim(64, 64);
        int nextSession = 0;
        var metrics = new MetricsWindow();
        CancellationTokenSource? loadCancellation = null;
        Thread[] loadThreads = [];
        long loadRequests = 0;

        app.MapPost("/session", () =>
        {
            int id = Interlocked.Increment(ref nextSession);
            sessions[id] = new Session(
                template?.CloneDirect(),
                stateless ? new StatelessWorkspace(artifact.Manifest.MarkCount) : null,
                startBox);
            return Results.Json(new { id });
        });

        app.MapGet("/delta/{id:int}", async (int id, float lower, HttpContext context) =>
        {
            if (!sessions.TryGetValue(id, out Session? session))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            long queued = Stopwatch.GetTimestamp();
            await gate.WaitAsync(context.RequestAborted);
            double queueMs = Stopwatch.GetElapsedTime(queued).TotalMilliseconds;
            try
            {
                await session.Lock.WaitAsync(context.RequestAborted);
                try
                {
                    RangeBox next = session.Box.WithLower(
                        Fields.Download, lower,
                        $"download-{lower.ToString("F2", CultureInfo.InvariantCulture)}");
                    byte[] buffer;
                    int length;
                    double computeMs;
                    double encodeMs;
                    long changed;
                    uint revision = ++session.Revision;
                    if (stateless)
                    {
                        StatelessDeltaOutcome result = engine.StatelessDeltaFast(
                            session.Box, next, Fields.Download, session.Stateless!, revision);
                        buffer = result.Buffer;
                        length = result.Length;
                        computeMs = result.BoundaryMilliseconds + result.RecomputeMilliseconds;
                        encodeMs = result.EncodeMilliseconds;
                        changed = result.Changed.Rows;
                    }
                    else
                    {
                        var result = engine.DeltaFast(
                            session.Box, next, Fields.Download, session.Stateful!, covering: true);
                        long encodeStart = Stopwatch.GetTimestamp();
                        (buffer, length) = session.Stateful!.EncodeChangedVarint(revision);
                        computeMs = result.Milliseconds;
                        encodeMs = Stopwatch.GetElapsedTime(encodeStart).TotalMilliseconds;
                        changed = result.Changed.Rows;
                    }
                    session.Box = next;

                    context.Response.ContentType = "application/octet-stream";
                    context.Response.Headers["X-Queue-Ms"] = queueMs.ToString("F3", CultureInfo.InvariantCulture);
                    context.Response.Headers["X-Compute-Ms"] = computeMs.ToString("F3", CultureInfo.InvariantCulture);
                    context.Response.Headers["X-Encode-Ms"] = encodeMs.ToString("F3", CultureInfo.InvariantCulture);
                    context.Response.Headers["X-Changed"] = changed.ToString(CultureInfo.InvariantCulture);
                    context.Response.Headers["X-Uncompressed-Bytes"] = length.ToString(CultureInfo.InvariantCulture);
                    await context.Response.Body.WriteAsync(buffer.AsMemory(0, length), context.RequestAborted);
                }
                finally
                {
                    session.Lock.Release();
                }
            }
            finally
            {
                gate.Release();
            }
        });

        app.MapPost("/metrics/reset", () =>
        {
            metrics.Reset();
            return Results.Ok();
        });
        app.MapGet("/metrics", () => Results.Json(metrics.Read()));
        app.MapPost("/load/start", (int users) =>
        {
            if (loadCancellation is not null)
                return Results.Conflict("load already running");
            loadCancellation = new CancellationTokenSource();
            loadRequests = 0;
            var states = Enumerable.Range(0, users)
                .Select(_ => new Session(
                    template?.CloneDirect(),
                    stateless ? new StatelessWorkspace(artifact.Manifest.MarkCount) : null,
                    startBox)).ToArray();
            CancellationToken token = loadCancellation.Token;
            loadThreads = states.Select((state, user) => new Thread(() =>
            {
                float lower = user % 2 == 0 ? 10f : 10.25f;
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        gate.Wait(token);
                        try
                        {
                            float nextLower = lower == 10f ? 10.25f : 10f;
                            RangeBox next = state.Box.WithLower(Fields.Download, nextLower, "background");
                            byte[] payload;
                            int length;
                            uint revision = ++state.Revision;
                            if (stateless)
                            {
                                StatelessDeltaOutcome result = engine.StatelessDeltaFast(
                                    state.Box, next, Fields.Download, state.Stateless!, revision);
                                payload = result.Buffer;
                                length = result.Length;
                            }
                            else
                            {
                                engine.DeltaFast(state.Box, next, Fields.Download, state.Stateful!, covering: true);
                                (payload, length) = state.Stateful!.EncodeChangedVarint(revision);
                            }
                            state.Box = next;
                            lower = nextLower;
                            if (compress)
                            {
                                byte[] compressed = GC.AllocateUninitializedArray<byte>(BrotliEncoder.GetMaxCompressedLength(length));
                                BrotliEncoder.TryCompress(
                                    payload.AsSpan(0, length), compressed, out _, quality: 1, window: 22);
                                GC.KeepAlive(compressed);
                            }
                            GC.KeepAlive(payload);
                            Interlocked.Increment(ref loadRequests);
                        }
                        finally
                        {
                            gate.Release();
                        }
                    }
                }
                catch (OperationCanceledException) { }
            }) { IsBackground = true, Name = $"e2e-load-{user}" }).ToArray();
            foreach (Thread thread in loadThreads) thread.Start();
            return Results.Json(new { users });
        });
        app.MapPost("/load/stop", () =>
        {
            if (loadCancellation is null)
                return Results.Json(new { requests = loadRequests });
            loadCancellation.Cancel();
            foreach (Thread thread in loadThreads) thread.Join();
            loadCancellation.Dispose();
            loadCancellation = null;
            loadThreads = [];
            return Results.Json(new { requests = loadRequests });
        });
        app.MapGet("/", () => Results.Content(Html(artifact.Manifest.MarkCount), "text/html"));
        app.Lifetime.ApplicationStopping.Register(artifact.Dispose);

        Console.WriteLine($"e2eServer=http://127.0.0.1:{port} responseMode={(compress ? "brotli" : "plain")} engineMode={(stateless ? "stateless" : "stateful")} facts={artifact.Manifest.FactCount} marks={artifact.Manifest.MarkCount}");
        await app.RunAsync();
    }

    private sealed class Session(
        QueryWorkspace? stateful, StatelessWorkspace? stateless, RangeBox box)
    {
        public QueryWorkspace? Stateful { get; } = stateful;
        public StatelessWorkspace? Stateless { get; } = stateless;
        public RangeBox Box { get; set; } = box;
        public SemaphoreSlim Lock { get; } = new(1, 1);
        public uint Revision { get; set; }
    }

    private sealed class MetricsWindow
    {
        private TimeSpan _cpu;
        private long _workingSet;
        private long _timestamp;

        public MetricsWindow() => Reset();

        public void Reset()
        {
            var process = Process.GetCurrentProcess();
            process.Refresh();
            _cpu = process.TotalProcessorTime;
            _workingSet = process.WorkingSet64;
            _timestamp = Stopwatch.GetTimestamp();
        }

        public object Read()
        {
            var process = Process.GetCurrentProcess();
            process.Refresh();
            double seconds = Stopwatch.GetElapsedTime(_timestamp).TotalSeconds;
            double cpu = (process.TotalProcessorTime - _cpu).TotalSeconds;
            return new
            {
                elapsedSeconds = seconds,
                cpuSeconds = cpu,
                effectiveCores = cpu / seconds,
                workingSetMiB = process.WorkingSet64 / 1048576.0,
                workingSetDeltaMiB = (process.WorkingSet64 - _workingSet) / 1048576.0,
                peakWorkingSetMiB = process.PeakWorkingSet64 / 1048576.0,
                sessions = 0
            };
        }
    }

    private static string Html(int marks) => $$"""
        <!doctype html>
        <meta charset="utf-8">
        <title>Colossus numeric E2E benchmark</title>
        <style>html,body,canvas{margin:0;width:100%;height:100%;display:block;background:#05070b}</style>
        <canvas id="map" width="1280" height="720"></canvas>
        <script>
        const MARKS = {{marks}};
        const values = new Float32Array(MARKS);
        const canvas = document.getElementById('map');
        const gl = typeof canvas.getContext === 'function'
          ? canvas.getContext('webgl2', {antialias:false, depth:false, preserveDrawingBuffer:false})
          : null;
        let valueBuffer = null;
        if (gl) {
          const rendererInfo = gl.getExtension('WEBGL_debug_renderer_info');
          window.renderer = rendererInfo
            ? gl.getParameter(rendererInfo.UNMASKED_RENDERER_WEBGL)
            : gl.getParameter(gl.RENDERER);
          const shader = (type, source) => {
            const s = gl.createShader(type); gl.shaderSource(s, source); gl.compileShader(s);
            if (!gl.getShaderParameter(s, gl.COMPILE_STATUS)) throw new Error(gl.getShaderInfoLog(s));
            return s;
          };
          const program = gl.createProgram();
          gl.attachShader(program, shader(gl.VERTEX_SHADER, `#version 300 es
            layout(location=0) in float value;
            out float shade;
            void main(){
              int ix = gl_VertexID & 2047;
              int iy = gl_VertexID >> 11;
              gl_Position = vec4(float(ix)/1024.0-1.0, float(iy)/512.0-1.0, 0.0, 1.0);
              gl_PointSize = 1.0; shade = fract(value * 0.013);
            }`));
          gl.attachShader(program, shader(gl.FRAGMENT_SHADER, `#version 300 es
            precision highp float; in float shade; out vec4 color;
            void main(){ color=vec4(shade,0.35,1.0-shade,1.0); }`));
          gl.linkProgram(program); if (!gl.getProgramParameter(program, gl.LINK_STATUS)) throw new Error(gl.getProgramInfoLog(program));
          gl.useProgram(program);
          valueBuffer = gl.createBuffer(); gl.bindBuffer(gl.ARRAY_BUFFER, valueBuffer);
          gl.bufferData(gl.ARRAY_BUFFER, values, gl.DYNAMIC_DRAW);
          gl.enableVertexAttribArray(0); gl.vertexAttribPointer(0,1,gl.FLOAT,false,0,0);
        } else {
          window.renderer = 'WebGL2 unavailable';
        }
        const frame = () => new Promise(resolve => requestAnimationFrame(() => resolve()));

        function decode(buffer) {
          const started = performance.now();
          const view = new DataView(buffer); const count = view.getUint32(4,true);
          let offset=8, mark=0;
          for(let i=0;i<count;i++){
            let delta=0, shift=0, byte;
            do { byte=view.getUint8(offset++); delta |= (byte & 127) << shift; shift += 7; } while(byte & 128);
            mark += delta; values[mark] = view.getFloat32(offset,true); offset += 4;
          }
          return {count, ms:performance.now()-started};
        }
        async function createSession(){ return (await (await fetch('/session',{method:'POST'})).json()).id; }
        async function step(id, lower, sequence){
          const url=`/delta/${id}?lower=${lower}&sequence=${sequence}`;
          const start=performance.now(); const response=await fetch(url); const headers=performance.now();
          const body=await response.arrayBuffer(); const bodyDone=performance.now();
          const decoded=decode(body); const decodedAt=performance.now();
          if (gl) {
            gl.bindBuffer(gl.ARRAY_BUFFER,valueBuffer); gl.bufferSubData(gl.ARRAY_BUFFER,0,values);
            gl.drawArrays(gl.POINTS,0,MARKS); gl.finish();
          }
          const gpu=performance.now();
          await frame(); const painted=performance.now();
          const resource=performance.getEntriesByName(new URL(url,location.href).href).at(-1);
          return {
            total:painted-start, headers:headers-start, body:bodyDone-headers,
            decode:decoded.ms, gpu:gpu-decodedAt, paint:painted-gpu, changed:decoded.count,
            queue:+response.headers.get('x-queue-ms'), compute:+response.headers.get('x-compute-ms'),
            encode:+response.headers.get('x-encode-ms'), uncompressed:+response.headers.get('x-uncompressed-bytes'),
            transfer:resource?.encodedBodySize || 0
          };
        }
        window.createSession=createSession;
        window.run=async(id, iterations, warmup=5)=>{
          let sequence=0, lower=10;
          for(let i=0;i<warmup;i++){ lower=lower===10?10.25:10; await step(id,lower,sequence++); }
          const output=[];
          for(let i=0;i<iterations;i++){ lower=lower===10?10.25:10; output.push(await step(id,lower,sequence++)); }
          return output;
        };
        </script>
        """;
}
