using System.Diagnostics;
using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Colossus.NumericFilterBench;

internal static class TimestampTreeE2eServer
{
    public static async Task RunAsync(string directory, int port, bool directIo)
    {
        ProcessMetrics.PinToSixCores();
        ThreadPool.SetMinThreads(64, 64);
        TimestampTreeManifest manifest = TimestampTreeManifest.Read(directory);
        (long start, long end) = WorstRange(manifest.DayCount);
        var gate = new SemaphoreSlim(6, 6);

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
                using var artifact = new TimestampTreeArtifact(directory, directIo);
                var engine = new TimestampTreeEngine(artifact);
                TimestampPayloadResult result = engine.AbsoluteDisplayPayload(start, end);
                TimestampQueryOutcome outcome = result.Outcome;
                context.Response.ContentType = "application/octet-stream";
                context.Response.ContentLength = result.Payload.Length;
                context.Response.Headers["X-Queue-Ms"] = queueMs.ToString("F3", CultureInfo.InvariantCulture);
                context.Response.Headers["X-Backend-Ms"] = outcome.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture);
                context.Response.Headers["X-Merge-Ms"] = outcome.ReadAndMergeMilliseconds.ToString("F3", CultureInfo.InvariantCulture);
                context.Response.Headers["X-Encode-Ms"] = outcome.EncodeMilliseconds.ToString("F3", CultureInfo.InvariantCulture);
                context.Response.Headers["X-Logical-Bytes"] = outcome.LogicalBytes.ToString(CultureInfo.InvariantCulture);
                context.Response.Headers["X-Tree-Nodes"] = outcome.TreeNodes.ToString(CultureInfo.InvariantCulture);
                context.Response.Headers["X-Boundary-Facts"] = outcome.BoundaryFacts.ToString(CultureInfo.InvariantCulture);
                await context.Response.Body.WriteAsync(result.Payload, context.RequestAborted);
            }
            finally
            {
                gate.Release();
            }
        });
        app.MapGet("/", () => Results.Content(Html(manifest.MarkCount), "text/html"));

        Console.WriteLine(
            $"timestampE2eServer=http://127.0.0.1:{port} ioMode={(directIo ? "direct" : "buffered")} " +
            $"facts={manifest.FactCount} marks={manifest.MarkCount} displayPayloadMiB={manifest.MarkCount * 4L / 1048576.0:F2}");
        await app.RunAsync();
    }

    private static (long Start, long End) WorstRange(int days)
    {
        (int Start, int End, int Nodes) best = default;
        for (int start = 1; start < days - 1; start++)
        {
            for (int end = start + 1; end < days; end++)
            {
                int nodes = NodeCount(days, start, end);
                if (nodes > best.Nodes ||
                    (nodes == best.Nodes && end - start > best.End - best.Start))
                    best = (start, end, nodes);
            }
        }
        return (
            (long)(best.Start - 1) * 86_400L + 1,
            (long)best.End * 86_400L + 86_399L);
    }

    private static int NodeCount(int days, int start, int end)
    {
        int count = 0;
        int left = start + days;
        int right = end + days;
        while (left < right)
        {
            if ((left & 1) != 0) { count++; left++; }
            if ((right & 1) != 0) { right--; count++; }
            left >>= 1;
            right >>= 1;
        }
        return count;
    }

    private static string Html(int marks) => $$"""
        <!doctype html>
        <meta charset="utf-8">
        <title>Colossus exact timestamp E2E benchmark</title>
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
}
