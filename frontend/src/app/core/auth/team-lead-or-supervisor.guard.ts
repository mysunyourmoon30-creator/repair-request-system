import { CanActivateFn } from '@angular/router';
import { inject } from '@angular/core';
import { Router } from '@angular/router';
import { getCurrentUserRoles } from './current-user-role';

/**
 * UX gate for the Work Summary review page (`docs/13` §4.15): redirects away when the token's own `role` claim
 * includes neither TEAM_LEAD nor SUPERVISOR. This is convenience routing only — the backend's
 * `WorkOrder.SubmitForAcceptance` policy (Team Lead or Supervisor) is the real authorization boundary and is
 * re-checked on every request regardless of what this guard decides.
 */
export const teamLeadOrSupervisorGuard: CanActivateFn = () => {
  const router = inject(Router);
  const roles = getCurrentUserRoles();
  return roles.includes('TEAM_LEAD') || roles.includes('SUPERVISOR') ? true : router.parseUrl('/work-orders');
};
