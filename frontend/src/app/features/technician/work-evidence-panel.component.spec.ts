import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { environment } from '../../../environments/environment';
import { WorkEvidenceItem } from '../work-orders/work-evidence.models';
import { WorkEvidencePanelComponent } from './work-evidence-panel.component';

describe('WorkEvidencePanelComponent', () => {
  let httpMock: HttpTestingController;
  const url = `${environment.apiBaseUrl}/v1/work-orders/wo-1/evidence`;

  const item = (overrides: Partial<WorkEvidenceItem>): WorkEvidenceItem => ({
    evidenceId: 'ev-1',
    serviceVisitId: 'visit-1',
    evidenceType: 'AFTER_WORK',
    fileName: 'after.png',
    mimeType: 'image/png',
    sizeBytes: 2048,
    scanStatus: 'PENDING',
    bound: false,
    workSummaryId: null,
    slotNo: null,
    uploadedAt: '2026-10-01T08:00:00Z',
    ...overrides,
  });

  function create(listed: WorkEvidenceItem[] = []): { fixture: ComponentFixture<WorkEvidencePanelComponent>; root: HTMLElement } {
    TestBed.configureTestingModule({ imports: [WorkEvidencePanelComponent], providers: [provideHttpClient(), provideHttpClientTesting()] });
    httpMock = TestBed.inject(HttpTestingController);
    const fixture = TestBed.createComponent(WorkEvidencePanelComponent);
    fixture.componentRef.setInput('workOrderId', 'wo-1');
    fixture.detectChanges();
    httpMock.expectOne(url).flush({ items: listed });
    fixture.detectChanges();
    return { fixture, root: fixture.nativeElement as HTMLElement };
  }

  function chooseFile(fixture: ComponentFixture<WorkEvidencePanelComponent>, root: HTMLElement, file: File): void {
    const input = root.querySelector<HTMLInputElement>('input[type="file"]')!;
    Object.defineProperty(input, 'files', { value: [file], configurable: true });
    input.dispatchEvent(new Event('change'));
    fixture.detectChanges();
  }

  function buttonNamed(root: HTMLElement, text: string): HTMLButtonElement | undefined {
    return Array.from(root.querySelectorAll('button')).find((button) => button.textContent?.trim() === text);
  }

  afterEach(() => httpMock.verify());

  it('loads the list once and shows an empty state', () => {
    const { root } = create([]);

    expect(root.textContent).toContain('No evidence has been uploaded yet.');
    expect(root.querySelector('select')?.querySelectorAll('option')).toHaveLength(3);
  });

  it('shows every item with its scan status: PENDING and FAILED do not count, CLEAN does, bound items are marked', () => {
    const { root } = create([
      item({ evidenceId: 'a', fileName: 'pending.png', scanStatus: 'PENDING' }),
      item({ evidenceId: 'b', fileName: 'failed.png', scanStatus: 'FAILED' }),
      item({ evidenceId: 'c', fileName: 'clean.png', scanStatus: 'CLEAN' }),
      item({ evidenceId: 'd', fileName: 'bound.png', scanStatus: 'CLEAN', bound: true, slotNo: 1 }),
    ]);
    const text = root.textContent ?? '';

    expect(text).toContain('pending.png');
    expect(text).toContain('PENDING scan — does not count yet');
    expect(text).toContain('FAILED — does not count');
    expect(text).toContain('CLEAN — part of the submitted Work Summary');
    expect(text).toContain('2 of 4 item(s) are CLEAN.');
  });

  it('keeps Upload disabled until a file is chosen, then uploads once and appends the new PENDING item', () => {
    const { fixture, root } = create([]);
    expect(buttonNamed(root, 'Upload evidence')!.disabled).toBe(true);

    chooseFile(fixture, root, new File(['x'], 'new.png', { type: 'image/png' }));
    const select = root.querySelector('select')!;
    select.value = 'BEFORE_WORK';
    select.dispatchEvent(new Event('change'));
    fixture.detectChanges();
    expect(buttonNamed(root, 'Upload evidence')!.disabled).toBe(false);

    root.querySelector('form')!.dispatchEvent(new Event('submit'));
    root.querySelector('form')!.dispatchEvent(new Event('submit'));
    fixture.detectChanges();

    const request = httpMock.expectOne(url);
    expect(request.request.method).toBe('POST');
    expect((request.request.body as FormData).get('evidenceType')).toBe('BEFORE_WORK');
    expect(buttonNamed(root, 'Uploading…')!.disabled).toBe(true);

    request.flush(item({ evidenceId: 'new', fileName: 'new.png', evidenceType: 'BEFORE_WORK' }), { status: 201, statusText: 'Created' });
    fixture.detectChanges();

    expect(root.textContent).toContain('new.png');
    expect(root.textContent).toContain('PENDING scan');
    expect(buttonNamed(root, 'Upload evidence')!.disabled).toBe(true);
  });

  it('rejects a file over 10 MB without a request', () => {
    const { fixture, root } = create([]);
    const big = new File([new Uint8Array(1)], 'big.png', { type: 'image/png' });
    Object.defineProperty(big, 'size', { value: 10 * 1024 * 1024 + 1 });

    chooseFile(fixture, root, big);

    expect(root.querySelector('[role="alert"]')?.textContent).toContain('10 MB');
    expect(buttonNamed(root, 'Upload evidence')!.disabled).toBe(true);
  });

  it.each([
    [409, { detail: 'This submission round already has 10 active evidence items.' }, 'already has 10 active evidence items'],
    [422, { errors: { file: ['Only PDF, JPG, JPEG or PNG files with a matching content type are accepted.'] } }, 'Only PDF, JPG, JPEG or PNG'],
    [404, {}, 'not found, or it is not assigned to you'],
    [403, {}, 'do not have permission'],
    [500, {}, 'could not be uploaded'],
  ])('shows a plain message when the upload is refused (%i)', (status, body, expected) => {
    const { fixture, root } = create([]);
    chooseFile(fixture, root, new File(['x'], 'a.png', { type: 'image/png' }));

    root.querySelector('form')!.dispatchEvent(new Event('submit'));
    httpMock.expectOne(url).flush(body, { status, statusText: 'Refused' });
    fixture.detectChanges();

    expect(root.querySelector('[role="alert"]')?.textContent).toContain(expected);
    expect(buttonNamed(root, 'Upload evidence')!.disabled).toBe(false);
  });

  it('refreshes the status from the server on demand', () => {
    const { fixture, root } = create([item({ scanStatus: 'PENDING' })]);

    buttonNamed(root, 'Refresh status')!.click();
    fixture.detectChanges();
    httpMock.expectOne(url).flush({ items: [item({ scanStatus: 'CLEAN' })] });
    fixture.detectChanges();

    expect(root.textContent).toContain('1 of 1 item(s) are CLEAN.');
  });
});
