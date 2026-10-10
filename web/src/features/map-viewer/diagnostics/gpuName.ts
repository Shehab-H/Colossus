/** The GPU actually rendering this session, via WEBGL_debug_renderer_info. Static, read once and cached —
 *  it costs a throwaway context, and the extension is absent in some privacy configurations (undefined,
 *  not a guess). Not a measurement, but without it none of the GPU or frame numbers above are comparable
 *  across machines: "8ms/frame" means nothing until you know what drew it. */
let gpuNameCache: string | null | undefined;

export function gpuName(): string | null {
  if (gpuNameCache !== undefined) return gpuNameCache;
  gpuNameCache = null;
  try {
    const c = document.createElement('canvas');
    const gl = c.getContext('webgl2') ?? c.getContext('webgl');
    const ext = gl?.getExtension('WEBGL_debug_renderer_info');
    if (gl && ext) {
      // Renderer strings are long and vendor-decorated, e.g.
      //   ANGLE (AMD, AMD Radeon RX 6750 XT (0x00007479) Direct3D11 vs_5_0 ps_5_0, D3D11)
      // The second comma-field is the device; the hex id and shader-model suffix are noise.
      const raw = String(gl.getParameter(ext.UNMASKED_RENDERER_WEBGL) ?? '');
      const m = /\(([^,]+),\s*([^,]+)/.exec(raw);
      const name = (m ? m[2] : raw).replace(/\s*\(0x[0-9a-f]+\).*$/i, '').trim();
      gpuNameCache = name.slice(0, 30) || null;
    }
  } catch {
    gpuNameCache = null;
  }
  return gpuNameCache;
}
