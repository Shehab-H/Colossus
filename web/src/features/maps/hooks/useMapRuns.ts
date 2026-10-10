import { useInfiniteQuery } from '@tanstack/react-query';
import { ACTIVE_RUN_POLL_MS, mapRunsQuery } from '@/api/bakes';
import type { BakeRunStatus } from '@/types/bake';

export function useMapRuns(mapId: string, statuses: readonly BakeRunStatus[], live: boolean) {
  const query = useInfiniteQuery({
    ...mapRunsQuery(mapId, statuses),
    refetchInterval: live ? ACTIVE_RUN_POLL_MS : false,
  });
  const runs = query.data?.pages.flatMap((page) => page.runs) ?? [];
  return { ...query, runs };
}
