import { recordFrame } from '@/lib/perf/recorder';

/** A frame this long is a visible hitch, not a dropped frame — the stutter the tile pipeline causes. */
export const LONG_FRAME_MS = 50;

/** A rAF loop sampling frame-to-frame deltas — the deck layer-admission hitch shows up here as a long
 *  frame. Only runs while the dashboard is mounted (i.e. ?perf=1), because a permanent rAF loop keeps the
 *  compositor awake and would itself perturb idle-time work like the predictive prefetch. */
export function startFrameProbe(): () => void {
  let last = performance.now();
  let raf = requestAnimationFrame(function tick() {
    const now = performance.now();
    recordFrame(now - last);
    last = now;
    raf = requestAnimationFrame(tick);
  });
  return () => cancelAnimationFrame(raf);
}
