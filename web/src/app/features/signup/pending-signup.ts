import { Injectable } from '@angular/core';

const KEY = 'bp.signupStore';
const STORE_ID = /^[A-Za-z0-9_-]{1,128}$/;

/**
 * Remembers which store's sign-up link the visitor used, across the round trip to the login page.
 * sessionStorage: per tab, gone when the tab closes. It's only a convenience; the Identity service
 * decides whether the store accepts sign-ups (it may be closed or unknown).
 */
@Injectable({ providedIn: 'root' })
export class PendingSignup {
  static isValidStoreId(value: string | null | undefined): value is string {
    return !!value && STORE_ID.test(value);
  }

  get(): string | null {
    try {
      const value = sessionStorage.getItem(KEY);
      return PendingSignup.isValidStoreId(value) ? value : null;
    } catch {
      return null;
    }
  }

  set(store: string): void {
    try {
      sessionStorage.setItem(KEY, store);
    } catch {
      /* storage blocked: the visitor can open the link again after signing in */
    }
  }

  clear(): void {
    try {
      sessionStorage.removeItem(KEY);
    } catch {
      /* nothing to clear */
    }
  }
}
