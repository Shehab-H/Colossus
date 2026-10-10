import type { Meta, StoryObj } from '@storybook/react-vite';
import { fn } from 'storybook/test';
import { Button, ButtonLink } from './Button';

const variants = ['primary', 'secondary', 'ghost', 'danger'] as const;
const sizes = ['sm', 'md', 'lg'] as const;

const meta = {
  title: 'Components/Button',
  component: Button,
  subcomponents: { ButtonLink },
  parameters: {
    layout: 'padded',
    docs: {
      description: {
        component: [
          '`Button` renders a native `<button>` and `ButtonLink` renders an `<a>` with the same classes.',
          'Both accept the style props `variant`, `size` and `iconOnly`, resolved by `buttonClassName` in `buttonStyles.ts`; every other prop is passed to the element.',
          '`Button` defaults `type` to `"button"`, so it never submits a form unless asked.',
          '',
          'For client-side navigation use `ButtonRouteLink` (see the RouteLink entry), which applies the same styles to a router link.',
        ].join('\n'),
      },
    },
  },
  args: { children: 'Publish map', onClick: fn() },
  argTypes: {
    variant: {
      control: 'inline-radio',
      options: variants,
      description: 'Visual hierarchy of the action.',
      table: { type: { summary: variants.map((value) => `'${value}'`).join(' | ') }, defaultValue: { summary: "'secondary'" } },
    },
    size: {
      control: 'inline-radio',
      options: sizes,
      description: 'Control height and padding.',
      table: { type: { summary: sizes.map((value) => `'${value}'`).join(' | ') }, defaultValue: { summary: "'md'" } },
    },
    iconOnly: {
      control: 'boolean',
      description: 'Square button for a single icon; always pair it with an `aria-label`.',
      table: { type: { summary: 'boolean' }, defaultValue: { summary: 'false' } },
    },
    disabled: { control: 'boolean', description: 'Native disabled state.', table: { type: { summary: 'boolean' } } },
    type: {
      control: 'inline-radio',
      options: ['button', 'submit', 'reset'],
      description: 'Native button type.',
      table: { defaultValue: { summary: "'button'" } },
    },
    className: { control: false, description: 'Extra classes appended to the generated ones.' },
    onClick: { control: false, description: 'Click handler.' },
  },
} satisfies Meta<typeof Button>;

export default meta;
type Story = StoryObj<typeof meta>;

export const Playground: Story = {};

export const Variants: Story = {
  parameters: { docs: { description: { story: 'All four variants at the default size. `secondary` is used when no variant is given.' } } },
  render: (args) => (
    <div className="story-row">
      {variants.map((variant) => <Button key={variant} {...args} variant={variant}>{variant}</Button>)}
    </div>
  ),
};

export const Sizes: Story = {
  parameters: { docs: { description: { story: 'Minimum heights come from `--control-height-sm`, `--control-height-md` and `--control-height-lg`.' } } },
  render: (args) => (
    <div className="story-row">
      {sizes.map((size) => <Button key={size} {...args} size={size}>{size}</Button>)}
    </div>
  ),
};

export const IconOnly: Story = {
  args: { iconOnly: true, variant: 'ghost', children: '×', 'aria-label': 'Close' },
  parameters: { docs: { description: { story: 'The glyph is the only content, so the accessible name must come from `aria-label`.' } } },
};

export const Disabled: Story = {
  parameters: { docs: { description: { story: 'Disabled buttons drop to half opacity and stop reacting to hover and press.' } } },
  render: (args) => (
    <div className="story-row">
      {variants.map((variant) => <Button key={variant} {...args} variant={variant} disabled>{variant}</Button>)}
    </div>
  ),
};

export const AsLink: Story = {
  name: 'ButtonLink',
  parameters: { docs: { description: { story: '`ButtonLink` is an anchor, so it takes `href`, `target` and `rel` instead of `onClick` and `type`.' } } },
  render: () => (
    <div className="story-row">
      {variants.map((variant) => <ButtonLink key={variant} variant={variant} href="#button-link">{variant}</ButtonLink>)}
    </div>
  ),
};
