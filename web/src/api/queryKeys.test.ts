import { describe, expect, it } from 'vitest';
import { catalogQueryKeys } from './queryKeys';
import { datasetsQuery } from './sources';
import { managedMapQuery, managedMapsQuery } from './maps';

describe('catalog query ownership', () => {
  it('groups map-list variants beneath one invalidation boundary', () => {
    expect(catalogQueryKeys.mapList(false)).toEqual(['catalog', 'maps', { includeArchived: false }]);
    expect(catalogQueryKeys.mapList(true)).toEqual(['catalog', 'maps', { includeArchived: true }]);
    expect(catalogQueryKeys.mapLists()).toEqual(['catalog', 'maps']);
  });

  it('gives list, detail, and dataset resources distinct cache keys', () => {
    expect(managedMapsQuery(false).queryKey).toEqual(catalogQueryKeys.mapList(false));
    expect(managedMapQuery('coverage').queryKey).toEqual(catalogQueryKeys.mapDetail('coverage'));
    expect(datasetsQuery().queryKey).toEqual(catalogQueryKeys.datasets());
  });
});
