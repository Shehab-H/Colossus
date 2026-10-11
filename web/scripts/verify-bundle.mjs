import { readFile, stat } from 'node:fs/promises';
import path from 'node:path';

const root = path.resolve(import.meta.dirname, '..');
const output = path.join(root, 'dist');
const manifest = JSON.parse(await readFile(path.join(output, '.vite', 'manifest.json'), 'utf8'));
const viewerKey = 'src/app/routes/viewerRoutes.tsx';
const entryKey = Object.keys(manifest).find((key) => manifest[key]?.isEntry);
const entry = manifest[entryKey];

if (!entryKey || !entry?.isEntry) throw new Error('Vite manifest is missing the application entry.');
if (!manifest[viewerKey]?.isDynamicEntry) throw new Error('The map viewer is no longer a dynamic route entry.');
if (!(entry.dynamicImports ?? []).includes(viewerKey)) {
  throw new Error('The application entry no longer exposes the map viewer as a dynamic route.');
}

const settled = new Set();
const trail = [];
const rejectCycles = (key) => {
  const start = trail.indexOf(key);
  if (start !== -1) {
    const cycle = [...trail.slice(start), key].map((part) => manifest[part]?.file ?? part);
    throw new Error(`Static chunk imports form a cycle: ${cycle.join(' -> ')}`);
  }
  if (settled.has(key)) return;
  trail.push(key);
  for (const dependency of manifest[key]?.imports ?? []) rejectCycles(dependency);
  trail.pop();
  settled.add(key);
};
for (const key of Object.keys(manifest)) rejectCycles(key);

const staticKeys = new Set();
const visitStatic = (key) => {
  if (staticKeys.has(key)) return;
  staticKeys.add(key);
  for (const dependency of manifest[key]?.imports ?? []) visitStatic(dependency);
};
visitStatic(entryKey);

if (staticKeys.has(viewerKey)) throw new Error('The map viewer leaked into the initial application graph.');

let entryBytes = 0;
for (const key of staticKeys) {
  const file = manifest[key]?.file;
  if (file?.endsWith('.js')) entryBytes += (await stat(path.join(output, file))).size;
}

const entryBudget = 450 * 1024;
if (entryBytes > entryBudget) {
  throw new Error(`Initial JavaScript is ${(entryBytes / 1024).toFixed(1)} KiB; budget is ${entryBudget / 1024} KiB.`);
}

console.log(`Bundle contract passed: ${(entryBytes / 1024).toFixed(1)} KiB initial JS; viewer remains route-lazy.`);
