import { HttpEventType, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { StudentDocumentDto } from '../../api/models';
import { errorMessages, ui } from '../../core/i18n/tr';
import { ConfirmService } from '../../shared/ui/confirm-dialog.component';
import { ToastService } from '../../shared/ui/toast.service';
import { acceptFor, checkFile, detectFormat, formatSize } from './document-files';
import { StudentDocumentsComponent } from './student-documents.component';

const LIST = '/api/v1/students/st1/documents';
const PHOTO = '/api/v1/students/st1/photo';

const pdfBytes = new TextEncoder().encode('%PDF-1.7\n%%EOF\n');
const jpegBytes = new Uint8Array([0xff, 0xd8, 0xff, 0xe0, 0x00, 0x10]);
const svgBytes = new TextEncoder().encode('<svg xmlns="http://www.w3.org/2000/svg"></svg>');

const file = (bytes: Uint8Array, name: string, type = 'application/octet-stream') =>
  new File([bytes as BlobPart], name, { type });

const doc = (id: string, overrides: Partial<StudentDocumentDto> = {}): StudentDocumentDto => ({
  id,
  studentId: 'st1',
  type: 'HealthReport',
  contentType: 'application/pdf',
  fileName: `rapor-${id}.pdf`,
  sizeBytes: 1536,
  uploadedBy: 'u1',
  uploadedAt: '2027-01-14T09:00:00Z',
  ...overrides,
});

async function settle(fixture: ComponentFixture<unknown>): Promise<void> {
  for (let i = 0; i < 3; i++) {
    await new Promise((resolve) => setTimeout(resolve));
  }
  fixture.detectChanges();
}

describe('document-files', () => {
  it('detects formats by magic bytes, not by name', () => {
    expect(detectFormat(pdfBytes)).toBe('Pdf');
    expect(detectFormat(jpegBytes)).toBe('Jpeg');
    expect(detectFormat(new Uint8Array([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]))).toBe(
      'Png',
    );
    expect(detectFormat(new TextEncoder().encode('RIFF\0\0\0\0WEBPVP8 '))).toBe('WebP');
    expect(detectFormat(svgBytes)).toBeNull();
    expect(detectFormat(new TextEncoder().encode('GIF89a'))).toBeNull();
  });

  it('gives the code the API would return', async () => {
    expect(await checkFile('HealthReport', file(pdfBytes, 'rapor.pdf'))).toBeNull();
    expect(await checkFile('Photo', file(jpegBytes, 'foto.JPEG'))).toBeNull();
    expect(await checkFile('Photo', file(svgBytes, 'foto.png', 'image/png'))).toBe(
      'Document.UnsupportedType',
    );
    expect(await checkFile('Photo', file(pdfBytes, 'rapor.pdf'))).toBe('Document.UnsupportedType');
    expect(await checkFile('Photo', file(jpegBytes, 'foto.jpg.html'))).toBe(
      'Document.ExtensionMismatch',
    );
    expect(await checkFile('Photo', file(new Uint8Array(), 'bos.jpg'))).toBe('Document.Empty');
    const big = new Uint8Array(2 * 1024 * 1024 + 1);
    big.set(jpegBytes);
    expect(await checkFile('Photo', file(big, 'buyuk.jpg'))).toBe('Document.TooLarge');
    expect(await checkFile('HealthReport', file(big, 'buyuk.jpg'))).toBeNull();
  });

  it('formats sizes and accept lists', () => {
    expect(formatSize(1536)).toBe('2 KB');
    expect(formatSize('1572864')).toBe('1,5 MB');
    expect(acceptFor('Photo')).toContain('image/webp');
    expect(acceptFor('Photo')).not.toContain('pdf');
    expect(acceptFor('HealthReport')).toContain('.pdf');
  });
});

describe('StudentDocumentsComponent', () => {
  let fixture: ComponentFixture<StudentDocumentsComponent>;
  let backend: HttpTestingController;
  let root: HTMLElement;
  const toast = { success: vi.fn(), error: vi.fn() };
  const confirm = vi.fn<() => Promise<boolean>>();

  async function create(list: StudentDocumentDto[]): Promise<void> {
    fixture = TestBed.createComponent(StudentDocumentsComponent);
    fixture.componentRef.setInput('studentId', 'st1');
    root = fixture.nativeElement as HTMLElement;
    await settle(fixture);
    backend.expectOne(LIST).flush(list);
    await settle(fixture);
  }

  async function choose(type: 'HealthReport' | 'Photo', selected: File): Promise<void> {
    const radio = root.querySelector<HTMLInputElement>(`input[type=radio][value=${type}]`)!;
    radio.click();
    const input = root.querySelector<HTMLInputElement>('#document-file')!;
    Object.defineProperty(input, 'files', { value: [selected], configurable: true });
    input.dispatchEvent(new Event('change'));
    await settle(fixture);
  }

  const submit = async () => {
    root.querySelector<HTMLFormElement>('form')!.dispatchEvent(new Event('submit'));
    await settle(fixture);
  };

  beforeEach(() => {
    toast.success.mockReset();
    toast.error.mockReset();
    confirm.mockReset().mockResolvedValue(true);
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: ToastService, useValue: toast },
        { provide: ConfirmService, useValue: { confirm } },
      ],
    });
    backend = TestBed.inject(HttpTestingController);
  });

  afterEach(() => backend.verify());

  it('offers no upload for an anonymized student', async () => {
    fixture = TestBed.createComponent(StudentDocumentsComponent);
    fixture.componentRef.setInput('studentId', 'st1');
    fixture.componentRef.setInput('anonymized', true);
    root = fixture.nativeElement as HTMLElement;
    await settle(fixture);
    backend.expectOne(LIST).flush([]);
    await settle(fixture);

    expect(root.querySelector('form')).toBeNull();
    expect(root.querySelector('#document-file')).toBeNull();
    expect(root.querySelector('[data-testid=upload-closed]')?.textContent).toContain(
      ui.documents.anonymizedNoUpload,
    );
  });

  it('lists the documents and shows the photo through a fresh signed link', async () => {
    await create([doc('p1', { type: 'Photo', fileName: 'foto.jpg' }), doc('d1')]);
    backend
      .expectOne(PHOTO)
      .flush({ url: 'http://storage.test/b/k?X-Amz-Signature=x', expiresAt: '' });
    await settle(fixture);

    const items = root.querySelectorAll('[data-testid=document-list] li');
    expect(items.length).toBe(2);
    expect(items[1].textContent).toContain('rapor-d1.pdf');
    expect(items[1].textContent).toContain(ui.documents.types.HealthReport);
    expect(items[1].textContent).toContain('2 KB');
    const img = root.querySelector<HTMLImageElement>('[data-testid=student-photo]')!;
    expect(img.src).toBe('http://storage.test/b/k?X-Amz-Signature=x');
    expect(img.alt).toBe(ui.documents.photoAlt);
  });

  it('keeps the list when only the photo link fails', async () => {
    await create([doc('p1', { type: 'Photo', fileName: 'foto.jpg' }), doc('d1')]);
    backend
      .expectOne(PHOTO)
      .flush({ code: 'Unexpected.Error' }, { status: 500, statusText: 'Server Error' });
    await settle(fixture);

    expect(root.querySelectorAll('[data-testid=document-list] li').length).toBe(2);
    expect(root.querySelector('[role=alert]')).toBeNull();
    expect(root.querySelector('[data-testid=no-photo]')).not.toBeNull();
    expect(root.querySelector('[data-testid=photo-error]')?.textContent).toContain(
      ui.documents.photoUnavailable,
    );
  });

  it('announces upload progress only in 25% steps', async () => {
    await create([]);
    await choose('HealthReport', file(pdfBytes, 'rapor.pdf'));
    await submit();
    const request = backend.expectOne((r) => r.method === 'POST');
    const live = () => root.querySelector('[data-testid=upload-live]')?.textContent?.trim();

    request.event({ type: HttpEventType.UploadProgress, loaded: 10, total: 100 });
    await settle(fixture);
    expect(live()).toContain('%0');
    request.event({ type: HttpEventType.UploadProgress, loaded: 37, total: 100 });
    await settle(fixture);
    expect(live()).toContain('%25');
    expect(root.textContent).toContain('%37');
    request.flush(doc('d1'), { status: 201, statusText: 'Created' });
    await settle(fixture);
    backend.expectOne(LIST).flush([doc('d1')]);
    await settle(fixture);
  });

  it('cancels an upload in flight when the student changes', async () => {
    await create([]);
    await choose('HealthReport', file(pdfBytes, 'rapor.pdf'));
    await submit();
    const request = backend.expectOne((r) => r.method === 'POST');

    fixture.componentRef.setInput('studentId', 'st2');
    await settle(fixture);

    expect(request.cancelled).toBe(true);
    backend.expectOne('/api/v1/students/st2/documents').flush([]);
    await settle(fixture);
    expect(root.querySelector('progress')).toBeNull();
    expect(toast.success).not.toHaveBeenCalled();
  });

  it('shows a placeholder and an empty list without asking for a photo link', async () => {
    await create([]);

    expect(root.querySelector('[data-testid=no-photo]')).not.toBeNull();
    expect(root.textContent).toContain(ui.documents.empty);
    backend.expectNone(PHOTO);
  });

  it('refuses a disguised or oversized file before uploading', async () => {
    await create([]);

    await choose('Photo', file(svgBytes, 'foto.png', 'image/png'));
    expect(root.querySelector('#document-error')?.textContent).toContain(
      errorMessages['Document.UnsupportedType'],
    );
    await submit();
    backend.expectNone(LIST);

    const big = new Uint8Array(2 * 1024 * 1024 + 1);
    big.set(jpegBytes);
    await choose('Photo', file(big, 'buyuk.jpg'));
    expect(root.querySelector('#document-error')?.textContent).toContain(
      errorMessages['Document.TooLarge'],
    );
    // The same file is fine as a health report (5 MB limit).
    await choose('HealthReport', file(big, 'buyuk.jpg'));
    expect(root.querySelector('#document-error')).toBeNull();
  });

  it('uploads with progress, then reloads the list', async () => {
    await create([]);
    await choose('HealthReport', file(pdfBytes, 'Sağlık raporu.pdf', 'application/pdf'));

    await submit();
    const request = backend.expectOne((r) => r.method === 'POST' && r.url === LIST);
    const body = request.request.body as FormData;
    expect(body.get('type')).toBe('HealthReport');
    expect((body.get('file') as File).name).toBe('Sağlık raporu.pdf');
    request.event({ type: HttpEventType.UploadProgress, loaded: 50, total: 100 });
    await settle(fixture);
    expect(root.querySelector('progress')?.getAttribute('value')).toBe('50');
    expect(root.textContent).toContain('%50');

    request.flush(doc('d1'), { status: 201, statusText: 'Created' });
    await settle(fixture);
    expect(toast.success).toHaveBeenCalledWith(ui.documents.uploaded);
    backend.expectOne(LIST).flush([doc('d1')]);
    await settle(fixture);
    expect(root.querySelector('progress')).toBeNull();
    expect(root.querySelectorAll('[data-testid=document-list] li').length).toBe(1);
  });

  it("shows the API's reason when it refuses the file", async () => {
    await create([]);
    await choose('HealthReport', file(pdfBytes, 'rapor.pdf'));

    await submit();
    backend
      .expectOne((r) => r.method === 'POST')
      .flush(
        { code: 'Document.ActiveContent', status: 400 },
        { status: 400, statusText: 'Bad Request' },
      );
    await settle(fixture);

    expect(root.querySelector('#document-error')?.textContent).toContain(
      errorMessages['Document.ActiveContent'],
    );
    expect(toast.success).not.toHaveBeenCalled();
  });

  it('asks before deleting and deletes on confirm', async () => {
    await create([doc('d1')]);
    const remove = root.querySelector<HTMLButtonElement>('button[aria-label^="Sil"]')!;

    confirm.mockResolvedValueOnce(false);
    remove.click();
    await settle(fixture);
    backend.expectNone((r) => r.method === 'DELETE');

    remove.click();
    await settle(fixture);
    backend.expectOne((r) => r.method === 'DELETE' && r.url === '/api/v1/documents/d1').flush(null);
    await settle(fixture);
    expect(toast.success).toHaveBeenCalledWith(ui.documents.removed);
    backend.expectOne(LIST).flush([]);
    await settle(fixture);
    expect(root.textContent).toContain(ui.documents.empty);
  });

  it('opens a document in a new tab without giving it access to this page', async () => {
    await create([doc('d1')]);
    const tab = { opener: {} as unknown, location: { href: '' }, close: vi.fn() };
    const open = vi.spyOn(window, 'open').mockReturnValue(tab as unknown as Window);

    root.querySelector<HTMLButtonElement>('button[aria-label^="Aç"]')!.click();
    await settle(fixture);
    backend
      .expectOne('/api/v1/documents/d1/link')
      .flush({ url: 'http://storage.test/b/k?sig', expiresAt: '' });
    await settle(fixture);

    expect(open).toHaveBeenCalledWith('', '_blank');
    expect(tab.opener).toBeNull();
    expect(tab.location.href).toBe('http://storage.test/b/k?sig');
    open.mockRestore();
  });
});
