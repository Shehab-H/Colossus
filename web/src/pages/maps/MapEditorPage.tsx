import { useEffect, useMemo, useRef, useState, type FormEvent } from 'react';
import { useQuery } from '@tanstack/react-query';
import { useNavigate } from 'react-router';
import { CatalogApiError, datasetsQuery, managedMapQuery, type CatalogDataset } from '@entities/catalog';
import { useSaveManagedMapMutation } from '@features/map-management/api/mapManagementMutations';
import { draftFromForm, emptyMapForm, formFromDraft, intervalFromParts, type MapFormState } from '@features/map-management/model/mapForm';
import { MapConfigurationBuilder } from '@features/map-management/ui/MapConfigurationBuilder';
import {
  Alert,
  Badge,
  Button,
  Checkbox,
  ConfirmDialog,
  Field,
  Input,
  LoadingState,
  PageHeader,
  PaddedCard,
  Select,
} from '@shared/ui';
import { ButtonRouteLink } from '@shared/navigation/RouteLink';
import ManagementShell from '@widgets/management-shell/ManagementShell';

const marks: MapFormState['mark'][] = ['point', 'polygon', 'line', 'arc', 'rect', 'heat', 'text'];
const emptyDatasets: CatalogDataset[] = [];

export default function MapEditorPage({ mapId }: { mapId?: string }) {
  const navigate = useNavigate();
  const editing = Boolean(mapId);
  const datasetsResult = useQuery(datasetsQuery());
  const detailResult = useQuery({ ...managedMapQuery(mapId ?? ''), enabled: editing });
  const saveMutation = useSaveManagedMapMutation();
  const datasets = datasetsResult.data ?? emptyDatasets;
  const detail = detailResult.data;
  const loading = datasetsResult.isPending || (editing && detailResult.isPending);
  const loadError = datasetsResult.error ?? detailResult.error;
  const [form, setForm] = useState<MapFormState>(emptyMapForm);
  const [mapRevision, setMapRevision] = useState<number | null>(null);
  const [policyRevision, setPolicyRevision] = useState<number | undefined>();
  const [confirmCancel, setConfirmCancel] = useState(false);
  const dirtyRef = useRef(false);
  const initializedRef = useRef<string | null>(null);
  const [errors, setErrors] = useState<Record<string, string[]>>({});
  const [message, setMessage] = useState<string | null>(null);

  useEffect(() => {
    if (loading || loadError || (editing && !detail)) return;
    const initializationKey = editing ? `${mapId}:${detail!.summary.revision}` : 'new';
    if (initializedRef.current === initializationKey) return;
    initializedRef.current = initializationKey;
    if (detail) {
      setForm(formFromDraft(detail.definition, detail.bakePolicy));
      setMapRevision(detail.summary.revision);
      setPolicyRevision(detail.bakePolicy?.revision);
    } else {
      setForm({ ...emptyMapForm(), datasetId: datasets[0]?.id ?? '' });
      setMapRevision(null);
      setPolicyRevision(undefined);
    }
    dirtyRef.current = false;
  }, [datasets, detail, editing, loadError, loading, mapId]);

  useEffect(() => {
    if (loadError) setMessage(loadError instanceof Error ? loadError.message : String(loadError));
  }, [loadError]);

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
    try {
      const saved = await saveMutation.mutateAsync({
        mapId,
        expectedRevision: mapRevision ?? undefined,
        definition: parsed.draft,
        policyRevision,
        policy: {
          enabled: form.scheduleEnabled,
          interval: intervalFromParts(form),
          timeZoneId: 'UTC',
          allowOverlap: false,
          retainSuccessfulReleases: Math.max(1, Number(form.retention) || 3),
        },
      });
      dirtyRef.current = false;
      await navigate(`/maps/manage/${saved.id}`);
    } catch (reason) {
      if (reason instanceof CatalogApiError) {
        setErrors(reason.fieldErrors);
        setMessage(reason.message);
      } else {
        setMessage(reason instanceof Error ? reason.message : String(reason));
      }
    }
  };

  const leaveEditor = () => {
    dirtyRef.current = false;
    void navigate(mapId ? `/maps/manage/${mapId}` : '/maps');
  };

  const cancel = () => dirtyRef.current ? setConfirmCancel(true) : leaveEditor();
  const fieldError = (field: string) => errors[field]?.join(' ');

  return (
    <ManagementShell actions={<ButtonRouteLink variant="ghost" to={mapId ? `/maps/manage/${mapId}` : '/maps'}>Back to maps</ButtonRouteLink>}>
      <form className="maps-editor" onSubmit={(event) => void submit(event)}>
        <PageHeader
          eyebrow={editing ? 'Edit map' : 'New map'}
          title={editing ? form.title || form.slug : 'Create map configuration'}
          description="Define the dataset binding, rendering configuration, interaction contract, and bake policy."
          actions={<><Button variant="secondary" onClick={cancel}>Cancel</Button><Button variant="primary" type="submit" disabled={saveMutation.isPending || loading || datasets.length === 0}>{saveMutation.isPending ? 'Saving…' : editing ? 'Save changes' : 'Create map'}</Button></>}
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
              <div><ButtonRouteLink variant="ghost" size="sm" to="/sources?task=datasets">Manage datasets</ButtonRouteLink></div>
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
              <SectionHeading step="3" title="Map configuration" description="Configure extraction predicates, controls, computed measures, encoding, and inspection against the selected dataset contract." />
              <MapConfigurationBuilder form={form} dataset={selectedDataset} errors={errors} onChange={change} />
            </PaddedCard>

            <PaddedCard className="maps-editor__card maps-editor__card--wide">
              <SectionHeading step="4" title="Schedule & retention" description="Recurring bake intervals are evaluated to the second. Successful release retention is applied after publication." />
              <div className="maps-editor__schedule">
                <Checkbox label="Enable recurring bake" checked={form.scheduleEnabled} onChange={(event) => change('scheduleEnabled', event.target.checked)} />
                <div className="maps-editor__duration" role="group" aria-label="Bake interval">
                  <Field label="Days"><Input type="number" min="0" value={form.intervalDays} onChange={(event) => change('intervalDays', event.target.value)} /></Field>
                  <Field label="Hours"><Input type="number" min="0" max="23" value={form.intervalHours} onChange={(event) => change('intervalHours', event.target.value)} /></Field>
                  <Field label="Minutes"><Input type="number" min="0" max="59" value={form.intervalMinutes} onChange={(event) => change('intervalMinutes', event.target.value)} /></Field>
                  <Field label="Seconds"><Input type="number" min="0" max="59" value={form.intervalSeconds} onChange={(event) => change('intervalSeconds', event.target.value)} /></Field>
                </div>
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
