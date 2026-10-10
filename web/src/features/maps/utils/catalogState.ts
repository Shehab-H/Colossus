export type CatalogContentState = 'loading' | 'error' | 'empty' | 'ready';

export function catalogContentState(itemCount: number | null, hasError: boolean): CatalogContentState {
  if (itemCount === null) return 'loading';
  if (hasError) return 'error';
  return itemCount === 0 ? 'empty' : 'ready';
}
