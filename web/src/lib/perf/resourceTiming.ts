/** Resource Timing keeps a bounded entry buffer (250 by default). Once full it silently stops recording,
 *  and every later fetch reports "unknown" — a long pan would go blind halfway through. Raise it, and
 *  clear on overflow so measurement resumes rather than degrading without saying so. Called on both the
 *  main thread and the worker: each realm has its own timeline, and tiles are fetched in the worker. */
export function initResourceTiming(): void {
  try {
    performance.setResourceTimingBufferSize?.(5000);
    (performance as unknown as EventTarget).addEventListener?.('resourcetimingbufferfull', () =>
      performance.clearResourceTimings(),
    );
  } catch {
    // Not every realm exposes these; the probes degrade to "unknown", never to a wrong number.
  }
}
