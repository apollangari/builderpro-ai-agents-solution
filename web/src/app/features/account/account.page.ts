import { Component, computed, effect, inject, linkedSignal, signal } from '@angular/core';
import { httpResource } from '@angular/common/http';
import { OidcSecurityService } from 'angular-auth-oidc-client';
import { describeError } from '../../core/api/describe-error';

/** GET/PUT /api/identity/profile. Mirrors ProfileDto in C#. */
export interface Profile {
  firstName: string;
  lastName: string;
  email: string;
  phone: string;
  addressLine1: string;
  addressLine2: string | null;
  city: string;
  region: string;
  postalCode: string;
  country: string;
  contactConsent: string;
  contactHours: string;
  canChangePassword: boolean;
}

type ProfileForm = Omit<Profile, 'email' | 'canChangePassword'>;
interface FieldError {
  field: string;
  message: string;
}

const CONSENTS = [
  { id: 'call', label: 'Phone calls' },
  { id: 'text', label: 'Text messages (SMS)' },
  { id: 'both', label: 'Calls and texts' },
  { id: 'none', label: 'Email only, no calls or texts' },
];
const HOURS = [
  { id: 'business', label: 'Business hours (Mon-Fri, 9am-5pm)' },
  { id: 'evenings', label: 'Weekday evenings (5pm-8pm)' },
  { id: 'weekends', label: 'Weekends (9am-5pm)' },
  { id: 'anytime', label: 'Any time during business or evening hours' },
];

/**
 * Account (owner request): edit the profile in place and change the password in its own tab.
 * Both are saved by the Identity service (PUT /api/identity/profile, POST /api/identity/password),
 * which checks the same rules as sign-up and talks to the user store. Google sign-ins have no
 * password tab. Signal-first: the form is a signal; each save is an httpResource fired by a click.
 */
@Component({
  selector: 'bp-account-page',
  template: `
    <div class="page narrow">
      <header class="page-head">
        <span class="eyebrow">Account</span>
        <h1>Your account</h1>
      </header>

      <div class="tabs" role="tablist" aria-label="Account">
        <button type="button" role="tab" class="tab" [class.active]="tab() === 'profile'" [attr.aria-selected]="tab() === 'profile'"
                (click)="tab.set('profile')" data-testid="tab-profile">Profile</button>
        @if (loaded.hasValue() && loaded.value()!.canChangePassword) {
          <button type="button" role="tab" class="tab" [class.active]="tab() === 'password'" [attr.aria-selected]="tab() === 'password'"
                  (click)="tab.set('password')" data-testid="tab-password">Password</button>
        }
      </div>

      @if (loaded.error()) {
        <div class="warning" role="alert"><span>{{ describe(loaded.error()) }}</span></div>
      } @else if (!form()) {
        <p class="muted">Loading your profile…</p>
      } @else if (tab() === 'profile') {
        @let f = form()!;
        <form class="card form" (submit)="$event.preventDefault(); saveProfile()" data-testid="profile-form" novalidate>
          <div class="grid2">
            <label>First name <input type="text" autocomplete="given-name" [value]="f.firstName" (input)="set('firstName', $event)" data-testid="f-firstName" /></label>
            <label>Last name <input type="text" autocomplete="family-name" [value]="f.lastName" (input)="set('lastName', $event)" data-testid="f-lastName" /></label>
          </div>
          <label>Email <input type="email" [value]="loaded.value()!.email" disabled /><span class="hint">Your sign-in email can't be changed here.</span></label>
          <label>Mobile phone <input type="tel" autocomplete="tel" [value]="f.phone" (input)="set('phone', $event)" data-testid="f-phone" /></label>
          <label>Street address <input type="text" autocomplete="address-line1" [value]="f.addressLine1" (input)="set('addressLine1', $event)" data-testid="f-addressLine1" /></label>
          <label>Apartment, suite (optional) <input type="text" autocomplete="address-line2" [value]="f.addressLine2 ?? ''" (input)="set('addressLine2', $event)" /></label>
          <div class="grid3">
            <label>City <input type="text" autocomplete="address-level2" [value]="f.city" (input)="set('city', $event)" data-testid="f-city" /></label>
            <label>State / province <input type="text" autocomplete="address-level1" [value]="f.region" (input)="set('region', $event)" /></label>
            <label>ZIP / postal code <input type="text" autocomplete="postal-code" [value]="f.postalCode" (input)="set('postalCode', $event)" data-testid="f-postalCode" /></label>
          </div>
          <label>Country
            <select [value]="f.country" (change)="set('country', $event)">
              <option value="US">United States</option>
              <option value="CA">Canada</option>
            </select>
          </label>
          <fieldset>
            <legend>How may we contact you about your project?</legend>
            @for (c of consents; track c.id) {
              <label class="radio"><input type="radio" name="consent" [checked]="f.contactConsent === c.id" (change)="setValue('contactConsent', c.id)" [attr.data-testid]="'consent-' + c.id" /> {{ c.label }}</label>
            }
          </fieldset>
          <label>Best time to reach you
            <select [value]="f.contactHours" (change)="set('contactHours', $event)">
              @for (h of hours; track h.id) { <option [value]="h.id">{{ h.label }}</option> }
            </select>
          </label>

          @for (e of profileErrors(); track $index) { <p class="field-error" role="alert">{{ e.message }}</p> }
          @if (profileSaved.hasValue() && !dirty()) { <p class="ok" role="status" data-testid="profile-saved">Saved.</p> }
          <div class="actions">
            <button type="submit" class="btn btn-primary" [disabled]="!dirty() || profileSaved.isLoading()" data-testid="save-profile">
              {{ profileSaved.isLoading() ? 'Saving…' : 'Save changes' }}
            </button>
          </div>
        </form>
      } @else {
        <form class="card form" (submit)="$event.preventDefault(); savePassword()" data-testid="password-form" novalidate>
          <label>Current password <input type="password" autocomplete="current-password" [value]="pw().current" (input)="setPw('current', $event)" data-testid="pw-current" /></label>
          <label>New password <input type="password" autocomplete="new-password" [value]="pw().next" (input)="setPw('next', $event)" data-testid="pw-new" />
            <span class="hint">At least 8 characters.</span></label>
          <label>Confirm new password <input type="password" autocomplete="new-password" [value]="pw().confirm" (input)="setPw('confirm', $event)" data-testid="pw-confirm" /></label>
          @if (mismatch()) { <p class="field-error" data-testid="pw-mismatch">The new passwords don't match.</p> }
          @for (e of passwordErrors(); track $index) { <p class="field-error" role="alert" data-testid="pw-error">{{ e.message }}</p> }
          @if (passwordDone()) { <p class="ok" role="status" data-testid="pw-changed">Your password was changed. Use it next time you sign in.</p> }
          <div class="actions">
            <button type="submit" class="btn btn-primary" [disabled]="!canChangePw() || passwordSaved.isLoading()" data-testid="save-password">
              {{ passwordSaved.isLoading() ? 'Changing…' : 'Change password' }}
            </button>
          </div>
        </form>
      }
    </div>
  `,
  styles: `
    .narrow { max-width: 46rem; }
    .tabs { display: flex; gap: .35rem; border-bottom: 1px solid var(--bp-border); margin-bottom: 1rem; }
    .tab { min-height: var(--bp-tap); padding: 0 1rem; border: 0; border-bottom: 3px solid transparent; background: none; color: var(--bp-ink-muted); font: inherit; cursor: pointer; }
    .tab.active { color: var(--bp-accent); border-bottom-color: var(--bp-accent); font-weight: 600; }
    .form { display: grid; gap: .8rem; }
    .form label { display: grid; gap: .3rem; font-weight: 600; }
    /* Text fields and selects only: radios and checkboxes keep their own size (owner: radios were stretched). */
    .form input:not([type='radio']):not([type='checkbox']), .form select {
      width: 100%; box-sizing: border-box; min-width: 0;
      min-height: var(--bp-tap); padding: 0 .8rem; border-radius: var(--bp-radius-md); border: 1px solid var(--bp-border-strong);
      background: var(--bp-surface); color: var(--bp-ink); font: inherit; font-weight: 400;
    }
    .form input:disabled { background: var(--bp-surface-muted); color: var(--bp-ink-muted); }
    .grid2 { display: grid; gap: .8rem; grid-template-columns: minmax(0, 1fr) minmax(0, 1fr); }
    .grid3 { display: grid; gap: .8rem; grid-template-columns: minmax(0, 2fr) minmax(0, 1fr) minmax(0, 1fr); }
    @media (max-width: 640px) { .grid2, .grid3 { grid-template-columns: 1fr; } }
    fieldset { border: 1px solid var(--bp-border); border-radius: var(--bp-radius-md); padding: .6rem .9rem; display: grid; gap: .2rem; }
    legend { font-weight: 600; padding: 0 .3rem; }
    .form .radio { display: flex; align-items: center; justify-content: flex-start; gap: .6rem; font-weight: 400; min-height: 2.25rem; cursor: pointer; }
    .form .radio input { width: 1.1rem; height: 1.1rem; margin: 0; flex: none; accent-color: var(--bp-accent); }
    .hint { font-weight: 400; font-size: .85rem; color: var(--bp-ink-muted); }
    .field-error { margin: 0; color: var(--bp-danger, #b3261e); }
    .ok { margin: 0; color: var(--bp-accent); font-weight: 600; }
    .actions { display: flex; gap: .6rem; justify-content: flex-end; }
  `,
})
export class AccountPage {
  private readonly oidc = inject(OidcSecurityService);
  protected readonly consents = CONSENTS;
  protected readonly hours = HOURS;
  protected readonly tab = signal<'profile' | 'password'>('profile');

  protected readonly loaded = httpResource<Profile>(() => '/api/identity/profile');

  /** The editable copy: starts as the saved profile, then each keystroke. */
  protected readonly form = linkedSignal<ProfileForm | null>(() => {
    if (!this.loaded.hasValue()) return null;
    const { email, canChangePassword, ...editable } = this.loaded.value()!;
    void email;
    void canChangePassword;
    return editable;
  });
  private readonly savedJson = signal<string | null>(null);
  protected readonly dirty = computed(() => {
    const f = this.form();
    if (!f) return false;
    const base = this.savedJson() ?? (this.loaded.hasValue() ? JSON.stringify(this.form0()) : null);
    return JSON.stringify(f) !== base;
  });
  private readonly form0 = computed(() => {
    if (!this.loaded.hasValue()) return null;
    const { email, canChangePassword, ...editable } = this.loaded.value()!;
    void email;
    void canChangePassword;
    return editable;
  });

  private readonly profileRequest = signal<ProfileForm | null>(null);
  protected readonly profileSaved = httpResource<Profile>(() => {
    const body = this.profileRequest();
    return body ? { url: '/api/identity/profile', method: 'PUT', body } : undefined;
  });
  protected readonly profileErrors = computed(() => fieldErrors(this.profileSaved.error()));

  protected readonly pw = signal({ current: '', next: '', confirm: '' });
  protected readonly mismatch = computed(() => this.pw().confirm.length > 0 && this.pw().next !== this.pw().confirm);
  protected readonly canChangePw = computed(() => {
    const p = this.pw();
    return p.current.length > 0 && p.next.length >= 8 && p.next === p.confirm;
  });
  private readonly passwordRequest = signal<{ currentPassword: string; newPassword: string } | null>(null);
  protected readonly passwordSaved = httpResource<unknown>(() => {
    const body = this.passwordRequest();
    return body ? { url: '/api/identity/password', method: 'POST', body } : undefined;
  });
  protected readonly passwordErrors = computed(() => fieldErrors(this.passwordSaved.error()));
  protected readonly passwordDone = computed(() => this.passwordSaved.status() === 'resolved');

  constructor() {
    // Saved: this is the new baseline, and fresh tokens carry the new claims (name, ZIP, consent).
    effect(() => {
      if (this.profileSaved.hasValue()) {
        this.savedJson.set(JSON.stringify(this.profileRequest()));
        this.oidc.forceRefreshSession().subscribe({ error: () => undefined });
      }
    });
    effect(() => {
      if (this.passwordDone()) this.pw.set({ current: '', next: '', confirm: '' });
    });
  }

  protected set(field: keyof ProfileForm, e: Event): void {
    this.setValue(field, (e.target as HTMLInputElement | HTMLSelectElement).value);
  }

  protected setValue(field: keyof ProfileForm, value: string): void {
    this.form.update((f) => (f ? { ...f, [field]: value } : f));
  }

  protected setPw(field: 'current' | 'next' | 'confirm', e: Event): void {
    this.pw.update((p) => ({ ...p, [field]: (e.target as HTMLInputElement).value }));
  }

  protected saveProfile(): void {
    const f = this.form();
    if (f && this.dirty()) this.profileRequest.set({ ...f });
  }

  protected savePassword(): void {
    const p = this.pw();
    if (this.canChangePw()) this.passwordRequest.set({ currentPassword: p.current, newPassword: p.next });
  }

  protected describe(err: unknown): string {
    return describeError(err as Parameters<typeof describeError>[0]);
  }
}

/** The server's per-field messages, or one general message. */
function fieldErrors(err: unknown): FieldError[] {
  if (!err) return [];
  const body = (err as { error?: { errors?: FieldError[] } }).error;
  return body?.errors?.length ? body.errors : [{ field: '', message: describeError(err as never) }];
}
