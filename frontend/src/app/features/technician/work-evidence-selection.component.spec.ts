import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { environment } from '../../../environments/environment';
import { WorkEvidenceItem } from '../work-orders/work-evidence.models';
import { MAX_SELECTED_EVIDENCE, WorkEvidenceSelectionComponent } from './work-evidence-selection.component';

describe('WorkEvidenceSelectionComponent', () => {
  let httpMock: HttpTestingController;
  const url = `${environment.apiBaseUrl}/v1/work-orders/wo-1/evidence`;

  const item = (id: string, overrides: Partial<WorkEvidenceItem> = {}): WorkEvidenceItem => ({
    evidenceId: id,
    serviceVisitId: 'visit-1',
    evidenceType: 'AFTER_WORK',
    fileName: `${id}.png`,
    mimeType: 'image/png',
    sizeBytes: 2048,
    scanStatus: 'CLEAN',
    bound: false,
    workSummaryId: null,
    slotNo: null,
    uploadedAt: '2026-10-01T08:00:00Z',
    ...overrides,
  });

  function create(listed: WorkEvidenceItem[]): {
    fixture: ComponentFixture<WorkEvidenceSelectionComponent>;
    root: HTMLElement;
    emitted: string[][];
  } {
    TestBed.configureTestingModule({ imports: [WorkEvidenceSelectionComponent], providers: [provideHttpClient(), provideHttpClientTesting()] });
    httpMock = TestBed.inject(HttpTestingController);
    const fixture = TestBed.createComponent(WorkEvidenceSelectionComponent);
    fixture.componentRef.setInput('workOrderId', 'wo-1');
    fixture.componentRef.setInput('serviceVisitId', 'visit-1');
    const emitted: string[][] = [];
    fixture.componentInstance.selectionChange.subscribe((ids) => emitted.push(ids));
    fixture.detectChanges();
    httpMock.expectOne(url).flush({ items: listed });
    fixture.detectChanges();
    return { fixture, root: fixture.nativeElement as HTMLElement, emitted };
  }

  function boxes(root: HTMLElement): HTMLInputElement[] {
    return Array.from(root.querySelectorAll<HTMLInputElement>('input[type="checkbox"]'));
  }

  afterEach(() => httpMock.verify());

  it('offers exactly the unfrozen evidence of the given Visit, in server order', () => {
    const { root } = create([
      item('a'),
      item('b', { serviceVisitId: 'visit-0' }),
      item('c', { bound: true, slotNo: 1 }),
      item('d', { scanStatus: 'PENDING' }),
      item('e', { scanStatus: 'FAILED' }),
    ]);

    const names = Array.from(root.querySelectorAll('li')).map((li) => li.textContent ?? '');
    expect(names).toHaveLength(3);
    expect(names[0]).toContain('a.png');
    expect(names[1]).toContain('d.png');
    expect(names[2]).toContain('e.png');
  });

  it('enables only CLEAN checkboxes and emits the selection in click order, without duplicates', () => {
    const { fixture, root, emitted } = create([item('a'), item('p', { scanStatus: 'PENDING' }), item('f', { scanStatus: 'FAILED' }), item('b')]);
    expect(boxes(root).map((box) => box.disabled)).toEqual([false, true, true, false]);

    boxes(root)[3].click();
    fixture.detectChanges();
    boxes(root)[0].click();
    fixture.detectChanges();
    // A disabled (PENDING / FAILED) box does nothing.
    boxes(root)[1].click();
    boxes(root)[2].click();
    fixture.detectChanges();

    expect(emitted.at(-1)).toEqual(['b', 'a']);

    // Unselecting removes only that id; selecting again appends it once.
    boxes(root)[3].click();
    fixture.detectChanges();
    expect(emitted.at(-1)).toEqual(['a']);
    boxes(root)[3].click();
    fixture.detectChanges();
    expect(emitted.at(-1)).toEqual(['a', 'b']);
    expect(new Set(emitted.at(-1)).size).toBe(emitted.at(-1)!.length);
  });

  it('never allows more than ten selected items', () => {
    const { fixture, root, emitted } = create(Array.from({ length: 12 }, (_, i) => item(`e${i + 1}`)));
    expect(MAX_SELECTED_EVIDENCE).toBe(10);

    for (let index = 0; index < 12; index++) {
      boxes(root)[index].click();
      fixture.detectChanges();
    }

    expect(emitted.at(-1)).toHaveLength(10);
    expect(boxes(root).filter((box) => box.checked)).toHaveLength(10);
    expect(boxes(root).slice(10).every((box) => box.disabled)).toBe(true);
  });

  it('re-reads the list on Refresh status and drops selections that are no longer selectable', () => {
    const { fixture, root, emitted } = create([item('a'), item('b')]);
    boxes(root)[0].click();
    boxes(root)[1].click();
    fixture.detectChanges();
    expect(emitted.at(-1)).toEqual(['a', 'b']);

    Array.from(root.querySelectorAll('button')).find((button) => button.textContent?.trim() === 'Refresh status')!.click();
    httpMock.expectOne(url).flush({ items: [item('a'), item('b', { scanStatus: 'FAILED' })] });
    fixture.detectChanges();

    expect(emitted.at(-1)).toEqual(['a']);
    expect(root.textContent).toContain('1 of 10 selected.');
  });

  it('shows an empty state and a plain error without selecting anything', () => {
    const empty = create([]);
    expect(empty.root.textContent).toContain('No evidence has been uploaded for this visit yet.');
    expect(empty.emitted).toEqual([[]]);
  });
});
