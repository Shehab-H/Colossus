import { describe, expect, it } from 'vitest';
import { urlWithViewId, viewIdFromSearch } from './viewRoute';

describe('legacy viewer bootstrap URL', () => {
  it('reads the selected map from the query string', () => {
    expect(viewIdFromSearch('?theme=dark&view=mobile-coverage&z=4.8')).toBe('mobile-coverage');
    expect(viewIdFromSearch('?theme=dark')).toBeNull();
  });

  it('changes only the selected map and preserves the rest of the viewer state', () => {
    const next = new URL(urlWithViewId('https://maps.example/?view=old&theme=light&z=4', 'geonames'));
    expect(next.searchParams.get('view')).toBe('geonames');
    expect(next.searchParams.get('theme')).toBe('light');
    expect(next.searchParams.get('z')).toBe('4');
  });
});
