import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { SportDto } from '../../api/models';
import { errorMessages, ui } from '../../core/i18n/tr';
import { ConfirmService } from '../../shared/ui/confirm-dialog.component';
import { SportsPage } from './sports.page';

const futbol: SportDto = { id: '1', name: 'Futbol', isArchived: false, version: 1 };
const yuzme: SportDto = { id: '2', name: 'Yüzme', isArchived: true, version: 3 };

const pageOf = (items: SportDto[], totalPages = 1) => ({
  items,
  page: 1,
  pageSize: 50,
  totalCount: items.length,
  totalPages,
});

/** Lets awaited API promises resolve and re-renders. */
async function settle(fixture: ComponentFixture<unknown>): Promise<void> {
  await new Promise((resolve) => setTimeout(resolve));
  fixture.detectChanges();
}

describe('SportsPage', () => {
  let fixture: ComponentFixture<SportsPage>;
  let backend: HttpTestingController;
  let root: HTMLElement;
  const confirm = vi.fn<() => Promise<boolean>>();

  beforeEach(() => {
    confirm.mockReset().mockResolvedValue(true);
    TestBed.configureTestingModule({
      imports: [SportsPage],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: ConfirmService, useValue: { confirm } },
      ],
    });
    fixture = TestBed.createComponent(SportsPage);
    backend = TestBed.inject(HttpTestingController);
    root = fixture.nativeElement as HTMLElement;
    document.body.appendChild(root);
  });

  afterEach(() => {
    root.remove();
    backend.verify();
  });

  const listRequest = () => backend.expectOne((r) => r.url === '/api/v1/sports');
  const byLabel = (label: string) =>
    root.querySelector(`button[aria-label="${label}"]`) as HTMLButtonElement;
  const renameInput = () => root.querySelector('#rename-1') as HTMLInputElement;

  async function loadWith(items: SportDto[]): Promise<void> {
    listRequest().flush(pageOf(items));
    await settle(fixture);
  }

  async function type(input: HTMLInputElement, value: string): Promise<void> {
    input.value = value;
    input.dispatchEvent(new Event('input'));
    await settle(fixture);
  }

  it('lists sports with their status', async () => {
    await loadWith([futbol, yuzme]);
    expect(root.textContent).toContain('Futbol');
    expect(root.textContent).toContain('Arşivde');
  });

  it('shows a Turkish message when loading fails', async () => {
    listRequest().flush(null, { status: 0, statusText: '' });
    await settle(fixture);
    expect(root.textContent).toContain('Sunucuya ulaşılamıyor');
  });

  it('tells the user about the archived filter when no active sport is left', async () => {
    await loadWith([]);
    expect(root.textContent).toContain(ui.sports.emptyActive);
  });

  describe('rename', () => {
    beforeEach(async () => {
      await loadWith([futbol]);
      byLabel(`${ui.actions.rename}: Futbol`).click();
      await settle(fixture);
    });

    it('sends the new name with the row version', async () => {
      await type(renameInput(), 'Futbol (U12)');
      renameInput().form!.requestSubmit();
      await settle(fixture);

      const put = backend.expectOne('/api/v1/sports/1');
      expect(put.request.method).toBe('PUT');
      expect(put.request.body).toEqual({ name: 'Futbol (U12)', version: 1 });
      put.flush(null);
      await settle(fixture);
      listRequest().flush(pageOf([{ ...futbol, name: 'Futbol (U12)', version: 2 }]));
      await settle(fixture);

      expect(renameInput()).toBeNull();
      expect(root.textContent).toContain('Futbol (U12)');
    });

    it('validates on the client before calling the API', async () => {
      await type(renameInput(), '   ');
      renameInput().form!.requestSubmit();
      await settle(fixture);

      backend.expectNone('/api/v1/sports/1');
      expect(renameInput().getAttribute('aria-invalid')).toBe('true');
      expect(root.querySelector('#rename-error')?.textContent).toContain(
        errorMessages['Field.Required'],
      );
    });

    it('reloads on a version conflict so the retry uses the fresh version', async () => {
      await type(renameInput(), 'Futbol A');
      renameInput().form!.requestSubmit();
      await settle(fixture);
      backend
        .expectOne('/api/v1/sports/1')
        .flush({ code: 'Concurrency.Conflict' }, { status: 409, statusText: 'Conflict' });
      await settle(fixture);
      listRequest().flush(pageOf([{ ...futbol, name: 'Futbol B', version: 5 }]));
      await settle(fixture);

      expect(root.querySelector('#rename-error')?.textContent).toContain(
        ui.common.conflictReloaded,
      );
      expect(renameInput().value).toBe('Futbol A');

      renameInput().form!.requestSubmit();
      await settle(fixture);
      const retry = backend.expectOne('/api/v1/sports/1');
      expect(retry.request.body).toEqual({ name: 'Futbol A', version: 5 });
      retry.flush(null);
      await settle(fixture);
      listRequest().flush(pageOf([{ ...futbol, name: 'Futbol A', version: 6 }]));
      await settle(fixture);
    });

    it('cancels with Escape and returns focus to the rename button', async () => {
      renameInput().dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
      await settle(fixture);

      expect(renameInput()).toBeNull();
      expect(document.activeElement).toBe(byLabel(`${ui.actions.rename}: Futbol`));
    });
  });

  it('ignores a second archive click while the first is running', async () => {
    await loadWith([futbol]);
    let release!: (value: boolean) => void;
    confirm.mockReturnValueOnce(new Promise((resolve) => (release = resolve)));

    byLabel(`${ui.actions.archive}: Futbol`).click();
    await settle(fixture);
    expect(byLabel(`${ui.actions.archive}: Futbol`).disabled).toBe(true);
    byLabel(`${ui.actions.archive}: Futbol`).click();
    expect(confirm).toHaveBeenCalledTimes(1);

    release(false);
    await settle(fixture);
    backend.expectNone('/api/v1/sports/1/archive');
  });

  it('goes back to the last page when an archive empties the current one', async () => {
    listRequest().flush(pageOf([futbol], 2));
    await settle(fixture);
    // Navigate to page 2, which holds one sport.
    byLabel(ui.common.nextPage).click();
    await settle(fixture);
    backend
      .expectOne((r) => r.url === '/api/v1/sports' && r.params.get('page') === '2')
      .flush({
        ...pageOf([{ ...futbol, id: '9', name: 'Tenis' }], 2),
        page: 2,
      });
    await settle(fixture);

    byLabel(`${ui.actions.archive}: Tenis`).click();
    await settle(fixture);
    backend.expectOne('/api/v1/sports/9/archive').flush(null);
    await settle(fixture);
    backend
      .expectOne((r) => r.url === '/api/v1/sports' && r.params.get('page') === '2')
      .flush({ ...pageOf([], 1), page: 2 });
    await settle(fixture);
    backend
      .expectOne((r) => r.url === '/api/v1/sports' && r.params.get('page') === '1')
      .flush(pageOf([futbol], 1));
    await settle(fixture);

    expect(root.textContent).toContain('Futbol');
  });
});
