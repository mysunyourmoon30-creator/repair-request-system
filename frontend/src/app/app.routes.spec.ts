import { routes } from './app.routes';

describe('app routes', () => {
  const byPath = (path: string) => routes.find((route) => route.path === path);

  it('adds the Service Report route under the Work Order, loaded lazily', () => {
    const route = byPath('work-orders/:id/service-report');

    expect(route).toBeDefined();
    expect(route?.loadComponent).toBeDefined();
  });

  it('does not guard the Service Report route by role: the backend decides who may open it (guards are UX only)', () => {
    expect(byPath('work-orders/:id/service-report')?.canActivate).toBeUndefined();
  });

  it('keeps every existing route and its role guard', () => {
    expect(byPath('work-orders')).toBeDefined();
    expect(byPath('work-orders/:id')).toBeDefined();
    expect(byPath('my-visits')?.canActivate).toHaveLength(1);
    expect(byPath('work-summary-review')?.canActivate).toHaveLength(1);
    expect(byPath('cost-summary-review')?.canActivate).toHaveLength(1);
    expect(byPath('repair-requests')).toBeDefined();
    expect(byPath('repair-requests/:id')).toBeDefined();
  });

  it('adds no Repair Request creation, approval or login screen', () => {
    expect(routes.map((route) => route.path)).not.toContain('login');
    expect(routes.map((route) => route.path).filter((path) => path?.includes('approval'))).toEqual([]);
    expect(routes.map((route) => route.path).filter((path) => path?.includes('new'))).toEqual([]);
  });
});
