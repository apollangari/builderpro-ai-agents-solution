import { Component, computed, effect, input, output, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { httpResource } from '@angular/common/http';
import { describeError } from '../../core/api/describe-error';
import { VisitInfo } from '../quote/guided';

interface Slot {
  start: string;
  available: boolean;
  /** Inside the customer's contact hours (null when unknown). */
  fitsHours?: boolean | null;
}

/**
 * 6b: book or move the measure visit. Times come from the store (GET .../visit/slots) and are shown
 * in the viewer's time zone, grouped by day. A booked visit shows with "Change time".
 */
@Component({
  selector: 'bp-visit-booking',
  imports: [DatePipe],
  template: `
    @if (visit() && !changing()) {
      <p class="ok" data-testid="visit-booked">Measure visit booked for {{ visit()!.start | date: 'EEEE, MMM d, h:mm a' }}.</p>
      <div class="actions"><button type="button" class="btn" (click)="changing.set(true)" data-testid="visit-change">Change time</button></div>
    } @else {
      @if (suggestedSlot(); as s) {
        <div class="suggested" data-testid="visit-suggested">
          <span>From your call: <strong>{{ s.start | date: 'EEEE, MMM d, h:mm a' }}</strong></span>
          <button type="button" class="btn btn-primary" (click)="book(s.start)" [disabled]="booking.isLoading()" data-testid="visit-confirm-suggested">Confirm this time</button>
        </div>
      }
      <p>Pick a time for the measure visit. We confirm the sizes on site; your price per unit stays held.</p>
      @if (anyOutside()) { <p class="muted small" data-testid="hours-legend">Times outside your contact hours are shown faded; you can still pick them.</p> }
      @if (slots.error()) {
        <div class="warning" role="alert"><span>{{ describe(slots.error()) }}</span></div>
      } @else if (slots.hasValue()) {
        <div class="days">
          @for (day of shownDays(); track day.label) {
            <div class="day">
              <span class="label">{{ day.label }}</span>
              @for (s of day.slots; track s.start) {
                <button type="button" class="btn slot" [class.outside]="s.fitsHours === false" [disabled]="!s.available || booking.isLoading()" (click)="book(s.start)" data-testid="visit-slot"
                        [attr.title]="s.fitsHours === false ? 'Outside your contact hours' : null">
                  {{ s.start | date: 'h:mm a' }}
                </button>
              }
            </div>
          }
        </div>
        @if (days().length > shownDays().length) {
          <div class="actions"><button type="button" class="btn" (click)="showAll.set(true)" data-testid="visit-more">More days</button></div>
        }
      } @else {
        <p class="muted">Loading times…</p>
      }
      @if (booking.error()) { <div class="warning" role="alert"><span>{{ describe(booking.error()) }}</span></div> }
    }
  `,
  styles: `
    .days { display: grid; grid-template-columns: repeat(auto-fill, minmax(9rem, 1fr)); gap: .5rem; margin-top: .5rem; }
    .day { display: grid; gap: .3rem; align-content: start; }
    .label { font-weight: 600; font-size: .9rem; }
    .slot { justify-content: center; }
    .slot.outside { opacity: .55; border-style: dashed; }
    .small { font-size: .85rem; }
    .suggested { display: flex; align-items: center; justify-content: space-between; gap: 1rem; padding: .6rem .8rem; margin-bottom: .6rem;
                 border-radius: var(--bp-radius-md); background: var(--bp-accent-soft); }
  `,
})
export class VisitBookingComponent {
  readonly projectId = input.required<string>();
  readonly visit = input<VisitInfo | null | undefined>(null);
  readonly booked = output<VisitInfo>();
  /** 4b: a time the customer picked on a scheduling call (from the confirm link's ?visit=). Confirmed only here, by them. */
  readonly suggested = input<string | undefined>(undefined);
  protected readonly suggestedSlot = computed(() => {
    const want = this.suggested();
    if (!want || !this.slots.hasValue()) return null;
    const t = new Date(want).getTime();
    return this.slots.value().find((s) => s.available && new Date(s.start).getTime() === t) ?? null;
  });

  protected readonly changing = signal(false);
  /** A week's worth first, so the card doesn't scroll; the rest on request. */
  protected readonly showAll = signal(false);
  protected readonly anyOutside = computed(() => this.slots.hasValue() && this.slots.value().some((s) => s.fitsHours === false));
  protected readonly shownDays = computed(() => (this.showAll() ? this.days() : this.days().slice(0, 6)));
  protected readonly slots = httpResource<Slot[]>(() =>
    !this.visit() || this.changing() ? `/api/projects/${encodeURIComponent(this.projectId())}/visit/slots` : undefined,
  );
  protected readonly days = computed(() => {
    const groups = new Map<string, Slot[]>();
    for (const s of this.slots.hasValue() ? this.slots.value() : []) {
      const label = new Date(s.start).toLocaleDateString(undefined, { weekday: 'short', month: 'short', day: 'numeric' });
      groups.set(label, [...(groups.get(label) ?? []), s]);
    }
    return [...groups].map(([label, slots]) => ({ label, slots }));
  });

  private readonly request = signal<{ start: string; n: number } | null>(null);
  private n = 0;
  protected readonly booking = httpResource<VisitInfo>(() => {
    const r = this.request();
    return r ? { url: `/api/projects/${encodeURIComponent(this.projectId())}/visit`, method: 'POST', body: { start: r.start } } : undefined;
  });

  constructor() {
    effect(() => {
      if (!this.booking.hasValue()) return;
      this.booked.emit(this.booking.value()!);
      this.changing.set(false);
      this.request.set(null);
    });
  }

  protected book(start: string): void {
    this.request.set({ start, n: ++this.n });
  }

  protected describe(err: unknown): string {
    return describeError(err as Parameters<typeof describeError>[0]);
  }
}
