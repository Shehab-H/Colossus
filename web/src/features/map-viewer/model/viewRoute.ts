/** Compatibility route state until the product map routes in saga item 03 replace `?view=`. */
export const viewIdFromSearch = (search: string): string | null => new URLSearchParams(search).get('view');

export const urlViewId = (): string | null => viewIdFromSearch(window.location.search);

export function urlWithViewId(href: string, id: string): string {
  const url = new URL(href);
  url.searchParams.set('view', id);
  return url.toString();
}

export function setUrlViewId(id: string): void {
  window.history.replaceState(null, '', urlWithViewId(window.location.href, id));
}
