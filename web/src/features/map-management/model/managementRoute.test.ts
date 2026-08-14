import { describe, expect, it } from 'vitest';
import { resolveManagementRoute } from './managementRoute';

describe('management routes', () => {
  it('reserves the product home and focused management routes', () => {
    expect(resolveManagementRoute('/maps')).toEqual({ kind: 'list' });
    expect(resolveManagementRoute('/maps/new')).toEqual({ kind: 'create' });
    expect(resolveManagementRoute('/maps/manage/abc')).toEqual({ kind: 'detail', mapId: 'abc' });
    expect(resolveManagementRoute('/maps/manage/abc/edit')).toEqual({ kind: 'edit', mapId: 'abc' });
  });

  it('leaves individual viewer paths to the viewer router', () => {
    expect(resolveManagementRoute('/maps/mobile-coverage')).toBeNull();
    expect(resolveManagementRoute('/embed/mobile-coverage')).toBeNull();
  });
});
