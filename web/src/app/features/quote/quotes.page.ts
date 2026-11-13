import { Component, input } from '@angular/core';
import { CurrencyPipe, DatePipe } from '@angular/common';
import { httpResource } from '@angular/common/http';
import { RouterLink } from '@angular/router';
import { describeError } from '../../core/api/describe-error';

/** GET /api/quotes item. Mirrors QuoteSummaryDto in C#. */
export interface QuoteSummary {
  id: string;
  status: string;
  customerId: string | null;
  currency: string;
  lineCount: number;
  total: number;
  createdAt: string;
}

/** "My quotes" (2m): a customer's own quotes, or every quote of the store for staff (server decides). */
@Component({
  selector: 'bp-quotes-page',
  imports: [CurrencyPipe, DatePipe, RouterLink],
  template: `
    <div class="page">
      <header class="page-head">
        <span class="eyebrow">Quotes</span>
        <h1>My quotes</h1>
      </header>
      @if (saved()) {
        <div class="saved card" role="status" data-testid="saved">Your quote was saved.</div>
      }
      @if (quotes.error()) {
        <div class="warning" role="alert"><span>{{ describe(quotes.error()) }}</span></div>
      } @else if (quotes.hasValue() && quotes.value().length === 0) {
        <div class="card"><p>No quotes yet. <a routerLink="/quote">Start one</a>.</p></div>
      } @else {
        <div class="list">
          @for (q of quotes.value(); track q.id) {
            <article class="card row" data-testid="quote-row" [class.highlight]="q.id === saved()">
              <div>
                <strong>{{ q.createdAt | date: 'medium' }}</strong>
                <span class="muted">{{ q.lineCount }} {{ q.lineCount === 1 ? 'line' : 'lines' }} · {{ q.status }}</span>
              </div>
              <span class="total">{{ q.total | currency: q.currency }}</span>
            </article>
          }
        </div>
      }
      <p><a routerLink="/quote" class="btn btn-primary">New quote</a></p>
    </div>
  `,
  styles: `
    .list { display: grid; gap: .6rem; margin-bottom: 1rem; }
    .row { display: flex; justify-content: space-between; align-items: center; gap: 1rem; }
    .row div { display: grid; gap: .15rem; }
    .row.highlight { border-color: var(--bp-accent); background: var(--bp-accent-soft); }
    .muted { color: var(--bp-ink-muted); font-size: .85rem; }
    .total { font-family: var(--bp-font-heading); font-size: 1.2rem; font-weight: 700; color: var(--bp-accent); }
    .saved { margin-bottom: 1rem; background: var(--bp-accent-soft); border-color: var(--bp-accent); font-weight: 600; }
  `,
})
export class QuotesPage {
  /** ?saved=<id> after saving a quote. */
  readonly saved = input<string | undefined>();
  protected readonly quotes = httpResource<QuoteSummary[]>(() => '/api/quotes', { defaultValue: [] });
  protected readonly describe = describeError;
}
