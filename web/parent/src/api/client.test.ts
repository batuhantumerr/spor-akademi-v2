import { describe, expect, it, vi } from 'vitest';
import { api, type components } from './client';

describe('api client', () => {
  it('calls the typed path on the page origin and parses the typed body', async () => {
    // Synthetic user; the shape comes from the generated schema, so a contract change breaks typecheck.
    const user: components['schemas']['UserSummary'] = {
      id: '0190a1b2-0000-7000-8000-000000000001',
      email: 'kullanici@example.test',
      firstName: 'Deneme',
      lastName: 'Kullanıcı',
      role: 'SchoolAdmin',
      tenantId: '0190a1b2-0000-7000-8000-000000000002',
      mustChangePassword: false,
    };
    const fetch = vi.fn((request: Request) => {
      expect(request.method).toBe('GET');
      expect(new URL(request.url).origin).toBe(window.location.origin);
      expect(new URL(request.url).pathname).toBe('/api/v1/me');
      return Promise.resolve(Response.json(user));
    });

    const { data } = await api.GET('/api/v1/me', { fetch });

    expect(data?.email).toBe('kullanici@example.test');
    expect(fetch).toHaveBeenCalledOnce();
  });
});
