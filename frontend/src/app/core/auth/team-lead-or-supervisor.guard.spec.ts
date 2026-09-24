import { TestBed } from '@angular/core/testing';
import { Router, UrlTree, provideRouter } from '@angular/router';
import { teamLeadOrSupervisorGuard } from './team-lead-or-supervisor.guard';

function tokenWithPayload(payload: Record<string, unknown>): string {
  const base64url = (value: string) => btoa(value).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
  return `${base64url('{"alg":"HS256"}')}.${base64url(JSON.stringify(payload))}.signature`;
}

describe('teamLeadOrSupervisorGuard', () => {
  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideRouter([])] });
  });

  afterEach(() => localStorage.clear());

  it('allows navigation when the token role is TEAM_LEAD', () => {
    localStorage.setItem('accessToken', tokenWithPayload({ sub: 'u1', role: 'TEAM_LEAD' }));

    const result = TestBed.runInInjectionContext(() => teamLeadOrSupervisorGuard({} as never, {} as never));

    expect(result).toBe(true);
  });

  it('allows navigation when the token role is SUPERVISOR', () => {
    localStorage.setItem('accessToken', tokenWithPayload({ sub: 'u1', role: 'SUPERVISOR' }));

    const result = TestBed.runInInjectionContext(() => teamLeadOrSupervisorGuard({} as never, {} as never));

    expect(result).toBe(true);
  });

  it('redirects to /work-orders when the caller is neither', () => {
    localStorage.setItem('accessToken', tokenWithPayload({ sub: 'u1', role: 'TECHNICIAN' }));

    const result = TestBed.runInInjectionContext(() => teamLeadOrSupervisorGuard({} as never, {} as never)) as UrlTree;

    expect(result).toBeInstanceOf(UrlTree);
    expect(TestBed.inject(Router).serializeUrl(result)).toBe('/work-orders');
  });

  it('redirects to /work-orders when there is no token at all', () => {
    const result = TestBed.runInInjectionContext(() => teamLeadOrSupervisorGuard({} as never, {} as never)) as UrlTree;

    expect(TestBed.inject(Router).serializeUrl(result)).toBe('/work-orders');
  });
});
