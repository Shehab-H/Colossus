import { LayerExtension } from '@deck.gl/core';
import type { Layer, LayerContext, UpdateParameters } from '@deck.gl/core';
import type { Texture } from '@luma.gl/core';
import type { ColorLut } from './colorLut';

// A LayerExtension that colors marks on the GPU from a lookup-table texture. The per-mark `scaleValue`
// attribute uploads once per (tile, channel) and is reused across every scale/theme; a recolor swaps the
// LUT texture + a few uniforms, touching no per-mark data. Sibling of deck's own DataFilterExtension —
// the value→t transform mirrors colorScale.ts (baked by colorLut.ts), so the GPU can't disagree with the
// legend.

type ColorScaleProps = { scaleLut?: ColorLut | null };

const uniformBlock = /* glsl */ `\
layout(std140) uniform colorScaleUniforms {
  vec2 domain;
  float transform;
  float kind;
  vec3 unknownColor;
  float lutWidth;
} colorScale;
`;

// The sampler + the per-mark value attribute sit outside the std140 block (luma binds them separately).
// Vertex-shader texture fetch is core in GLSL 300 es / WebGL2 — everywhere deck 9 runs.
const vsDecls = /* glsl */ `
uniform sampler2D colorScaleLut;
in float scaleValue;
`;

const colorScaleModule = {
  name: 'colorScale',
  vs: uniformBlock + vsDecls,
  fs: uniformBlock,
  uniformTypes: {
    domain: 'vec2<f32>',
    transform: 'f32',
    kind: 'f32',
    unknownColor: 'vec3<f32>',
    lutWidth: 'f32',
  },
  inject: {
    // Runs where the layer sets its per-vertex color. Every vertex of a mark carries the same
    // scaleValue, so the mark reads flat — matching the old per-vertex-uniform color exactly.
    'vs:DECKGL_FILTER_COLOR': /* glsl */ `
      float colorScale_v = scaleValue;
      float colorScale_t;
      if (colorScale.kind > 0.5) {
        colorScale_t = (min(colorScale_v, colorScale.lutWidth - 1.0) + 0.5) / colorScale.lutWidth;
      } else {
        if (colorScale.transform > 0.5) colorScale_v = log(colorScale_v);
        colorScale_t = clamp((colorScale_v - colorScale.domain.x) / (colorScale.domain.y - colorScale.domain.x), 0.0, 1.0);
      }
      vec3 colorScale_rgb = (colorScale_v != colorScale_v)
        ? colorScale.unknownColor
        : texture(colorScaleLut, vec2(colorScale_t, 0.5)).rgb;
      color.rgb = colorScale_rgb;
    `,
  },
};

const TEXTURE_PROPS = {
  format: 'rgba8unorm' as const,
  dimension: '2d' as const,
  sampler: {
    minFilter: 'nearest' as const,
    magFilter: 'nearest' as const,
    addressModeU: 'clamp-to-edge' as const,
    addressModeV: 'clamp-to-edge' as const,
  },
};

export default class ColorScaleExtension extends LayerExtension {
  static extensionName = 'ColorScaleExtension';
  static defaultProps = {
    getScaleValue: { type: 'accessor', value: 0 }, // supplied binary via data.attributes; default unused
    scaleLut: null,
  };

  getShaders(this: Layer<ColorScaleProps>) {
    return { modules: [colorScaleModule] };
  }

  initializeState(this: Layer<ColorScaleProps>, _context: LayerContext, extension: ColorScaleExtension) {
    this.getAttributeManager()?.add({
      scaleValue: { size: 1, type: 'float32', stepMode: 'dynamic', accessor: 'getScaleValue' },
    });
    extension.updateTexture.call(this);
  }

  updateState(
    this: Layer<ColorScaleProps>,
    { props, oldProps }: UpdateParameters<Layer<ColorScaleProps>>,
    extension: ColorScaleExtension,
  ) {
    if (props.scaleLut !== oldProps.scaleLut) extension.updateTexture.call(this);
  }

  draw(this: Layer<ColorScaleProps>) {
    const lut = this.props.scaleLut;
    const texture = this.state.colorScaleTexture as Texture | undefined;
    if (!lut || !texture) return;
    const [ur, ug, ub] = lut.unknown;
    this.setShaderModuleProps({
      colorScale: {
        domain: lut.domain,
        transform: lut.transform,
        kind: lut.kind === 'categorical' ? 1 : 0,
        unknownColor: [ur / 255, ug / 255, ub / 255],
        lutWidth: lut.width,
        colorScaleLut: texture,
      },
    });
  }

  finalizeState(this: Layer<ColorScaleProps>) {
    (this.state.colorScaleTexture as Texture | undefined)?.destroy();
  }

  // Rebuild the LUT texture when the `scaleLut` prop changes identity (App memoizes it on scaleKey).
  updateTexture(this: Layer<ColorScaleProps>) {
    const lut = this.props.scaleLut;
    (this.state.colorScaleTexture as Texture | undefined)?.destroy();
    if (!lut) {
      this.setState({ colorScaleTexture: undefined });
      return;
    }
    this.setState({
      colorScaleTexture: this.context.device.createTexture({
        ...TEXTURE_PROPS,
        data: lut.texels,
        width: lut.width,
        height: 1,
      }),
    });
  }
}

// Module-level singleton — a fresh instance per render would defeat deck's prop diffing (extension
// identity), forcing a shader relink each frame. Same rule as Phase 1's DataFilterExtension.
export const colorScaleExtension = new ColorScaleExtension();
