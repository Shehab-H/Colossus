export interface DatasetGeometrySpec {
  kind: string;
  x?: string;
  y?: string;
  lon?: string;
  lat?: string;
  column?: string;
  geographic?: boolean;
}

export interface DatasetChannelSpec {
  name: string;
  column: string;
  role: string;
  type: string;
}

export interface DatasetSource {
  query: string;
  geometry: DatasetGeometrySpec;
  channels: DatasetChannelSpec[];
}

export interface CatalogDataset {
  id: string;
  workspaceKey: string;
  draft: {
    slug: string;
    name: string;
    connectionId: string;
    source: DatasetSource;
  };
  revision: number;
  archived: boolean;
  createdAt: string;
  updatedAt: string;
}

export interface CatalogConnection {
  id: string;
  workspaceKey: string;
  slug: string;
  name: string;
  adapter: string;
  revision: number;
  archived: boolean;
  createdAt: string;
  updatedAt: string;
}

export interface SourceConfigurationField {
  key: string;
  label: string;
  required: boolean;
  secret: boolean;
  placeholder?: string;
  allowedValues?: string[];
}

export interface SourceAdapterDescriptor {
  id: string;
  name: string;
  capabilities: {
    connectionValidation: boolean;
    locationDiscovery: boolean;
    schemaInspection: boolean;
    boundedPreview: boolean;
    canonicalExtraction: boolean;
  };
  configurationFields: SourceConfigurationField[];
  geometryKinds: string[];
  datasetInputLabel: string;
  datasetInputDescription: string;
  datasetInputPlaceholder: string;
}

export interface SourceConnectionValidation {
  message: string;
  details: Record<string, string>;
}

export interface SourceLocation {
  namespace: string;
  name: string;
  kind: string;
}

export interface SourceSchemaColumn {
  name: string;
  sourceType: string;
  nullable: boolean;
}

export interface SourceSchema {
  columns: SourceSchemaColumn[];
  warnings?: string[];
}

export interface SourcePreviewColumn {
  name: string;
  sourceType: string;
  nullable: boolean;
  role: string;
}

export interface SourcePreview {
  columns: SourcePreviewColumn[];
  rows: Record<string, string | null>[];
  geometry: {
    kind: string;
    geographic: boolean;
    bounds: { minX: number; minY: number; maxX: number; maxY: number };
    sampledRows: number;
    distinctGeometries: number;
  };
  truncated: boolean;
  resultBytes: number;
  elapsed: string;
  warnings?: string[];
}

export interface CreateConnectionInput {
  definition: {
    slug: string;
    name: string;
    adapter: string;
    settings: Record<string, string>;
  };
  secrets: Record<string, string>;
}

export interface CreateDatasetInput {
  slug: string;
  name: string;
  connectionId: string;
  source: DatasetSource;
}
