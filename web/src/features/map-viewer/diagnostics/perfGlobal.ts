import { setPerfEnabled, isPerfOn, perfEvents, frameSamples, deckSnapshot, clearPerf } from '@/lib/perf/recorder';
import { stats } from './stats';
import { totals } from './totals';
import { gpuName } from './gpuName';

/** `window.__perf` — the programmatic read. Lets a browser-driving agent (or you, in the console) pull
 *  the same numbers the dashboard shows without scraping the DOM: `__perf.dump()`. */
export function installPerfGlobal(): void {
  if (typeof window === 'undefined') return;
  (window as unknown as Record<string, unknown>).__perf = {
    on: () => setPerfEnabled(true),
    off: () => setPerfEnabled(false),
    isOn: () => isPerfOn(),
    events: () => perfEvents().slice(),
    frames: () => frameSamples().slice(),
    stats: () => stats(),
    totals: () => totals(),
    deck: () => deckSnapshot(),
    gpu: () => gpuName(),
    clear: () => clearPerf(),
    dump: () => ({ stats: stats(), totals: totals(), deck: deckSnapshot(), gpu: gpuName() }),
  };
}
