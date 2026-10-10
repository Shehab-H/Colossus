import type { Meta, StoryObj } from '@storybook/react-vite';
import { Checkbox, Field, Input, Select, Textarea } from './Field';

const meta = {
  title: 'Components/Field',
  component: Field,
  subcomponents: { Input, Select, Textarea, Checkbox },
  parameters: {
    layout: 'padded',
    docs: {
      description: {
        component: [
          '`Field` is a `<label>` that wraps one control, so the label text names the control without ids.',
          'It renders `label`, an optional `description` above the control and an optional `error` below it.',
          '',
          '`Input`, `Select` and `Textarea` apply the shared control styles and pass remaining props to the native element; set `aria-invalid="true"` to get the error border.',
          '`Checkbox` is its own label and takes a `label` prop.',
        ].join('\n'),
      },
    },
  },
  args: { label: 'Map title', children: <Input defaultValue="Network coverage" /> },
  argTypes: {
    label: { control: 'text', description: 'Visible label text.' },
    description: { control: 'text', description: 'Helper text shown between the label and the control.' },
    error: { control: 'text', description: 'Validation message shown below the control.' },
    className: { control: false, description: 'Extra classes on the label element.' },
    children: { control: false, description: 'The single control the label wraps.' },
  },
  decorators: [(Story) => <div className="story-narrow story-frame"><Story /></div>],
} satisfies Meta<typeof Field>;

export default meta;
type Story = StoryObj<typeof meta>;

export const Playground: Story = {};

export const WithDescription: Story = {
  args: { description: 'Visible in the catalog and viewer.' },
};

export const WithError: Story = {
  args: {
    label: 'Map slug',
    error: 'Use lowercase kebab-case.',
    children: <Input defaultValue="Network Coverage" aria-invalid="true" />,
  },
};

export const SelectField: Story = {
  args: {
    label: 'Source adapter',
    children: (
      <Select defaultValue="clickhouse">
        <option value="clickhouse">ClickHouse</option>
        <option value="trino">Trino</option>
        <option value="parquet">Parquet</option>
      </Select>
    ),
  },
};

export const TextareaField: Story = {
  args: {
    label: 'Description',
    children: <Textarea defaultValue="Published coverage measurements by market." />,
  },
};

export const DisabledField: Story = {
  args: {
    label: 'Dataset',
    description: 'Fixed after the first bake.',
    children: <Input defaultValue="mobile-coverage" disabled />,
  },
};

export const CheckboxControl: Story = {
  name: 'Checkbox',
  render: () => (
    <div className="story-stack">
      <Checkbox label="Run a bake after saving" defaultChecked />
      <Checkbox label="Show archived" />
      <Checkbox label="Unavailable option" disabled />
    </div>
  ),
};
