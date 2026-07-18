using Colossus.Infrastructure.Tiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Net.Http.Headers;

namespace Colossus.Server;

/// <summary>Dev stand-in for how Cloudflare R2 serves precompressed tiles (tile-transfer initiative, Phase 1;
/// see docs/DEPLOY.md). A GET/HEAD for <c>{requestPath}/…/z/x/y.arrow</c> whose <c>Accept-Encoding</c> includes
/// <c>br</c> and which has a <c>.arrow.br</c> sibling on disk is answered with the sibling's bytes plus
/// <c>Content-Encoding: br</c> — the browser decodes in the network stack, so the page's <c>fetch → arrayBuffer()</c>
/// sees the identical <c>.arrow</c> bytes and the zero-copy decode path is untouched. No sibling, or a client
/// that does not accept br, falls through to the plain static handler (the rollback rail). Static bytes only —
/// nothing is compressed on the request path (RULES R7).</summary>
public static class PrecompressedTiles
{
    private const string ArrowContentType = "application/vnd.apache.arrow.stream";

    public static IApplicationBuilder UsePrecompressedTiles(this IApplicationBuilder app, PathString requestPath, string tilesRoot)
    {
        var provider = new PhysicalFileProvider(Path.GetFullPath(tilesRoot));
        return app.Use(next => async context =>
        {
            var request = context.Request;
            if ((HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method))
                && request.Path.StartsWithSegments(requestPath, out var rest)
                && rest.HasValue
                && rest.Value!.EndsWith(".arrow", StringComparison.OrdinalIgnoreCase)
                && AcceptsBrotli(request.Headers)
                && provider.GetFileInfo(rest.Value + BrotliTileCompressor.SiblingSuffix) is { Exists: true, IsDirectory: false } sibling)
            {
                var response = context.Response;
                response.StatusCode = StatusCodes.Status200OK;
                response.ContentType = ArrowContentType;
                response.Headers.ContentEncoding = "br";
                response.Headers.Vary = HeaderNames.AcceptEncoding;
                // Match the plain static handler's headers (Program.cs) so the two representations are
                // interchangeable to a client or cache: same immutability, same cross-origin timing exposure.
                response.Headers.CacheControl = "public, max-age=31536000, immutable";
                response.Headers["Timing-Allow-Origin"] = "*";
                response.ContentLength = sibling.Length;
                if (HttpMethods.IsHead(request.Method)) return;
                await response.SendFileAsync(sibling, context.RequestAborted);
                return;
            }
            await next(context);
        });
    }

    // True unless Accept-Encoding is absent or lists br with an explicit q=0. Parsed rather than substring-
    // matched so a hypothetical "br;q=0" (client refusing brotli) is honored — the plain path answers it.
    private static bool AcceptsBrotli(IHeaderDictionary headers)
    {
        var values = headers.GetCommaSeparatedValues(HeaderNames.AcceptEncoding);
        if (values.Length == 0) return false;
        if (StringWithQualityHeaderValue.TryParseList(values, out var encodings))
            foreach (var encoding in encodings)
                if (encoding.Value.Equals("br", StringComparison.OrdinalIgnoreCase))
                    return encoding.Quality != 0;
        return false;
    }
}
