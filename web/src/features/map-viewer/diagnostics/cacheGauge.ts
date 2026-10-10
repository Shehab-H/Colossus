/** The in-memory tile cache's live gauge: decoded bytes resident against its budget, and how many tiles
 *  it has evicted. Evictions are the thrash signal — a pan that evicts and immediately re-fetches the
 *  same ground shows up here as a rising count with no new territory covered, and nowhere else. */
export interface CacheGauge {
  resident: number;
  budget: number;
  tiles: number;
  evictions: number;
}
