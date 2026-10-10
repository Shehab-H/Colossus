import { useState } from 'react';
import type { Meta, StoryObj } from '@storybook/react-vite';
import { expect, fn } from 'storybook/test';
import { colorChannelOption, measureColorOption } from '@/lib/color/colorEncoding';
import type { ColorSpec } from '@/types/viewConfig';
import { coverageChannels } from '../../../../.storybook/fixtures/sources';
import { ColorEncodingEditor } from './ColorEncodingEditor';

type EditorProps = Parameters<typeof ColorEncodingEditor>[0];

const channels = [...coverageChannels.map(colorChannelOption), measureColorOption('total_tests')];

const meta = {
  title: 'Maps/ColorEncodingEditor',
  component: ColorEncodingEditor,
  parameters: {
    layout: 'padded',
    docs: {
      description: {
        component: [
          'Edits the `ColorSpec` of a map: which channel or computed measure is colored, the scale type, a preset palette or custom colors, and the advanced scale options.',
          'It is controlled through `value` and `onChange`; `onChange(undefined)` removes the color encoding.',
          '',
          'Choosing a channel applies a recommended spec (categorical for categories, a linear viridis gradient for numbers). The scale types offered depend on the channel kind, and the preview is drawn with the same scale engine as the published legend, over an illustrative domain.',
          'A saved scale that does not fit the channel is kept and flagged with a warning until it is replaced.',
        ].join('\n'),
      },
    },
  },
  args: { channels, onChange: fn() },
  argTypes: {
    value: { control: 'object', description: 'The current color spec, or undefined for no encoding.' },
    channels: { control: 'object', description: 'Channels and computed measures that can be colored.' },
    error: { control: 'text', description: 'Validation message shown under the channel selector.' },
    disabled: { control: 'boolean', description: 'Disables every control.', table: { defaultValue: { summary: 'false' } } },
    onChange: { control: false, description: 'Called with the next spec, or undefined when the encoding is cleared.' },
  },
} satisfies Meta<typeof ColorEncodingEditor>;

export default meta;
type Story = StoryObj<typeof meta>;

function EditorHost({ value: initial, onChange, ...props }: EditorProps) {
  const [value, setValue] = useState<ColorSpec | undefined>(initial);
  return (
    <ColorEncodingEditor
      {...props}
      value={value}
      onChange={(next) => {
        onChange(next);
        setValue(next);
      }}
    />
  );
}

const render: Story['render'] = (args) => <EditorHost {...args} />;

export const NoEncoding: Story = {
  render,
  play: async ({ args, canvas, userEvent }) => {
    await userEvent.selectOptions(canvas.getByRole('combobox', { name: /Color channel/ }), 'network');
    await expect(args.onChange).toHaveBeenCalledWith({ channel: 'network', type: 'categorical', scheme: 'okabeIto' });
    await expect(await canvas.findByText('Color palette')).toBeInTheDocument();
  },
};

export const SequentialGradient: Story = {
  render,
  args: { value: { channel: 'signal_strength', type: 'linear', scheme: 'viridis' } },
};

export const DivergingScale: Story = {
  render,
  args: { value: { channel: 'signal_strength', type: 'diverging', scheme: 'blueRed', midpoint: -80 } },
};

export const BinnedScale: Story = {
  render,
  args: { value: { channel: 'tests', type: 'quantize', scheme: 'blues', bins: 5 } },
};

export const ThresholdScale: Story = {
  render,
  args: { value: { channel: 'total_tests', type: 'threshold', scheme: 'oranges', thresholds: [100, 1000, 10000] } },
};

export const CategoricalWithOverrides: Story = {
  render,
  args: {
    value: { channel: 'network', type: 'categorical', scheme: 'okabeIto', overrides: { '5G': '#e60000', '4G': '#0067b1' } },
  },
  parameters: { docs: { description: { story: 'Overrides pin specific category values to a color; other categories take colors from the preset in order.' } } },
};

export const CustomColors: Story = {
  render,
  args: { value: { channel: 'signal_strength', type: 'linear', range: ['#fff0f0', '#e60000', '#990000'] } },
  parameters: { docs: { description: { story: 'An explicit `range` replaces the preset; between 2 and 12 colors are allowed.' } } },
};

export const UnusualScaleForChannel: Story = {
  render,
  args: { value: { channel: 'network', type: 'linear', scheme: 'viridis' } },
  parameters: { docs: { description: { story: 'A gradient on a categorical channel is preserved and flagged.' } } },
  play: async ({ canvas }) => {
    await expect(canvas.getByText(/This saved scale is unusual/)).toBeInTheDocument();
  },
};

export const WithError: Story = {
  render,
  args: { value: { channel: 'signal_strength', type: 'linear', scheme: 'viridis' }, error: 'Colors must use #RGB or #RRGGBB hexadecimal notation.' },
};

export const Disabled: Story = {
  render,
  args: { value: { channel: 'signal_strength', type: 'linear', scheme: 'viridis' }, disabled: true },
};
