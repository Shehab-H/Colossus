import { useEffect, useMemo, useState, type FormEvent } from 'react';
import { useQuery } from '@tanstack/react-query';
import { useSearchParams } from 'react-router';
import {
  CatalogApiError,
  connectionsQuery,
  datasetsQuery,
  sourceAdaptersQuery,
  type CatalogConnection,
  type CatalogDataset,
  type SourceAdapterDescriptor,
  type SourceConnectionValidation,
  type SourceLocation,
  type SourceSchema,
} from '@entities/catalog';
import {
  useCreateConnectionMutation,
  useCreateDatasetMutation,
  useDiscoverLocationsMutation,
  useInspectSourceSchemaMutation,
  usePreviewDatasetMutation,
  useTestConnectionMutation,
} from '@features/source-management/api/sourceManagementMutations';
import {
  emptyConnectionForm,
  emptyDatasetForm,
  applySourceSchema,
  parseConnectionForm,
  parseDatasetForm,
  type ConnectionFormState,
  type DatasetFormState,
} from '@features/source-management/model/sourceForms';
import { ConnectionForm } from '@features/source-management/ui/ConnectionForm';
import { DatasetForm } from '@features/source-management/ui/DatasetForm';
import { ButtonRouteLink } from '@shared/navigation/RouteLink';
import { Alert, Badge, Button, LoadingState, PageHeader, PaddedCard } from '@shared/ui';
import ManagementShell from '@widgets/management-shell/ManagementShell';
import { SourceConnectionList } from '@widgets/source-workspace/SourceConnectionList';
import { SourceDatasetList } from '@widgets/source-workspace/SourceDatasetList';
import { SourcePreviewPanel } from '@widgets/source-workspace/SourcePreviewPanel';
import '@widgets/source-workspace/source-workspace.scss';
import './sources.scss';

const emptyConnections: CatalogConnection[] = [];
const emptyDatasets: CatalogDataset[] = [];
const emptyAdapters: SourceAdapterDescriptor[] = [];
const editableGeometryKinds = new Set(['lonLat', 'xy', 'quadkey', 'wkt']);

export default function SourceWorkspacePage() {
  const [searchParams, setSearchParams] = useSearchParams();
  const connectionsResult = useQuery(connectionsQuery());
  const datasetsResult = useQuery(datasetsQuery());
  const adaptersResult = useQuery(sourceAdaptersQuery());
  const connections = connectionsResult.data ?? emptyConnections;
  const datasets = datasetsResult.data ?? emptyDatasets;
  const adapters = adaptersResult.data ?? emptyAdapters;
  const createConnectionMutation = useCreateConnectionMutation();
  const createDatasetMutation = useCreateDatasetMutation();
  const testMutation = useTestConnectionMutation();
  const discoveryMutation = useDiscoverLocationsMutation();
  const schemaMutation = useInspectSourceSchemaMutation();
  const previewMutation = usePreviewDatasetMutation();
  const [connectionForm, setConnectionForm] = useState<ConnectionFormState>(emptyConnectionForm);
  const [datasetForm, setDatasetForm] = useState<DatasetFormState>(emptyDatasetForm);
  const [connectionErrors, setConnectionErrors] = useState<Record<string, string[]>>({});
  const [datasetErrors, setDatasetErrors] = useState<Record<string, string[]>>({});
  const [message, setMessage] = useState<{ tone: 'info' | 'danger'; text: string }>();
  const [validation, setValidation] = useState<{ connectionId: string; result: SourceConnectionValidation }>();
  const [locations, setLocations] = useState<{ connectionId: string; result: SourceLocation[] }>();
  const [schemaInspection, setSchemaInspection] = useState<{ connectionId: string; query: string; result: SourceSchema }>();
  const selectedDatasetId = searchParams.get('dataset') ?? undefined;
  const activeTask = searchParams.get('task') === 'datasets' ? 'datasets' : 'connections';

  useEffect(() => {
    if (!connectionForm.adapter && adapters[0]) setConnectionForm((current) => ({ ...current, adapter: adapters[0].id }));
  }, [adapters, connectionForm.adapter]);

  useEffect(() => {
    if (!datasetForm.connectionId && connections[0]) setDatasetForm((current) => ({ ...current, connectionId: connections[0].id }));
  }, [connections, datasetForm.connectionId]);

  const selectedConnection = connections.find((connection) => connection.id === datasetForm.connectionId);
  const selectedAdapter = adapters.find((adapter) => adapter.id === selectedConnection?.adapter);
  const geometryKinds = useMemo(() => {
    const supported = selectedAdapter?.geometryKinds.filter((kind) => editableGeometryKinds.has(kind)) ?? [];
    return supported.length > 0 ? supported : ['lonLat', 'xy', 'quadkey', 'wkt'];
  }, [selectedAdapter]);
  const sourceInput = {
    label: selectedAdapter?.datasetInputLabel ?? 'Source query',
    description: selectedAdapter?.datasetInputDescription ?? 'Schema and preview use this query without launching a bake.',
    placeholder: selectedAdapter?.datasetInputPlaceholder ?? 'SELECT lon, lat, value FROM analytics.coverage',
  };

  useEffect(() => {
    if (!geometryKinds.includes(datasetForm.geometryKind)) {
      setDatasetForm((current) => ({
        ...current,
        geometryKind: geometryKinds[0] as DatasetFormState['geometryKind'],
      }));
    }
  }, [datasetForm.geometryKind, geometryKinds]);
  const selectedDataset = datasets.find((dataset) => dataset.id === selectedDatasetId);
  const inspectedSchema = schemaInspection?.connectionId === datasetForm.connectionId
    && schemaInspection.query === datasetForm.query.trim()
    ? schemaInspection.result
    : undefined;
  const loading = connectionsResult.isPending || datasetsResult.isPending || adaptersResult.isPending;
  const loadError = connectionsResult.error ?? datasetsResult.error ?? adaptersResult.error;

  useEffect(() => {
    if (loadError) setMessage({ tone: 'danger', text: errorMessage(loadError) });
  }, [loadError]);

  const submitConnection = async (event: FormEvent) => {
    event.preventDefault();
    const parsed = parseConnectionForm(
      connectionForm,
      adapters.find((adapter) => adapter.id === connectionForm.adapter),
    );
    setConnectionErrors(parsed.errors);
    setMessage(undefined);
    if (!parsed.value) return;
    try {
      const created = await createConnectionMutation.mutateAsync(parsed.value);
      setConnectionForm(emptyConnectionForm(connectionForm.adapter));
      setDatasetForm((current) => ({ ...current, connectionId: created.id }));
      setTask('datasets');
      setMessage({ tone: 'info', text: `${created.name} is saved. You can now define a dataset against it.` });
    } catch (error) {
      setConnectionErrors(fieldErrors(error));
      setMessage({ tone: 'danger', text: errorMessage(error) });
    }
  };

  const submitDataset = async (event: FormEvent) => {
    event.preventDefault();
    const parsed = parseDatasetForm(datasetForm);
    setDatasetErrors(parsed.errors);
    setMessage(undefined);
    if (!parsed.value) return;
    try {
      const created = await createDatasetMutation.mutateAsync(parsed.value);
      setDatasetForm(emptyDatasetForm(created.draft.connectionId));
      setSchemaInspection(undefined);
      setSearchParams({ task: 'datasets', dataset: created.id });
      setMessage({ tone: 'info', text: `${created.draft.name} is saved. Run its bounded preview to validate the mapping.` });
    } catch (error) {
      setDatasetErrors(fieldErrors(error));
      setMessage({ tone: 'danger', text: errorMessage(error) });
    }
  };

  const inspectSchema = async () => {
    setMessage(undefined);
    setDatasetErrors((current) => ({ ...current, query: [], geometry: [], channels: [] }));
    try {
      const query = datasetForm.query.trim();
      const result = await schemaMutation.mutateAsync({ connectionId: datasetForm.connectionId, query });
      setSchemaInspection({ connectionId: datasetForm.connectionId, query, result });
      setDatasetForm((current) => applySourceSchema(current, result));
    } catch (error) {
      setDatasetErrors(fieldErrors(error));
      setMessage({ tone: 'danger', text: errorMessage(error) });
    }
  };

  const testConnection = (connectionId: string) => {
    setMessage(undefined);
    testMutation.mutate(connectionId, {
      onSuccess: (result) => setValidation({ connectionId, result }),
      onError: (error) => setMessage({ tone: 'danger', text: errorMessage(error) }),
    });
  };

  const discoverLocations = (connectionId: string) => {
    setMessage(undefined);
    discoveryMutation.mutate(connectionId, {
      onSuccess: (result) => setLocations({ connectionId, result }),
      onError: (error) => setMessage({ tone: 'danger', text: errorMessage(error) }),
    });
  };

  const previewDataset = (datasetId: string) => {
    setMessage(undefined);
    setSearchParams({ task: 'datasets', dataset: datasetId });
    previewMutation.mutate(datasetId, {
      onError: (error) => setMessage({ tone: 'danger', text: errorMessage(error) }),
    });
  };

  const setTask = (task: 'connections' | 'datasets') => {
    const next = new URLSearchParams(searchParams);
    next.set('task', task);
    if (task === 'connections') next.delete('dataset');
    setSearchParams(next);
  };

  return (
    <ManagementShell actions={<ButtonRouteLink variant="ghost" to="/maps">Map catalog</ButtonRouteLink>}>
      <section className="sources-page">
        <PageHeader
          eyebrow="Source platform"
          title="Sources"
          description="Manage source connections, dataset queries, schema mappings, and bounded validation."
          actions={<Badge tone="brand">{adapters.length} adapter{adapters.length === 1 ? '' : 's'}</Badge>}
        />
        <nav className="sources-page__tasks" aria-label="Source management task">
          <Button variant={activeTask === 'connections' ? 'primary' : 'secondary'} onClick={() => setTask('connections')}>Connections</Button>
          <Button variant={activeTask === 'datasets' ? 'primary' : 'secondary'} onClick={() => setTask('datasets')}>Datasets</Button>
        </nav>
        {message && <Alert tone={message.tone}>{message.text}</Alert>}
        {loading ? <LoadingState>Loading source workspace…</LoadingState> : (
          <>
            {activeTask === 'connections' ? <>
              <PaddedCard className="sources-page__form-card">
                <SectionHeading step="1" title="Connection definition" description="Store adapter endpoints and credentials independently from dataset queries." />
                <ConnectionForm
                  form={connectionForm}
                  adapters={adapters}
                  errors={connectionErrors}
                  busy={createConnectionMutation.isPending}
                  onChange={setConnectionForm}
                  onSubmit={(event) => void submitConnection(event)}
                />
              </PaddedCard>
              <section className="sources-page__section">
                <SectionTitle eyebrow="Connections" title="Saved connection definitions" description="Test connectivity or inspect discoverable source locations without creating a dataset." />
                <SourceConnectionList
                  connections={connections}
                  testingId={testMutation.isPending ? testMutation.variables : undefined}
                  discoveringId={discoveryMutation.isPending ? discoveryMutation.variables : undefined}
                  validation={validation}
                  locations={locations}
                  onTest={testConnection}
                  onDiscover={discoverLocations}
                />
              </section>
            </> : <>
              <PaddedCard className="sources-page__form-card">
                <SectionHeading step="1" title="Dataset definition" description="Write the final source query, inspect its result columns, then map geometry and channels." />
                <DatasetForm
                  form={datasetForm}
                  connections={connections}
                  geometryKinds={geometryKinds}
                  sourceInput={sourceInput}
                  schema={inspectedSchema}
                  errors={datasetErrors}
                  busy={createDatasetMutation.isPending}
                  inspecting={schemaMutation.isPending}
                  onChange={setDatasetForm}
                  onInspect={() => void inspectSchema()}
                  onSubmit={(event) => void submitDataset(event)}
                />
              </PaddedCard>
              <section className="sources-page__section">
                <SectionTitle eyebrow="Datasets" title="Saved dataset definitions" description="Run a bounded preview against the same query, geometry mapping, and channels used by extraction." />
                <SourceDatasetList
                  datasets={datasets}
                  connections={connections}
                  selectedId={selectedDatasetId}
                  previewingId={previewMutation.isPending ? previewMutation.variables : undefined}
                  onPreview={previewDataset}
                />
              </section>
              <SourcePreviewPanel preview={previewMutation.data} datasetName={selectedDataset?.draft.name} />
            </>}
          </>
        )}
      </section>
    </ManagementShell>
  );
}

function SectionHeading({ step, title, description }: { step: string; title: string; description: string }) {
  return <div className="sources-page__step"><span>{step}</span><div><h2>{title}</h2><p>{description}</p></div></div>;
}

function SectionTitle({ eyebrow, title, description }: { eyebrow: string; title: string; description: string }) {
  return <header className="sources-page__section-title"><span>{eyebrow}</span><h2>{title}</h2><p>{description}</p></header>;
}

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

function fieldErrors(error: unknown): Record<string, string[]> {
  return error instanceof CatalogApiError ? error.fieldErrors : {};
}
