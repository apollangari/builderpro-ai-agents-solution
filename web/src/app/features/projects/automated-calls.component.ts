import { Component, computed, effect, input, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { httpResource } from '@angular/common/http';
import { describeError } from '../../core/api/describe-error';

interface CallInfo {
  id: string;
  status: 'scheduled' | 'calling' | 'slot_chosen' | 'person_requested' | 'opted_out' | 'no_answer' | 'cancelled';
  dueAt: string;
  attempts: number;
  proposals: string[];
  chosenStart: string | null;
  note: string | null;
}

interface AutomatedCalls {
  available: boolean;
  consented: boolean;
  wordingVersion: string;
  wording: string;
  phone: string | null;
  hours: string;
  calls: CallInfo[];
}

/**
 * 4b: AI phone scheduling (a store option). The customer turns automated calls on after reading the exact
 * wording (its version is sent and recorded), and off at any time. Shown only where the store offers it.
 */
@Component({
  selector: 'bp-automated-calls',
  imports: [DatePipe],
  template: `
    @if (state(); as s) {
      @if (s.available) {
        <div class="calls" data-testid="automated-calls">
          <strong>Prefer a call?</strong>
          <p class="muted small" data-testid="call-wording">{{ s.wording }}</p>
          @if (latest(); as c) {
            <p class="small" data-testid="call-status">
              @switch (c.status) {
                @case ('scheduled') { We'll call within your contact hours, from {{ c.dueAt | date: 'EEE MMM d, h:mm a' }}. }
                @case ('calling') { We've called you with {{ c.proposals.length }} times to choose from. }
                @case ('slot_chosen') { You picked {{ c.chosenStart | date: 'EEE MMM d, h:mm a' }} on the call. Confirm it with the link we sent, or below. }
                @case ('person_requested') { You asked for a person; the store will call you. }
                @case ('opted_out') { Automated calls are off. }
                @case ('no_answer') { We couldn't reach you; pick a time below. }
                @default { {{ c.note || 'No call needed.' }} }
              }
            </p>
          }
          <div class="actions">
            @if (s.consented) {
              <button type="button" class="btn" (click)="set(false)" [disabled]="saving.isLoading()" data-testid="calls-off">Stop automated calls</button>
            } @else {
              <button type="button" class="btn" (click)="set(true)" [disabled]="saving.isLoading()" data-testid="calls-on">Yes, call me to schedule</button>
            }
          </div>
          @if (saving.error()) { <div class="warning" role="alert"><span>{{ describe(saving.error()) }}</span></div> }
        </div>
      }
    }
  `,
  styles: `
    .calls { display: grid; gap: .35rem; margin-top: 1rem; padding: .8rem 1rem; border: 1px dashed var(--bp-border-strong); border-radius: var(--bp-radius-md); }
    .calls p { margin: 0; }
    .small { font-size: .85rem; }
  `,
})
export class AutomatedCallsComponent {
  readonly projectId = input.required<string>();

  protected readonly loaded = httpResource<AutomatedCalls>(() => `/api/projects/${encodeURIComponent(this.projectId())}/automated-calls`);
  private readonly request = signal<{ consent: boolean; n: number } | null>(null);
  private n = 0;
  protected readonly saving = httpResource<AutomatedCalls>(() => {
    const r = this.request();
    const s = this.loaded.hasValue() ? this.loaded.value() : null;
    return r && s
      ? { url: `/api/projects/${encodeURIComponent(this.projectId())}/automated-calls`, method: 'PUT', body: { consent: r.consent, wordingVersion: s.wordingVersion } }
      : undefined;
  });
  private readonly saved = signal<AutomatedCalls | null>(null);
  protected readonly state = computed(() => this.saved() ?? (this.loaded.hasValue() ? this.loaded.value() : null));
  protected readonly latest = computed(() => this.state()?.calls.at(-1) ?? null);

  constructor() {
    effect(() => {
      if (this.saving.hasValue()) this.saved.set(this.saving.value()!);
    });
  }

  protected set(consent: boolean): void {
    this.request.set({ consent, n: ++this.n });
  }

  protected describe(err: unknown): string {
    return describeError(err as Parameters<typeof describeError>[0]);
  }
}
