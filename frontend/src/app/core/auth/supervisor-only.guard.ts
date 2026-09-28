import { CanActivateFn } from '@angular/router';
import { inject } from '@angular/core';
import { Router } from '@angular/router';
import { getCurrentUserRoles } from './current-user-role';

/**
 * `docs/13` §4.19 UX gate for `/cost-summary-review`: redirects away when the token's own `role` claim doesn't
 * include SUPERVISOR, so a Team Lead/Requester/etc. browsing around doesn't land on a page that can only ever
 * 403 for them. This is convenience routing only — the backend's `CostSummary.Review` /
 * `CostSummary.ReadPendingReview` policies (SUPERVISOR only) are the real authorization boundary and are
 * re-checked on every request regardless of what this guard decides.
 */
export const supervisorOnlyGuard: CanActivateFn = () => {
  const router = inject(Router);
  return getCurrentUserRoles().includes('SUPERVISOR') ? true : router.parseUrl('/work-orders');
};
