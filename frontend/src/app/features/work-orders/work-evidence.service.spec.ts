import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { environment } from '../../../environments/environment';
import { ServiceReportService } from './service-report.service';
import { EVIDENCE_TYPES } from './work-evidence.models';
import { WorkEvidenceService } from './work-evidence.service';

describe('WorkEvidenceService and ServiceReportService', () => {
  let httpMock: HttpTestingController;
  const baseUrl = `${environment.apiBaseUrl}/v1/work-orders`;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  it('offers exactly the three approved evidence types', () => {
    expect(EVIDENCE_TYPES.map((type) => type.code)).toEqual(['BEFORE_WORK', 'AFTER_WORK', 'OTHER']);
  });

  it('lists the evidence of a Work Order with one GET', () => {
    TestBed.inject(WorkEvidenceService).list('wo-1').subscribe();

    const request = httpMock.expectOne(`${baseUrl}/wo-1/evidence`);
    expect(request.request.method).toBe('GET');
    request.flush({ items: [] });
  });

  it('uploads only the file and the evidence type — never a tenant, uploader, visit, scan status or binding id', () => {
    const file = new File([new Uint8Array([1, 2, 3])], 'photo.png', { type: 'image/png' });

    TestBed.inject(WorkEvidenceService).upload('wo-1', file, 'BEFORE_WORK').subscribe();

    const request = httpMock.expectOne(`${baseUrl}/wo-1/evidence`);
    expect(request.request.method).toBe('POST');
    const body = request.request.body as FormData;
    expect(Array.from(body.keys()).sort()).toEqual(['evidenceType', 'file']);
    expect(body.get('evidenceType')).toBe('BEFORE_WORK');
    expect((body.get('file') as File).name).toBe('photo.png');
    request.flush({});
  });

  it('reads the Service Report with one GET and downloads evidence content as a Blob from the dedicated endpoint', () => {
    const service = TestBed.inject(ServiceReportService);

    service.getServiceReport('wo-1').subscribe();
    const report = httpMock.expectOne(`${baseUrl}/wo-1/service-report`);
    expect(report.request.method).toBe('GET');
    report.flush({});

    service.downloadEvidence('wo-1', 'ev-9').subscribe();
    const download = httpMock.expectOne(`${baseUrl}/wo-1/evidence/ev-9/content`);
    expect(download.request.method).toBe('GET');
    expect(download.request.responseType).toBe('blob');
    expect(download.request.url).not.toContain('/files/');
    download.flush(new Blob());
  });
});
