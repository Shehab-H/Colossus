import { existsSync, readdirSync, readFileSync, statSync } from 'node:fs';
import { dirname, join, relative, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';

type Layer = 'app' | 'domain' | 'features' | 'infrastructure' | 'shared';

const sourceRoot = fileURLToPath(new URL('.', import.meta.url));
const aliasRoots: Record<string, Layer> = {
  '@app/': 'app',
  '@domain/': 'domain',
  '@features/': 'features',
  '@infrastructure/': 'infrastructure',
  '@shared/': 'shared',
};
const allowed: Record<Layer, ReadonlySet<Layer>> = {
  app: new Set(['app', 'domain', 'features', 'infrastructure', 'shared']),
  domain: new Set(['domain', 'shared']),
  features: new Set(['domain', 'features', 'infrastructure', 'shared']),
  infrastructure: new Set(['domain', 'infrastructure', 'shared']),
  shared: new Set(['shared']),
};

function sourceFiles(dir: string): string[] {
  return readdirSync(dir).flatMap((name) => {
    const path = join(dir, name);
    return statSync(path).isDirectory()
      ? sourceFiles(path)
      : /\.(ts|tsx)$/.test(name) && name !== 'architecture.test.ts'
        ? [path]
        : [];
  });
}

function layerOf(path: string): Layer | null {
  const first = relative(sourceRoot, path).split(sep)[0] as Layer;
  return first in allowed ? first : null;
}

function importedLayer(importer: string, specifier: string): Layer | null {
  const alias = Object.entries(aliasRoots).find(([prefix]) => specifier.startsWith(prefix));
  if (alias) return alias[1];
  return specifier.startsWith('.') ? layerOf(resolve(dirname(importer), specifier)) : null;
}

describe('frontend architecture', () => {
  it('keeps the retired flat scaffold out of src', () => {
    for (const retired of ['App.tsx', 'App.css', 'components', 'hooks', 'lib', 'assets']) {
      expect(existsSync(join(sourceRoot, retired)), `${retired} must stay retired`).toBe(false);
    }
  });

  it('keeps imports pointing inward through the declared layers', () => {
    const violations: string[] = [];
    const importPattern = /(?:import|export)\s+(?:[\s\S]*?\s+from\s+)?['"]([^'"]+)['"]/g;

    for (const file of sourceFiles(sourceRoot)) {
      const source = layerOf(file) ?? (file.endsWith(`${sep}main.tsx`) ? 'app' : null);
      if (!source) continue;

      const text = readFileSync(file, 'utf8');
      for (const match of text.matchAll(importPattern)) {
        const target = importedLayer(file, match[1]);
        if (target && !allowed[source].has(target)) {
          violations.push(`${relative(sourceRoot, file)} (${source}) -> ${match[1]} (${target})`);
        }
      }
    }

    expect(violations).toEqual([]);
  });
});
