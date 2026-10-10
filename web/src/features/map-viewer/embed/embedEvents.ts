import type { EmbedCamera } from './embedUrl';

export const EMBED_MESSAGE_SOURCE = 'colossus.map-viewer' as const;
export const EMBED_MESSAGE_VERSION = 1 as const;

export interface EmbedSelection {
  title?: string;
  rows: { name: string; value: string }[];
}

export type EmbedEvent =
  | { source: typeof EMBED_MESSAGE_SOURCE; version: typeof EMBED_MESSAGE_VERSION; type: 'ready'; mapId: string; release: string }
  | { source: typeof EMBED_MESSAGE_SOURCE; version: typeof EMBED_MESSAGE_VERSION; type: 'error'; mapId: string; message: string }
  | { source: typeof EMBED_MESSAGE_SOURCE; version: typeof EMBED_MESSAGE_VERSION; type: 'camera'; mapId: string; camera: EmbedCamera }
  | { source: typeof EMBED_MESSAGE_SOURCE; version: typeof EMBED_MESSAGE_VERSION; type: 'selection'; mapId: string; selection: EmbedSelection | null };

type WithoutEnvelope<T> = T extends EmbedEvent ? Omit<T, 'source' | 'version'> : never;
export type EmbedEventInput = WithoutEnvelope<EmbedEvent>;

export function embedEvent<T extends EmbedEventInput>(event: T): EmbedEvent {
  return { source: EMBED_MESSAGE_SOURCE, version: EMBED_MESSAGE_VERSION, ...event } as EmbedEvent;
}

/** Send the public, versioned event contract to an iframe parent. Payloads contain display state only. */
export function postEmbedEvent(event: EmbedEventInput): void {
  if (window.parent === window) return;
  window.parent.postMessage(embedEvent(event), '*');
}
