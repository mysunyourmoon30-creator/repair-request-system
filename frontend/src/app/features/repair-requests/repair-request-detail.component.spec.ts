import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';
import { vi } from 'vitest';
import { environment } from '../../../environments/environment';
import { RepairRequestDetailComponent } from './repair-request-detail.component';

describe('RepairRequestDetailComponent', () => {
  let httpMock: HttpTestingController;
  const baseUrl = `${environment.apiBaseUrl}/v1/repair-requests`;

  function createComponent(id: string | null): ComponentFixture<RepairRequestDetailComponent> {
    TestBed.configureTestingModule({
      imports: [RepairRequestDetailComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        {
          provide: ActivatedRoute,
          useValue: { snapshot: { paramMap: convertToParamMap(id ? { id } : {}) } },
        },
      ],
    });
    httpMock = TestBed.inject(HttpTestingController);
    return TestBed.createComponent(RepairRequestDetailComponent);
  }

  afterEach(() => httpMock.verify());

  function flushGet(fixture: ComponentFixture<RepairRequestDetailComponent>, status: string): void {
    httpMock.expectOne(`${baseUrl}/rr-1`).flush(
      {
        id: 'rr-1',
        status,
        requestNo: 'RR-2026-000001',
        description: 'Pump leaking',
        rowVersion: 'AAAAAAAAB9E=',
      },
      { headers: { ETag: '"AAAAAAAAB9E="' } },
    );
    fixture.detectChanges();
  }

  it('requests the Repair Request by id and renders its fields', () => {
    const fixture = createComponent('rr-1');
    fixture.detectChanges();
    flushGet(fixture, 'UNDER_REVIEW');

    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text).toContain('RR-2026-000001');
    expect(text).toContain('Pump leaking');
  });

  it('shows the Convert button only when the request is APPROVED', () => {
    const fixture = createComponent('rr-1');
    fixture.detectChanges();
    flushGet(fixture, 'UNDER_REVIEW');

    expect(fixture.nativeElement.querySelector('button')).toBeNull();
  });

  it('converts and navigates to the new Work Order on success', () => {
    const fixture = createComponent('rr-1');
    fixture.detectChanges();
    flushGet(fixture, 'APPROVED');

    const router = TestBed.inject(Router);
    const navigateSpy = vi.spyOn(router, 'navigate');

    const button = fixture.nativeElement.querySelector('button') as HTMLButtonElement;
    expect(button).not.toBeNull();
    button.click();

    const convertReq = httpMock.expectOne(`${baseUrl}/rr-1/convert-to-work-order`);
    expect(convertReq.request.headers.get('If-Match')).toBe('"AAAAAAAAB9E="');
    convertReq.flush({ workOrderId: 'wo-1', workOrderNo: 'WO-2026-000001', status: 'OPEN' });

    expect(navigateSpy).toHaveBeenCalledWith(['/work-orders', 'wo-1']);
  });

  it('shows a distinct message when Convert returns 409', () => {
    const fixture = createComponent('rr-1');
    fixture.detectChanges();
    flushGet(fixture, 'APPROVED');

    const button = fixture.nativeElement.querySelector('button') as HTMLButtonElement;
    button.click();

    httpMock
      .expectOne(`${baseUrl}/rr-1/convert-to-work-order`)
      .flush('Conflict', { status: 409, statusText: 'Conflict' });
    fixture.detectChanges();

    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text).toContain('already converted');
  });

  it('shows a distinct message when Convert returns 403', () => {
    const fixture = createComponent('rr-1');
    fixture.detectChanges();
    flushGet(fixture, 'APPROVED');

    const button = fixture.nativeElement.querySelector('button') as HTMLButtonElement;
    button.click();

    httpMock
      .expectOne(`${baseUrl}/rr-1/convert-to-work-order`)
      .flush('Forbidden', { status: 403, statusText: 'Forbidden' });
    fixture.detectChanges();

    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text).toContain('do not have permission');
  });

  it('shows a not-found message on 404 (missing or out of scope)', () => {
    const fixture = createComponent('missing');
    fixture.detectChanges();

    httpMock.expectOne(`${baseUrl}/missing`).flush('Not Found', { status: 404, statusText: 'Not Found' });
    fixture.detectChanges();

    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text).toContain('does not exist');
  });

  it('shows an unauthorized message on 401/403', () => {
    const fixture = createComponent('rr-1');
    fixture.detectChanges();

    httpMock.expectOne(`${baseUrl}/rr-1`).flush('Forbidden', { status: 403, statusText: 'Forbidden' });
    fixture.detectChanges();

    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text).toContain('not signed in');
  });
});
