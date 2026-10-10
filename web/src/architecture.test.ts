import { existsSync, readdirSync, readFileSync, statSync } from 'node:fs';
import { dirname, join, relative, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';

const sourceRoot = fileURLToPath(new URL('.', import.meta.url));
const projectRoot = resolve(sourceRoot, '..');
const sharedAreas = ['api', 'types', 'components', 'hooks', 'lib', 'config', 'styles', 'utils'];
const retiredAreas = ['domain', 'infrastructure', 'entities', 'widgets', 'pages', 'shared', 'renderer'];
const retiredSegments = ['model'];
const importPattern = /(?:\bfrom\s*|\bimport\s*\(\s*|\bimport\s+)(['"])([^'"]+)\1/g;

interface Edge {
  file: string;
  specifier: string;
  target: string;
}

function sourceFiles(dir: string): string[] {
  return readdirSync(dir).flatMap((name) => {
    const path = join(dir, name);
    if (statSync(path).isDirectory()) return name === 'node_modules' ? [] : sourceFiles(path);
    return /\.(ts|tsx)$/.test(name) && !path.endsWith('architecture.test.ts') ? [path] : [];
  });
}

function directories(dir: string): string[] {
  return readdirSync(dir).flatMap((name) => {
    const path = join(dir, name);
    return statSync(path).isDirectory() ? [path, ...directories(path)] : [];
  });
}

function posix(path: string): string {
  return path.split(sep).join('/');
}

function resolveSpecifier(importer: string, specifier: string): string | null {
  if (specifier.startsWith('@/')) return posix(join('src', specifier.slice(2)));
  if (specifier.startsWith('.')) return posix(relative(projectRoot, resolve(dirname(importer), specifier)));
  return null;
}

function collectEdges(): { edges: Edge[]; externals: { file: string; specifier: string }[] } {
  const edges: Edge[] = [];
  const externals: { file: string; specifier: string }[] = [];
  for (const absolute of [...sourceFiles(sourceRoot), ...sourceFiles(join(projectRoot, '.storybook'))]) {
    const file = posix(relative(projectRoot, absolute));
    for (const match of readFileSync(absolute, 'utf8').matchAll(importPattern)) {
      const target = resolveSpecifier(absolute, match[2]);
      if (target?.startsWith('src/')) edges.push({ file, specifier: match[2], target });
      else if (!target) externals.push({ file, specifier: match[2] });
    }
  }
  return { edges, externals };
}

const area = (path: string): string => path.split('/')[1] ?? '';
const featureOf = (path: string): string | null => (path.startsWith('src/features/') ? path.split('/')[2] : null);
const isStyle = (specifier: string): boolean => /\.s?css$/.test(specifier);
const isIndex = (target: string, feature: string): boolean => target === `src/features/${feature}` || target === `src/features/${feature}/index`;
const { edges, externals } = collectEdges();
const report = (found: Edge[]): string[] => found.map((edge) => `${edge.file} -> ${edge.specifier}`);

describe('frontend architecture', () => {
  it('keeps the retired layer folders out of src', () => {
    for (const retired of retiredAreas) {
      expect(existsSync(join(sourceRoot, retired)), `${retired} must stay retired`).toBe(false);
    }
    const segments = directories(sourceRoot).filter((dir) => retiredSegments.includes(dir.split(sep).pop() ?? ''));
    expect(segments.map((dir) => posix(relative(projectRoot, dir)))).toEqual([]);
  });

  it('never lets a feature import another feature', () => {
    const violations = edges.filter((edge) => {
      const from = featureOf(edge.file);
      const to = featureOf(edge.target);
      return from !== null && to !== null && from !== to;
    });
    expect(report(violations)).toEqual([]);
  });

  it('never lets shared folders import features or app', () => {
    const violations = edges.filter((edge) => {
      const from = edge.file.startsWith('src/') ? area(edge.file) : '';
      return sharedAreas.includes(from) && (area(edge.target) === 'features' || area(edge.target) === 'app');
    });
    expect(report(violations)).toEqual([]);
  });

  it('reaches into a feature only through its index', () => {
    const violations = edges.filter((edge) => {
      const to = featureOf(edge.target);
      return to !== null && featureOf(edge.file) !== to && !isIndex(edge.target, to) && !isStyle(edge.specifier);
    });
    expect(report(violations)).toEqual([]);
  });

  it('reaches into the engine only through its index', () => {
    const violations = edges.filter((edge) =>
      edge.file.startsWith('src/features/map-viewer/') &&
      !edge.file.startsWith('src/features/map-viewer/engine/') &&
      edge.target.startsWith('src/features/map-viewer/engine/') &&
      edge.target !== 'src/features/map-viewer/engine/index');
    expect(report(violations)).toEqual([]);
  });

  it('keeps the engine free of react, routing and components', () => {
    const forbidden = /^(react|react-dom|react-router|react-map-gl|@deck\.gl\/react|@tanstack\/react-query)(\/|$)/;
    const outsiders = externals.filter((item) => item.file.startsWith('src/features/map-viewer/engine/') && forbidden.test(item.specifier));
    const components = edges.filter((edge) => edge.file.startsWith('src/features/map-viewer/engine/') && area(edge.target) === 'components');
    expect([...outsiders.map((item) => `${item.file} -> ${item.specifier}`), ...report(components)]).toEqual([]);
  });
});
