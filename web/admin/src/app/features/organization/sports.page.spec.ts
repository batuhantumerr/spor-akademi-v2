import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import { SportsPage } from './sports.page';

describe('SportsPage', () => {
  it('lists sports with their status', async () => {
    TestBed.configureTestingModule({
      imports: [SportsPage],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    const fixture = TestBed.createComponent(SportsPage);
    const backend = TestBed.inject(HttpTestingController);

    backend
      .expectOne((r) => r.url === '/api/v1/sports')
      .flush({
        items: [
          { id: '1', name: 'Futbol', isArchived: false, version: 1 },
          { id: '2', name: 'Yüzme', isArchived: true, version: 3 },
        ],
        page: 1,
        pageSize: 50,
        totalCount: 2,
        totalPages: 1,
      });
    await fixture.whenStable();

    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text).toContain('Futbol');
    expect(text).toContain('Arşivde');
    backend.verify();
  });

  it('shows a Turkish message when loading fails', async () => {
    TestBed.configureTestingModule({
      imports: [SportsPage],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    const fixture = TestBed.createComponent(SportsPage);
    const backend = TestBed.inject(HttpTestingController);

    backend.expectOne((r) => r.url === '/api/v1/sports').flush(null, { status: 0, statusText: '' });
    await fixture.whenStable();

    expect((fixture.nativeElement as HTMLElement).textContent).toContain('Sunucuya ulaşılamıyor');
  });
});
