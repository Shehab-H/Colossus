import { useState } from 'react';
import type { Meta, StoryObj } from '@storybook/react-vite';
import { expect } from 'storybook/test';
import {
  Alert,
  Badge,
  Button,
  ButtonLink,
  Checkbox,
  ConfirmDialog,
  EmptyState,
  Field,
  Input,
  PaddedCard,
  Select,
  StatCard,
  Textarea,
} from '@shared/ui';

const meta = {
  title: 'Foundations/Design System',
  parameters: { layout: 'fullscreen' },
} satisfies Meta;

export default meta;
type Story = StoryObj<typeof meta>;

const colors = [
  ['Brand', '--color-brand'],
  ['Canvas', '--color-bg-canvas'],
  ['Surface', '--color-bg-surface'],
  ['Success', '--color-success'],
  ['Warning', '--color-warning'],
  ['Danger', '--color-danger'],
  ['Information', '--color-info'],
  ['Text', '--color-text-primary'],
  ['Secondary text', '--color-text-secondary'],
  ['Border', '--color-border-default'],
] as const;

export const Showcase: Story = {
  render: () => (
    <main className="workbench" data-testid="design-system-showcase">
      <header className="workbench__hero">
        <div className="workbench__brand">
          <div className="workbench__brand-mark" aria-hidden="true">C</div>
          <div>
            <span className="workbench__eyebrow">Vodafone-inspired system</span>
            <h1>Colossus UI</h1>
          </div>
        </div>
        <p>Semantic tokens and accessible React primitives for the control plane and dense-map viewer.</p>
      </header>

      <div className="workbench__content">
        <PaddedCard className="workbench__section">
          <div className="workbench__section-heading">
            <h2>Semantic color</h2>
            <p>The same contracts resolve for light and dark themes.</p>
          </div>
          <div className="workbench__palette">
            {colors.map(([label, token]) => (
              <div className="workbench__color" key={token}>
                <div className="workbench__swatch" style={{ '--workbench-swatch': `var(${token})` } as React.CSSProperties} />
                <div className="workbench__color-copy"><strong>{label}</strong><code>{token}</code></div>
              </div>
            ))}
          </div>
        </PaddedCard>

        <PaddedCard className="workbench__section">
          <div className="workbench__section-heading"><h2>Actions and status</h2><p>Variants communicate hierarchy and operational state.</p></div>
          <div className="workbench__grid">
            <div className="workbench__group">
              <h3>Buttons</h3>
              <div className="workbench__row">
                <Button variant="primary">Publish map</Button>
                <Button variant="secondary">Save draft</Button>
                <Button variant="ghost">Preview</Button>
                <Button variant="danger">Archive</Button>
                <Button disabled>Unavailable</Button>
                <ButtonLink href="#workbench-fields">Open fields</ButtonLink>
              </div>
            </div>
            <div className="workbench__group">
              <h3>Badges</h3>
              <div className="workbench__row">
                <Badge>Draft</Badge><Badge tone="brand">Vodafone</Badge><Badge tone="success">Published</Badge>
                <Badge tone="warning">Scheduled</Badge><Badge tone="danger">Failed</Badge><Badge tone="info">Running</Badge>
              </div>
            </div>
          </div>
        </PaddedCard>

        <PaddedCard className="workbench__section" id="workbench-fields">
          <div className="workbench__section-heading"><h2>Fields and feedback</h2><p>Labels, descriptions, validation, and system responses share one rhythm.</p></div>
          <div className="workbench__grid">
            <div className="workbench__stack">
              <Field label="Map title" description="Visible in the catalog and viewer."><Input defaultValue="Network coverage" /></Field>
              <Field label="Source adapter"><Select defaultValue="clickhouse"><option value="clickhouse">ClickHouse</option><option value="trino">Trino</option><option value="parquet">Parquet</option></Select></Field>
              <Field label="Description"><Textarea defaultValue="Published coverage measurements by market." /></Field>
              <Checkbox label="Run a bake after saving" defaultChecked />
            </div>
            <div className="workbench__stack">
              <Alert>Release v20260814 is ready to publish.</Alert>
              <Alert tone="danger">The source query could not be validated.</Alert>
              <EmptyState eyebrow="No releases" title="Publish the first release" description="Run a verified bake to make this map available to viewers." action={<Button variant="primary">Run bake</Button>} />
            </div>
          </div>
        </PaddedCard>

        <PaddedCard className="workbench__section">
          <div className="workbench__section-heading"><h2>Operational summaries</h2><p>Compact, scan-friendly read models for management pages.</p></div>
          <div className="workbench__stats">
            <StatCard label="Published release" value="v20260814" note="12 minutes ago" />
            <StatCard label="Visible cells" value="13.2M" note="4 tiles in viewport" />
            <StatCard label="Last run" value="Failed" note="Source timeout" danger />
          </div>
        </PaddedCard>
      </div>
    </main>
  ),
};

function ConfirmationExample() {
  const [open, setOpen] = useState(false);
  return (
    <main className="workbench-dialog-trigger">
      <PaddedCard>
        <Button variant="danger" onClick={() => setOpen(true)}>Archive map</Button>
      </PaddedCard>
      <ConfirmDialog
        open={open}
        title="Archive Network coverage?"
        description="The map will disappear from the active catalog. Published artifacts remain immutable."
        confirmLabel="Archive map"
        onConfirm={() => setOpen(false)}
        onCancel={() => setOpen(false)}
      />
    </main>
  );
}

export const ConfirmationFlow: Story = {
  render: () => <ConfirmationExample />,
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(canvas.getByRole('button', { name: 'Archive map' }));
    await expect(canvas.getByRole('dialog')).toBeInTheDocument();
    await userEvent.click(canvas.getByRole('button', { name: 'Cancel' }));
    await expect(canvas.queryByRole('dialog')).not.toBeInTheDocument();
  },
};
