using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Colossus.NumericFilterBench;

internal static class LanceE2eServer
{
    private const long QuarterSeconds = 7_889_400;

    public static async Task RunAsync(
        string directory, int quarter, int port, int threads, int marks, int maxActive)
    {
        ProcessMetrics.PinToSixCores();
        ThreadPool.SetMinThreads(64, 64);
        nint library = NativeLibrary.Load(LibraryPath());
        var open = Load<OpenDelegate>(library, "colossus_lance_open");
        var query = Load<QueryValuesDelegate>(library, "colossus_lance_query_values");
        var close = Load<CloseDelegate>(library, "colossus_lance_close");
        nint directoryUtf8 = Marshal.StringToCoTaskMemUTF8(directory);
        nint engine = open(directoryUtf8, threads);
        if (engine == 0)
            throw new InvalidOperationException("Lance + DataFusion failed to open the E2E shard");

        long quarterLow = quarter * QuarterSeconds;
        long quarterHigh = checked((quarter + 1L) * QuarterSeconds - 1);
        long low = quarterLow + 123;
        long high = quarterHigh - 456;
        var gate = new SemaphoreSlim(maxActive, maxActive);

        try
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.SetMinimumLevel(LogLevel.Warning);
            builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
            WebApplication app = builder.Build();

            app.MapGet("/query", async (HttpContext context) =>
            {
                long queued = Stopwatch.GetTimestamp();
                await gate.WaitAsync(context.RequestAborted);
                double queueMs = Stopwatch.GetElapsedTime(queued).TotalMilliseconds;
                try
                {
                    byte[] payload = GC.AllocateUninitializedArray<byte>(checked(marks * sizeof(float)));
                    var backend = Stopwatch.StartNew();
                    int status = InvokeQuery(
                        query, engine, low, high, payload, marks, out NativeMetrics metrics);
                    backend.Stop();
                    if (status != 0)
                        throw new InvalidOperationException($"Lance E2E query failed with native status {status}");

                    context.Response.ContentType = "application/octet-stream";
                    context.Response.ContentLength = payload.Length;
                    context.Response.Headers["X-Queue-Ms"] = queueMs.ToString("F3", CultureInfo.InvariantCulture);
                    context.Response.Headers["X-Backend-Ms"] = backend.Elapsed.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture);
                    context.Response.Headers["X-Merge-Ms"] = metrics.EngineMilliseconds.ToString("F3", CultureInfo.InvariantCulture);
                    context.Response.Headers["X-Encode-Ms"] = "0";
                    context.Response.Headers["X-Rows"] = metrics.Rows.ToString(CultureInfo.InvariantCulture);
                    context.Response.Headers["X-Rows-Scanned"] = metrics.RowsScanned.ToString(CultureInfo.InvariantCulture);
                    context.Response.Headers["X-Lance-Read-Bytes"] = metrics.BytesRead.ToString(CultureInfo.InvariantCulture);
                    await context.Response.Body.WriteAsync(payload, context.RequestAborted);
                }
                finally
                {
                    gate.Release();
                }
            });
            app.MapGet("/", () => Results.Content(Html(marks), "text/html"));

            Console.WriteLine(
                $"lanceE2eServer=http://127.0.0.1:{port} threads={threads} maxActive={maxActive} " +
                $"marks={marks} payloadMiB={marks * 4L / 1048576.0:F2}");
            await app.RunAsync();
        }
        finally
        {
            close(engine);
            Marshal.FreeCoTaskMem(directoryUtf8);
            NativeLibrary.Free(library);
            gate.Dispose();
        }
    }

    private static string LibraryPath() => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "native", "target", "release",
        OperatingSystem.IsWindows() ? "colossus_datafusion_bench.dll" : "libcolossus_datafusion_bench.so"));

    private static T Load<T>(nint library, string name) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));

    private static unsafe int InvokeQuery(
        QueryValuesDelegate query, nint engine, long low, long high,
        byte[] payload, int marks, out NativeMetrics metrics)
    {
        NativeMetrics local = default;
        int status;
        fixed (byte* output = payload)
        {
            status = query(
                engine, 0, -1, low, high, (nint)output, (nuint)marks, (nint)(&local));
        }
        metrics = local;
        return status;
    }

    private static string Html(int marks) => $$"""
        <!doctype html>
        <meta charset="utf-8">
        <title>Colossus Lance DataFusion E2E benchmark</title>
        <style>html,body,canvas{margin:0;width:100%;height:100%;display:block;background:#05070b}</style>
        <canvas id="map" width="1280" height="720"></canvas>
        <script>
        const MARKS={{marks}};
        const values=new Float32Array(MARKS);
        const canvas=document.getElementById('map');
        const gl=canvas.getContext('webgl2',{antialias:false,depth:false,preserveDrawingBuffer:false});
        let valueBuffer=null;
        if(gl){
          const rendererInfo=gl.getExtension('WEBGL_debug_renderer_info');
          window.renderer=rendererInfo?gl.getParameter(rendererInfo.UNMASKED_RENDERER_WEBGL):gl.getParameter(gl.RENDERER);
          const shader=(type,source)=>{const s=gl.createShader(type);gl.shaderSource(s,source);gl.compileShader(s);if(!gl.getShaderParameter(s,gl.COMPILE_STATUS))throw new Error(gl.getShaderInfoLog(s));return s;};
          const program=gl.createProgram();
          gl.attachShader(program,shader(gl.VERTEX_SHADER,`#version 300 es
            layout(location=0) in float value; out float shade;
            void main(){int ix=gl_VertexID&2047;int iy=gl_VertexID>>11;gl_Position=vec4(float(ix)/1024.0-1.0,float(iy)/512.0-1.0,0.0,1.0);gl_PointSize=1.0;shade=fract(value*0.013);}`));
          gl.attachShader(program,shader(gl.FRAGMENT_SHADER,`#version 300 es
            precision highp float;in float shade;out vec4 color;void main(){color=vec4(shade,0.35,1.0-shade,1.0);}`));
          gl.linkProgram(program);if(!gl.getProgramParameter(program,gl.LINK_STATUS))throw new Error(gl.getProgramInfoLog(program));
          gl.useProgram(program);valueBuffer=gl.createBuffer();gl.bindBuffer(gl.ARRAY_BUFFER,valueBuffer);gl.bufferData(gl.ARRAY_BUFFER,values,gl.DYNAMIC_DRAW);gl.enableVertexAttribArray(0);gl.vertexAttribPointer(0,1,gl.FLOAT,false,0,0);
        }else{window.renderer='WebGL2 unavailable';}
        const frame=()=>new Promise(resolve=>requestAnimationFrame(resolve));
        window.step=async()=>{
          const start=performance.now();const response=await fetch('/query');const headers=performance.now();
          const body=await response.arrayBuffer();const bodyDone=performance.now();
          const decodeStart=performance.now();values.set(new Float32Array(body));const decoded=performance.now();
          if(gl){gl.bindBuffer(gl.ARRAY_BUFFER,valueBuffer);gl.bufferSubData(gl.ARRAY_BUFFER,0,values);gl.drawArrays(gl.POINTS,0,MARKS);gl.finish();}
          const gpu=performance.now();await frame();const painted=performance.now();
          return {total:painted-start,headers:headers-start,body:bodyDone-headers,decode:decoded-decodeStart,gpu:gpu-decoded,paint:painted-gpu,bytes:body.byteLength,queue:+response.headers.get('x-queue-ms'),backend:+response.headers.get('x-backend-ms'),merge:+response.headers.get('x-merge-ms'),encode:+response.headers.get('x-encode-ms')};
        };
        window.run=async iterations=>{const rows=[];for(let i=0;i<iterations;i++)rows.push(await window.step());return rows;};
        </script>
        """;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMetrics
    {
        public double EngineMilliseconds;
        public ulong Rows;
        public ulong Checksum;
        public ulong RowsScanned;
        public ulong FragmentsScanned;
        public ulong RangesScanned;
        public ulong BytesRead;
        public ulong IndicesLoaded;
        public ulong IndexPartsLoaded;
        public ulong IndexComparisons;
        public ulong IndexCacheHits;
        public ulong IndexCacheMisses;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint OpenDelegate(nint directory, int threads);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int QueryValuesDelegate(
        nint engine, nint operatorValue, int quarter, long lowTimestamp, long highTimestamp,
        nint output, nuint outputLength, nint metrics);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void CloseDelegate(nint engine);
}
