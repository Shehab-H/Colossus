export type ManagementRoute =
  | { kind: 'list' }
  | { kind: 'create' }
  | { kind: 'detail'; mapId: string }
  | { kind: 'edit'; mapId: string };

export function resolveManagementRoute(pathname: string): ManagementRoute | null {
  const path = pathname.replace(/\/+$/, '') || '/';
  if (path === '/maps') return { kind: 'list' };
  if (path === '/maps/new') return { kind: 'create' };
  const match = path.match(/^\/maps\/manage\/([^/]+)(?:\/(edit))?$/);
  if (!match) return null;
  return match[2]
    ? { kind: 'edit', mapId: decodeURIComponent(match[1]) }
    : { kind: 'detail', mapId: decodeURIComponent(match[1]) };
}
