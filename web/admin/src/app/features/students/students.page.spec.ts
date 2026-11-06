import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import { StudentListItemDto } from '../../api/models';
import { ui } from '../../core/i18n/tr';
import { SEARCH_DEBOUNCE_MS, StudentsPage } from './students.page';

const ayse: StudentListItemDto = {
  id: 's1',
  firstName: 'Ayşe',
  lastName: 'Yılmaz',
  birthDate: '2014-05-02',
  gender: 'Female',
  status: 'Active',
  leftAt: null,
};

const pageOf = (items: StudentListItemDto[]) => ({
  items,
  page: 1,
  pageSize: 25,
  totalCount: items.length,
  totalPages: 1,
});

const wait = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));

async function settle(fixture: ComponentFixture<unknown>): Promise<void> {
  await wait(0);
  fixture.detectChanges();
}

describe('StudentsPage', () => {
  let fixture: ComponentFixture<StudentsPage>;
  let backend: HttpTestingController;
  let root: HTMLElement;

  beforeEach(async () => {
    TestBed.configureTestingModule({
      imports: [StudentsPage],
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    });
    fixture = TestBed.createComponent(StudentsPage);
    backend = TestBed.inject(HttpTestingController);
    root = fixture.nativeElement as HTMLElement;
    await settle(fixture);
    // Filter options load once; they do not matter here.
    backend.match((r) => r.url === '/api/v1/sports' || r.url === '/api/v1/groups');
  });

  afterEach(() => backend.verify());

  const students = () => backend.match((r) => r.url === '/api/v1/students');
  const search = () => root.querySelector('#student-search') as HTMLInputElement;
  async function type(value: string): Promise<void> {
    search().value = value;
    search().dispatchEvent(new Event('input'));
    await settle(fixture);
  }

  it('lists students with birth date, age, gender and status', async () => {
    const [first] = students();
    expect(first.request.params.get('status')).toBe('Active');
    expect(first.request.params.get('sort')).toBe('lastName');
    first.flush(pageOf([ayse]));
    await settle(fixture);

    expect(root.textContent).toContain('Ayşe Yılmaz');
    expect(root.textContent).toContain('02.05.2014');
    expect(root.textContent).toMatch(/\(\d+ yaş\)/);
    expect(root.textContent).toContain('Kız');
    expect(root.textContent).toContain('Aktif');
  });

  it('waits for a typing pause and sends only the last search', async () => {
    students()[0].flush(pageOf([ayse]));
    await settle(fixture);

    await type('ay');
    await type('ayş');
    await type('ayşe ');
    expect(students()).toEqual([]);

    await wait(SEARCH_DEBOUNCE_MS + 50);
    const requests = students();
    expect(requests.length).toBe(1);
    expect(requests[0].request.params.get('search')).toBe('ayşe');
    expect(requests[0].request.params.get('page')).toBe('1');
    requests[0].flush(pageOf([]));
    await settle(fixture);

    expect(root.textContent).toContain(ui.students.emptyFiltered);
    expect(root.querySelector('[role="status"]')?.textContent).toContain('0 öğrenci bulundu.');
  });

  it('does not search again when only spaces were added', async () => {
    students()[0].flush(pageOf([ayse]));
    await settle(fixture);
    await type('ayşe');
    await wait(SEARCH_DEBOUNCE_MS + 50);
    students()[0].flush(pageOf([ayse]));
    await settle(fixture);

    await type('ayşe  ');
    await wait(SEARCH_DEBOUNCE_MS + 50);
    expect(students()).toEqual([]);
  });

  it('applies the status filter and sort', async () => {
    students()[0].flush(pageOf([ayse]));
    await settle(fixture);
    const status = root.querySelector('#student-status') as HTMLSelectElement;
    status.value = 'Left';
    status.dispatchEvent(new Event('change'));
    await settle(fixture);
    const [byStatus] = students();
    expect(byStatus.request.params.get('status')).toBe('Left');
    byStatus.flush(pageOf([]));
    await settle(fixture);

    const sort = root.querySelector('#student-sort') as HTMLSelectElement;
    sort.value = '-createdAt';
    sort.dispatchEvent(new Event('change'));
    await settle(fixture);
    const [bySort] = students();
    expect(bySort.request.params.get('sort')).toBe('-createdAt');
    bySort.flush(pageOf([]));
    await settle(fixture);
  });

  it('shows the unfiltered empty state', async () => {
    const status = root.querySelector('#student-status') as HTMLSelectElement;
    students()[0].flush(pageOf([]));
    await settle(fixture);
    status.value = '';
    status.dispatchEvent(new Event('change'));
    await settle(fixture);
    students()[0].flush(pageOf([]));
    await settle(fixture);
    expect(root.textContent).toContain(ui.students.empty);
  });
});
