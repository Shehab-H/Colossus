/** One map in the current registry listing. The durable catalog will evolve this read model without
 * coupling viewer code to its transport. */
export interface ViewSummary {
  id: string;
  title?: string;
  viewport: string;
  mark: string;
  url: string;
  baked: boolean;
}
