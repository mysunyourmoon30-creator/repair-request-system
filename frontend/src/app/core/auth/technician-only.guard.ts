import { CanActivateFn } from '@angular/router';
import { inject } from '@angular/core';
import { Router } from '@angular/router';
import { getCurrentUserRoles } from './current-user-role';

/**
 * S3-001 UX gate for `/my-visits`: redirects away when the token's own `role` claim doesn't
 * include TECHNICIAN, so a Coordinator/Requester/etc. browsing around doesn't land on a page that
 * can only ever 403 for them. This is convenience routing only — the backend's `MyVisits.Read` /
 * `WorkSession.CheckIn` policies (TECHNICIAN only) are the real authorization boundary and are
 * re-checked on every request regardless of what this guard decides.
 */
export const technicianOnlyGuard: CanActivateFn = () => {
  const router = inject(Router);
  return getCurrentUserRoles().includes('TECHNICIAN') ? true : router.parseUrl('/work-orders');
};
