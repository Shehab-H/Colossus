import { useEffect, useMemo, useRef, useState, type FormEvent, type ReactNode } from 'react';
import type { CatalogDataset } from '@entities/catalog';
import { CatalogApiError, createMap, getManagedMap, listDatasets, setBakePolicy, updateMap } from '@entities/catalog';
import { draftFromForm, emptyMapForm, formFromDraft, hoursToInterval, type MapFormState } from '@features/map-management/model/mapForm';
import {
  Alert,
  Badge,
  Button,
  ButtonLink,
  Checkbox,
  ConfirmDialog,
  Field,
  Input,
  LoadingState,
  PageHeader,
  PaddedCard,
  Select,
  Textarea,
} from '@shared/ui';
import ManagementShell from '@widgets/management-shell/ManagementShell';

const marks: MapFormState['mark'][] = ['point', 'polygon', 'line', 'arc', 'rect', 'heat', 'text'];

export default function MapEditorPage({ mapId }: { mapId?: string }) {
  const editing = Boolean(mapId);
  const [form, setForm] = useState<MapFormState>(emptyMapForm);
  const [datasets, setDatasets] = useState<CatalogDataset[]>([]);
  const [mapRevision, setMapRevision] = useState<number | null>(null);
  const [policyRevision, setPolicyRevision] = useState<number | undefined>();
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [confirmCancel, setConfirmCancel] = useState(false);
  const dirtyRef = useRef(false);
  const [errors, setErrors] = useState<Record<string, string[]>>({});
  const [message, setMessage] = useState<string | null>(null);

  useEffect(() => {
    let alive = true;
    Promise.all([listDatasets(), mapId ? getManagedMap(mapId) : Promise.resolve(null)])
      .then(([available, detail]) => {
        if (!alive) return;
        setDatasets(available);
        if (detail) {
          setForm(formFromDraft(detail.definition, detail.bakePolicy));
          setMapRevision(detail.summary.revision);
          setPolicyRevision(detail.bakePolicy?.revision);
        } else {
          setForm((current) => ({ ...current, datasetId: available[0]?.id ?? '' }));
        }
        setLoading(false);
        dirtyRef.current = false;
      }, (reason) => {
        if (!alive) return;
        setLoading(false);
        setMessage(reason instanceof Error ? reason.message : String(reason));
      });
    return () => { alive = false; };
  }, [mapId]);

  useEffect(() => {
    const protect = (event: BeforeUnloadEvent) => {
      if (!dirtyRef.current) return;
      event.preventDefault();
    };
    window.addEventListener('beforeunload', protect);
    return () => window.removeEventListener('beforeunload', protect);
  }, []);

  const selectedDataset = useMemo(
    () => datasets.find((dataset) => dataset.id === form.datasetId),
    [datasets, form.datasetId],
  );

  const change = <K extends keyof MapFormState>(field: K, value: MapFormState[K]) => {
    setForm((current) => ({ ...current, [field]: value }));
    dirtyRef.current = true;
    setErrors((current) => {
      const next = { ...current };
      delete next[field];
      return next;
    });
  };

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    const parsed = draftFromForm(form);
    setErrors(parsed.errors);
    setMessage(null);
    if (!parsed.draft || (editing && mapRevision === null)) return;
    setSaving(true);
    try {
      const saved = editing
        ? await updateMap(mapId!, mapRevision!, parsed.draft)
        : await createMap(parsed.draft);
      await setBakePolicy(saved.id, {
        enabled: form.scheduleEnabled,
        interval: hoursToInterval(form.intervalHours),
        timeZoneId: 'UTC',
        allowOverlap: false,
        retainSuccessfulReleases: Math.max(1, Number(form.retention) || 3),
      }, policyRevision);
      dirtyRef.current = false;
      window.location.assign(`/maps/manage/${saved.id}`);
    } catch (reason) {
      if (reason instanceof CatalogApiError) {
        setErrors(reason.fieldErrors);
        setMessage(reason.message);
      } else {
        setMessage(reason instanceof Error ? reason.message : String(reason));
      }
    } finally {
      setSaving(false);
    }
  };

  const leaveEditor = () => {
    dirtyRef.current = false;
    window.location.assign(mapId ? `/maps/manage/${mapId}` : '/maps');
  };

  const cancel = () => dirtyRef.current ? setConfirmCancel(true) : leaveEditor();
  const fieldError = (field: string) => errors[field]?.join(' ');

  return (
    <ManagementShell actions={<ButtonLink variant="ghost" href={mapId ? `/maps/manage/${mapId}` : '/maps'}>Back to maps</ButtonLink>}>
      <form className="maps-editor" onSubmit={(event) => void submit(event)}>
        <PageHeader
          eyebrow={editing ? 'Edit map' : 'New map'}
          title={editing ? form.title || form.slug : 'Create a map that is ready to scale.'}
          description="Configure the view against a durable dataset. Server validation remains authoritative."
          actions={<><Button variant="secondary" onClick={cancel}>Cancel</Button><Button variant="primary" type="submit" disabled={saving || loading || datasets.length === 0}>{saving ? 'Saving…' : editing ? 'Save changes' : 'Create map'}</Button></>}
        />

        {message && <Alert tone="danger">{message}</Alert>}
        {errors.definition && <Alert tone="danger">{errors.definition.join(' ')}</Alert>}

        {loading ? <LoadingState>Loading editor…</LoadingState> : (
          <div className="maps-editor__grid">
            <PaddedCard className="maps-editor__card">
              <SectionHeading step="1" title="Identity & dataset" description="Stable URL identity and source contract." />
              <div className="maps-editor__fields maps-editor__fields--two">
                <Field label="Slug" description="Lowercase kebab-case; permanent after creation." error={fieldError('slug')}>
                  <Input value={form.slug} onChange={(event) => change('slug', event.target.value)} placeholder="mobile-coverage" disabled={editing} aria-invalid={Boolean(fieldError('slug'))} />
                </Field>
                <Field label="Title" error={fieldError('title')}>
                  <Input value={form.title} onChange={(event) => change('title', event.target.value)} placeholder="Mobile coverage" aria-invalid={Boolean(fieldError('title'))} />
                </Field>
              </div>
              <Field label="Dataset" error={fieldError('datasetId')}>
                <Select value={form.datasetId} onChange={(event) => change('datasetId', event.target.value)} aria-invalid={Boolean(fieldError('datasetId'))}>
                  <option value="">Select a dataset</option>
                  {datasets.map((dataset) => <option key={dataset.id} value={dataset.id}>{dataset.draft.name}</option>)}
                </Select>
              </Field>
              {datasets.length === 0 && <Alert>No datasets are available. Import or create a dataset before creating a map.</Alert>}
              {selectedDataset && (
                <div className="maps-editor__dataset">
                  <div className="maps-editor__dataset-summary">
                    <span><small>Geometry</small><strong>{selectedDataset.draft.source.geometry.kind}</strong></span>
                    <span><small>Channels</small><strong>{selectedDataset.draft.source.channels.length}</strong></span>
                  </div>
                  <details>
                    <summary>Inspect source contract</summary>
                    <pre>{JSON.stringify(selectedDataset.draft.source.geometry, null, 2)}</pre>
                    <div className="maps-editor__channels">{selectedDataset.draft.source.channels.map((channel) => <Badge key={channel.name}>{channel.name} · {channel.role} · {channel.type}</Badge>)}</div>
                    <pre>{selectedDataset.draft.source.query}</pre>
                  </details>
                </div>
              )}
            </PaddedCard>

            <PaddedCard className="maps-editor__card">
              <SectionHeading step="2" title="Map view" description="Rendering surface and mark geometry." />
              <div className="maps-editor__fields maps-editor__fields--two">
                <Field label="Viewport"><Select value={form.viewport} onChange={(event) => change('viewport', event.target.value as MapFormState['viewport'])}><option value="geo">Geographic</option><option value="orthographic">Orthographic</option></Select></Field>
                <Field label="Mark"><Select value={form.mark} onChange={(event) => change('mark', event.target.value as MapFormState['mark'])}>{marks.map((mark) => <option key={mark} value={mark}>{mark}</option>)}</Select></Field>
              </div>
            </PaddedCard>

            <PaddedCard className="maps-editor__card maps-editor__card--wide">
              <SectionHeading step="3" title="Channels, filters & encoding" description="Technical JSON remains available until source discovery powers the assisted builder." />
              <div className="maps-editor__json-grid">
                <JsonField label="Bake filters" field="bakeFilters" value={form.bakeFilters} onChange={change} errors={errors} />
                <JsonField label="Interactive filters" field="filters" value={form.filters} onChange={change} errors={errors} />
                <JsonField label="Measures" field="measures" value={form.measures} onChange={change} errors={errors} />
                <JsonField label="Encoding" field="encoding" value={form.encoding} onChange={change} errors={errors} placeholder={'{\n  "color": { "channel": "value" }\n}'} />
                <JsonField label="Inspection" field="inspect" value={form.inspect} onChange={change} errors={errors} placeholder={'{\n  "channels": ["value"]\n}'} />
              </div>
            </PaddedCard>

            <PaddedCard className="maps-editor__card maps-editor__card--wide">
              <SectionHeading step="4" title="Schedule & retention" description="Persist policy now; the worker will execute it when scheduling lands." />
              <div className="maps-editor__schedule">
                <Checkbox label="Enable recurring bake" checked={form.scheduleEnabled} onChange={(event) => change('scheduleEnabled', event.target.checked)} />
                <Field label="Interval (hours)"><Input type="number" min="1" value={form.intervalHours} onChange={(event) => change('intervalHours', event.target.value)} /></Field>
                <Field label="Successful releases to retain"><Input type="number" min="1" value={form.retention} onChange={(event) => change('retention', event.target.value)} /></Field>
              </div>
            </PaddedCard>
          </div>
        )}
      </form>

      <ConfirmDialog open={confirmCancel} title="Discard unsaved changes?" description="Your changes to this map have not been saved." confirmLabel="Discard changes" busy={false} onCancel={() => setConfirmCancel(false)} onConfirm={leaveEditor} />
    </ManagementShell>
  );
}

function SectionHeading({ step, title, description }: { step: string; title: string; description: string }) {
  return <div className="maps-editor__section-heading"><span>{step}</span><div><h2>{title}</h2><p>{description}</p></div></div>;
}

function JsonField<K extends 'bakeFilters' | 'filters' | 'measures' | 'encoding' | 'inspect'>({
  label, field, value, onChange, errors, placeholder,
}: {
  label: string;
  field: K;
  value: string;
  onChange: <T extends keyof MapFormState>(field: T, value: MapFormState[T]) => void;
  errors: Record<string, string[]>;
  placeholder?: string;
}) {
  const error: ReactNode = errors[field]?.join(' ');
  return <Field label={label} error={error}><Textarea className="maps-editor__json" value={value} placeholder={placeholder} onChange={(event) => onChange(field, event.target.value)} spellCheck={false} aria-invalid={Boolean(error)} /></Field>;
}
