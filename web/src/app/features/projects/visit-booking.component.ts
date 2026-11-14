import { Component, computed, effect, input, output, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { httpResource } from '@angular/common/http';
import { describeError } from '../../core/api/describe-error';
import { VisitInfo } from '../quote/guided';

interface Slot {
  start: string;
  available: boolean;
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
      <p>Pick a time for the measure visit. We confirm the sizes on site; your price per unit stays held.</p>
      @if (slots.error()) {
        <div class="warning" role="alert"><span>{{ describe(slots.error()) }}</span></div>
      } @else if (slots.hasValue()) {
        <div class="days">
          @for (day of days(); track day.label) {
            <div class="day">
              <span class="label">{{ day.label }}</span>
              @for (s of day.slots; track s.start) {
                <button type="button" class="btn slot" [disabled]="!s.available || booking.isLoading()" (click)="book(s.start)" data-testid="visit-slot">
                  {{ s.start | date: 'h:mm a' }}
                </button>
              }
            </div>
          }
        </div>
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
  `,
})
export class VisitBookingComponent {
  readonly projectId = input.required<string>();
  readonly visit = input<VisitInfo | null | undefined>(null);
  readonly booked = output<VisitInfo>();

  protected readonly changing = signal(false);
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
