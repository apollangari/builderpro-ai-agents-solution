import { Component, computed, effect, inject, input, linkedSignal, signal } from '@angular/core';
import { CurrencyPipe, DatePipe, DecimalPipe } from '@angular/common';
import { httpResource } from '@angular/common/http';
import { RouterLink } from '@angular/router';
import { describeError } from '../../core/api/describe-error';
import { GuidedStepsComponent } from '../quote/guided-steps.component';
import { GROUP_TITLES, GuidedLine, Project, STAGES, layoutSketch } from '../quote/guided';
import { ConciergePanelComponent } from './concierge-panel.component';
import { ProgressTrackerComponent, TrackerStep } from '../../shared/progress-tracker.component';
import { StoreFeatures } from '../../core/tenant/store-features';

/**
 * The project workspace (2n-f), the owner's mockup: stage rail, the concierge beside the work,
 * a layout sketch (estimated until measured), the selection, the editable steps, and the numbered
 * quote with Accept. Everything is the server's project: reload or another device shows the same.
 * Accept is the customer's button; the API refuses it from staff and from any agent.
 */
@Component({
  selector: 'bp-workspace-page',
  imports: [CurrencyPipe, DatePipe, DecimalPipe, RouterLink, GuidedStepsComponent, ConciergePanelComponent, ProgressTrackerComponent],
  template: `
    <div class="page">
      @if (loaded.error()) {
        <div class="warning" role="alert"><span>{{ describe(loaded.error()) }}</span></div>
      } @else if (project(); as p) {
        <header class="page-head head">
          <div>
            <span class="eyebrow">Project · ZIP {{ p.zip }}</span>
            <h1 data-testid="project-title">{{ p.title }}</h1>
          </div>
          <a routerLink="/projects" class="btn">My projects</a>
        </header>

        <bp-progress-tracker [steps]="journey()" [current]="locked() ? 'deposit' : currentGroup()" (selected)="goTo($event)" />

        <div class="layout" [class.no-chat]="!features.anyChat()">
          @if (features.anyChat()) {
            <bp-concierge-panel class="chat" [projectId]="p.id" [askText]="askText()" [helpOnly]="!features.concierge()" (projectChanged)="refresh()" />
          }

          <div class="work">
            <p class="selection" data-testid="selection-card">
              @if (p.preview; as q) {
                <strong>{{ q.lines[0]?.name }}</strong>
                @for (l of optionLines(q.lines); track l.sku) { <span class="muted"> · {{ l.name }}</span> }
              }
            </p>

            <!-- One step per screen (owner): Measure, Options, Protection, Quote -->

            @if (subStep()) { <p class="substep" data-testid="substep">{{ subStep() }}</p> }
            <div class="screen" [class.with-sketch]="page() === 'measure'">
              @if (p.sku && !locked()) {
                <bp-guided-steps [zip]="p.zip" [sku]="p.sku" [projectId]="p.id" [project]="loadedValue()" [page]="page()" (projectChange)="project.set($event)" (screens)="screens.set($event)" />
              }

              @if (page() === 'measure') {
                <section class="card" data-testid="layout-card">
                  <div class="card-row">
                    <h2 class="card-title">Layout</h2>
                    <span class="badge">Estimated</span>
                  </div>
                  @if (sketch().rects.length) {
                    <svg [attr.viewBox]="'-6 -6 ' + (sketch().width + 12) + ' ' + (sketch().height + 12)" class="sketch" role="img"
                         [attr.aria-label]="'Countertop layout, about ' + (p.preview?.measurement?.values?.['sqft'] ?? '') + ' square feet'">
                      @for (r of sketch().rects; track $index) {
                        <rect [attr.x]="r.x" [attr.y]="r.y" [attr.width]="r.w" [attr.height]="r.h" rx="2" [class.island]="r.kind === 'island'" />
                      }
                    </svg>
                  } @else {
                    <p class="muted">Your layout appears once the measurements are in.</p>
                  }
                  @if (p.preview; as q) {
                    <p class="muted" data-testid="layout-summary">
                      @if (q.measurement.values['sqft'] !== undefined) { ≈ {{ q.measurement.values['sqft'] | number: '1.0-2' }} sq ft }
                      @if (q.measurement.values['edgeFt']) { · {{ q.measurement.values['edgeFt'] }} ft of edge }
                    </p>
                  }
                  <p class="muted small">Final numbers are confirmed at the measurement visit.</p>
                </section>
              }

              @if (page() === 'quote') {
              <section class="card quote" data-testid="quote-card">
              @if (p.quote; as q) {
                <div class="card-row">
                  <h2 class="card-title" data-testid="quote-number">Quote Q-{{ q.number }}</h2>
                  <span class="muted small">Priced by your store's pricing engine · valid until {{ q.validUntil | date: 'mediumDate' }}</span>
                </div>
                @for (grp of groups(q.lines); track grp.group) {
                  <h3 class="group-title">{{ titles[grp.group] }}</h3>
                  @for (l of grp.lines; track l.sku) {
                    <div class="line"><span>{{ l.name }}</span><span>{{ l.lineTotal | currency: q.currency }}</span></div>
                  }
                }
                <dl class="totals">
                  <dt>Tax</dt><dd>{{ q.tax | currency: q.currency }}</dd>
                  <dt class="total">Total</dt><dd class="total" data-testid="quote-total">{{ q.total | currency: q.currency }}</dd>
                </dl>
                @if (q.status === 'accepted') {
                  <p class="ok" data-testid="accepted">Accepted on {{ q.acceptedAt | date: 'medium' }}. The price is held; the deposit is the next step.</p>
                } @else if (q.expired) {
                  <p class="warning"><span>This quote expired.</span></p>
                  <button type="button" class="btn btn-primary" (click)="issue()" [disabled]="busy()">Get an updated quote</button>
                } @else if (!q.current) {
                  <p class="warning" data-testid="outdated"><span>Your answers changed since this quote.</span></p>
                  <button type="button" class="btn btn-primary" (click)="issue()" [disabled]="busy() || !p.preview?.complete" data-testid="reissue">Get an updated quote</button>
                } @else {
                  <div class="actions">
                    <button type="button" class="btn btn-primary" (click)="accept()" [disabled]="busy()" data-testid="accept-quote">Accept quote</button>
                    <button type="button" class="btn" (click)="ask(q.number)" data-testid="ask-quote">Ask about this quote</button>
                  </div>
                  <p class="muted small">Accepting holds this price until {{ q.validUntil | date: 'mediumDate' }}. You confirm every step yourself: the concierge can prepare things, but never accepts or pays for you.</p>
                }
              } @else {
                <h2 class="card-title">Your quote</h2>
                @if (p.preview?.complete) {
                  <p>Everything's answered: {{ p.preview!.total | currency: p.preview!.currency }} with tax.</p>
                  <button type="button" class="btn btn-primary" (click)="issue()" [disabled]="busy()" data-testid="get-quote">Get my quote</button>
                } @else {
                  <p class="muted" data-testid="quote-missing">Still to answer: {{ missingSteps() }}. Use Back, or tell the concierge.</p>
                }
              }
              @if (actionError()) {
                <div class="warning" role="alert" data-testid="action-error"><span>{{ actionError() }}</span></div>
              }
              </section>
              }
            </div>

            @if (!locked()) {
              <div class="pager">
                <button type="button" class="btn" (click)="move(-1)" [disabled]="pageIndex() === 0" data-testid="page-back">Back</button>
                @if (pageIndex() < pages().length - 1) {
                  <button type="button" class="btn btn-primary" (click)="move(1)" data-testid="page-next">Next: {{ pages()[pageIndex() + 1].label }}</button>
                }
              </div>
            }
          </div>
        </div>
      } @else {
        <p class="muted">Loading your project…</p>
      }
    </div>
  `,
  styles: `
    .head { display: flex; justify-content: space-between; align-items: end; gap: 1rem; }
    .rail { display: flex; flex-wrap: wrap; gap: .5rem; list-style: none; padding: 0; margin: 0 0 1rem; }
    .rail li { padding: .4rem .9rem; border-radius: 999px; border: 1px solid var(--bp-border); color: var(--bp-ink-muted); font-size: .9rem; min-height: 2rem; }
    .rail li.done { background: var(--bp-accent-soft); color: var(--bp-accent); border-color: transparent; }
    .rail li.current { background: var(--bp-accent); color: var(--bp-on-accent); border-color: var(--bp-accent); font-weight: 600; }
    .layout { display: grid; gap: 1rem; grid-template-columns: minmax(17rem, 1fr) minmax(0, 2.6fr); align-items: stretch; }
    @media (max-width: 960px) { .layout { grid-template-columns: 1fr; } }
    .layout.no-chat { grid-template-columns: 1fr; }
    .work { display: grid; gap: 1rem; }
    .selection { margin: 0; }
    .tabs { display: flex; gap: .35rem; flex-wrap: wrap; border-bottom: 1px solid var(--bp-border); }
    .tab { min-height: var(--bp-tap); padding: 0 1rem; border: 0; border-bottom: 3px solid transparent; background: none; color: var(--bp-ink-muted); font: inherit; cursor: pointer; }
    .tab.active { color: var(--bp-accent); border-bottom-color: var(--bp-accent); font-weight: 600; }
    .tab:disabled { opacity: .5; cursor: default; }
    .screen { display: grid; gap: 1rem; align-items: start; }
    .screen.with-sketch { grid-template-columns: minmax(0, 2.4fr) minmax(12rem, 1fr); }
    @media (max-width: 1100px) { .screen.with-sketch { grid-template-columns: 1fr; } }
    .pager { display: flex; justify-content: flex-end; gap: .6rem; }
    .substep { margin: 0; font-weight: 600; color: var(--bp-accent); }
    .card-row { display: flex; justify-content: space-between; align-items: baseline; gap: .75rem; flex-wrap: wrap; }
    .sketch { width: 100%; max-height: 12rem; }
    .sketch rect { fill: var(--bp-surface-muted); stroke: var(--bp-ink-muted); stroke-width: 1; vector-effect: non-scaling-stroke; }
    .sketch rect.island { fill: var(--bp-accent-soft); }
    .sel-name { font-weight: 700; margin: 0 0 .25rem; }
    .small { font-size: .85rem; }
    .link { color: var(--bp-accent); }
    .quote { border: 2px solid var(--bp-accent); }
    .group-title { margin: .5rem 0 0; font-size: .9rem; color: var(--bp-ink-muted); }
    .line { display: flex; justify-content: space-between; gap: 1rem; padding: .3rem 0; border-bottom: 1px solid var(--bp-border); }
    .totals { display: grid; grid-template-columns: 1fr auto; gap: .2rem 1rem; margin: .5rem 0; }
    .totals dd { margin: 0; text-align: right; }
    .totals .total { font-weight: 700; font-size: 1.15rem; }
    .actions { display: flex; gap: .6rem; flex-wrap: wrap; justify-content: flex-end; }
    .ok { padding: .6rem .8rem; border-radius: var(--bp-radius-md); background: var(--bp-accent-soft); color: var(--bp-accent); font-weight: 600; }
  `,
})
export class WorkspacePage {
  /** The store's package (1h): concierge, help-only chat, or none. */
  protected readonly features = inject(StoreFeatures);

  /** Bound from the route (/projects/:id). */
  readonly id = input.required<string>();

  protected readonly stages = STAGES;
  protected readonly titles = GROUP_TITLES;

  protected readonly loaded = httpResource<Project>(() => `/api/projects/${encodeURIComponent(this.id())}`);
  /** The project as last seen from the server: the GET, then every save / quote / accept answer. */
  protected readonly project = linkedSignal<Project | null>(() => (this.loaded.hasValue() ? this.loaded.value()! : null));

  protected readonly stageIndex = computed(() => Math.max(0, STAGES.findIndex((s) => s.id === this.project()?.stage)));

  /** One screen per step (owner): Measure, then one per option step of this product, then Quote. */
  protected readonly screens = signal<{ id: string; label: string; group: string }[]>([{ id: 'measure', label: 'Measure', group: 'measure' }]);
  protected readonly pages = computed(() => [...this.screens(), { id: 'quote', label: 'Quote', group: 'quote' }]);
  /** Tracker steps group the screens: every option screen is one "Options" step (owner: no scrolling tracker). */
  protected readonly currentGroup = computed(() => this.pages().find((p) => p.id === this.page())?.group ?? 'measure');
  /** "Edge style · 1 of 4" under the Options / Protection heading. */
  protected readonly subStep = computed(() => {
    const group = this.currentGroup();
    const inGroup = this.pages().filter((p) => p.group === group);
    const i = inGroup.findIndex((p) => p.id === this.page());
    return inGroup.length > 1 && i >= 0 ? `${inGroup[i].label} · ${i + 1} of ${inGroup.length}` : null;
  });
  /** The project as last READ from the server: the steps restore from it (e.g. after the concierge saved). */
  protected readonly loadedValue = computed(() => (this.loaded.hasValue() ? this.loaded.value()! : null));
  private readonly stage = computed(() => this.project()?.stage ?? null);
  /** Changes screen only when the stage changes (a concierge save in the same stage keeps you where you are). */
  protected readonly page = linkedSignal<string | null, string>({
    source: this.stage,
    computation: (stage) => (stage === 'quote' || stage === 'deposit' ? 'quote' : 'measure'),
  });
  /** The whole journey in one tracker: done before the project, this project's screens, then what follows the quote. */
  protected readonly journey = computed<TrackerStep[]>(() => [
    { id: 'zip', label: 'ZIP' },
    { id: 'type', label: 'Type' },
    { id: 'product', label: 'Product' },
    ...[
      { id: 'measure', label: 'Measure' },
      { id: 'options', label: 'Options' },
      { id: 'protection', label: 'Protection' },
      { id: 'quote', label: 'Quote' },
    ]
      .filter((g) => this.pages().some((p) => p.group === g.id))
      .map((g) => ({ ...g, clickable: !this.locked() })),
    { id: 'deposit', label: 'Deposit' },
    { id: 'measure-visit', label: 'Visit' },
    { id: 'install', label: 'Install' },
  ]);

  /** A tracker step opens the first screen of its group. */
  protected goTo(group: string): void {
    const first = this.pages().find((p) => p.group === group);
    if (first) this.page.set(first.id);
  }
  protected readonly pageIndex = computed(() => this.pages().findIndex((p) => p.id === this.page()));
  /** Accepted: answers can't change, only the quote is shown. */
  protected readonly locked = computed(() => this.project()?.stage === 'deposit');
  protected readonly missingSteps = computed(() => {
    const missing = this.project()?.preview?.missing ?? [];
    return missing.length ? missing.join(', ') : 'the measurements';
  });

  protected move(delta: number): void {
    const next = this.pages()[this.pageIndex() + delta];
    if (next) this.page.set(next.id);
  }
  protected readonly sketch = computed(() => layoutSketch(this.project()?.answers));
  protected readonly askText = signal<string | null>(null);

  /** Quote and accept are POSTs fired by the customer's buttons. */
  private readonly action = signal<{ kind: 'quote' | 'accept'; n: number } | null>(null);
  private readonly actionResult = httpResource<Project>(() => {
    const a = this.action();
    const p = this.project();
    return a && p ? { url: `/api/projects/${encodeURIComponent(p.id)}/${a.kind}`, method: 'POST', body: {} } : undefined;
  });
  protected readonly busy = computed(() => this.actionResult.isLoading());
  protected readonly actionError = computed(() => (this.actionResult.error() ? describeError(this.actionResult.error() as never) : null));

  private n = 0;

  constructor() {
    effect(() => {
      if (this.actionResult.hasValue()) {
        this.project.set(this.actionResult.value()!);
        this.action.set(null);
      }
    });
  }

  protected issue(): void {
    this.action.set({ kind: 'quote', n: ++this.n });
    this.page.set('quote');
  }

  protected accept(): void {
    this.action.set({ kind: 'accept', n: ++this.n });
  }

  protected ask(number: number): void {
    this.askText.set(`I have a question about quote Q-${number}: `);
  }

  /** The concierge changed the project: read it again. */
  protected refresh(): void {
    this.loaded.reload();
  }

  protected optionLines(lines: GuidedLine[]): GuidedLine[] {
    return lines.filter((l) => l.group === 'options').slice(0, 4);
  }

  protected groups(lines: GuidedLine[]): { group: GuidedLine['group']; lines: GuidedLine[] }[] {
    const order: GuidedLine['group'][] = ['product', 'minimum', 'options', 'protection'];
    return order.map((group) => ({ group, lines: lines.filter((l) => l.group === group) })).filter((g) => g.lines.length);
  }

  protected describe(err: unknown): string {
    return describeError(err as Parameters<typeof describeError>[0]);
  }
}
