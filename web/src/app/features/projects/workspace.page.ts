import { Component, computed, effect, inject, input, linkedSignal, signal } from '@angular/core';
import { CurrencyPipe, DatePipe, DecimalPipe } from '@angular/common';
import { httpResource } from '@angular/common/http';
import { Router, RouterLink } from '@angular/router';
import { describeError } from '../../core/api/describe-error';
import { GuidedStepsComponent } from '../quote/guided-steps.component';
import { GROUP_TITLES, GuidedLine, Project, STAGES, VisitInfo, layoutSketch } from '../quote/guided';
import { VisitBookingComponent } from './visit-booking.component';
import { leaveFor } from './pay.page';
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
  imports: [CurrencyPipe, DatePipe, DecimalPipe, RouterLink, GuidedStepsComponent, ConciergePanelComponent, ProgressTrackerComponent, VisitBookingComponent],
  template: `
    <div class="page">
      @if (loaded.error()) {
        <div class="warning" role="alert"><span>{{ describe(loaded.error()) }}</span></div>
      } @else if (project(); as p) {
        <header class="page-head head">
          <div>
            <span class="eyebrow">Project · ZIP {{ p.zip }}@if (p.preparedBy) { · prepared by {{ p.preparedBy }} }</span>
            <h1 data-testid="project-title">{{ p.title }}</h1>
          </div>
          <a routerLink="/projects" class="btn">My projects</a>
        </header>

        <bp-progress-tracker [steps]="journey()" [current]="trackerCurrent()" (selected)="goTo($event)" />

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
                @if (p.stage === 'confirm') {
                  <p data-testid="confirm-measured">The measure visit changed your total to {{ q.total | currency: q.currency }} (same prices per unit, measured sizes).</p>
                  @if (features.isCustomer()) {
                    <div class="actions">
                      <button type="button" class="btn btn-primary" (click)="confirm()" [disabled]="busy()" data-testid="confirm-quote">Confirm new total</button>
                    </div>
                  } @else {
                    <p class="muted small">Waiting for the customer to confirm.</p>
                  }
                } @else if (q.held && (p.stage === 'install' || p.stage === 'done')) {
                  @if (p.stage === 'done') {
                    <p class="ok" data-testid="installed">Installed. Thank you for building with us!</p>
                  } @else {
                    <p class="ok" data-testid="order-confirmed">Measured and confirmed: this is your order.
                      @if (p.install?.scheduledFor) { Installation is scheduled for {{ p.install!.scheduledFor | date: 'EEEE, MMM d' }}. } @else { We'll schedule the installation with you next. }</p>
                  }
                  <ol class="install" data-testid="install-steps">
                    @for (s of installSteps; track s.id; let i = $index) {
                      <li [class.done]="installIndex() > i || p.stage === 'done'" [class.current]="installIndex() === i && p.stage !== 'done'">
                        <span>{{ s.label }}</span>
                        @if (s.id === 'scheduled' && p.install?.scheduledFor) {
                          <span class="muted small">for {{ p.install!.scheduledFor | date: 'MMM d' }}</span>
                        } @else if (installDate(s.id); as at) { <span class="muted small">{{ at | date: 'MMM d' }}</span> }
                      </li>
                    }
                  </ol>
                } @else if (q.held) {
                  <p class="ok" data-testid="held">Deposit of {{ p.deposit!.amount | currency: p.deposit!.currency }} paid on {{ p.deposit!.paidAt | date: 'mediumDate' }}. Your price is held; next is the measure visit.</p>
                  @if (p.stage === 'measure') {
                    <bp-visit-booking [projectId]="p.id" [visit]="p.visit" (booked)="visitBooked($event)" />
                  }
                } @else if (q.status === 'accepted' && q.current && !q.expired) {
                  <p data-testid="accepted">Accepted on {{ q.acceptedAt | date: 'medium' }}. Pay a {{ depositPercent() }}% deposit to hold this price; until then it can change.</p>
                  @if (p.deposit?.status === 'unknown') {
                    <p class="muted small" data-testid="deposit-checking">We're confirming your last payment with the payment provider.</p>
                  } @else if (p.deposit?.status === 'failed') {
                    <p class="warning" data-testid="deposit-failed"><span>Your last payment didn't go through; nothing was charged. You can try again.</span></p>
                  }
                  @if (!features.isCustomer()) {
                    <p class="muted small" data-testid="customer-pays">The customer pays the deposit in their own account.</p>
                  } @else {
                  <div class="actions">
                    <button type="button" class="btn btn-primary" (click)="payDeposit()" [disabled]="busy()" data-testid="pay-deposit">
                      Pay {{ depositPercent() }}% deposit@if (p.deposit) { ({{ p.deposit.amount | currency: p.deposit.currency }}) }
                    </button>
                  </div>
                  }
                } @else if (q.expired) {
                  <p class="warning"><span>This quote expired.</span></p>
                  <button type="button" class="btn btn-primary" (click)="issue()" [disabled]="busy()">Get an updated quote</button>
                } @else if (!q.current) {
                  <p class="warning" data-testid="outdated"><span>Your answers changed since this quote.</span></p>
                  <button type="button" class="btn btn-primary" (click)="issue()" [disabled]="busy() || !p.preview?.complete" data-testid="reissue">Get an updated quote</button>
                } @else if (!features.isCustomer()) {
                  <p class="muted small" data-testid="customer-accepts">Valid until {{ q.validUntil | date: 'mediumDate' }}. The customer sees this quote under My projects and accepts it there.</p>
                } @else {
                  <div class="actions">
                    <button type="button" class="btn btn-primary" (click)="accept()" [disabled]="busy()" data-testid="accept-quote">Accept quote</button>
                    <button type="button" class="btn" (click)="ask(q.number)" data-testid="ask-quote">Ask about this quote</button>
                  </div>
                  <p class="muted small">Valid until {{ q.validUntil | date: 'mediumDate' }}. After accepting, a {{ depositPercent() }}% deposit holds the price. You confirm every step yourself: the concierge can prepare things, but never accepts or pays for you.</p>
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
    .install { list-style: none; display: grid; grid-template-columns: repeat(4, minmax(0, 1fr)); gap: .4rem; padding: 0; margin: .75rem 0 0; }
    .install li { display: grid; gap: .1rem; padding: .5rem .6rem; border-radius: .5rem; border: 1px solid var(--bp-border); text-align: center; font-size: .9rem; }
    .install li.done { background: var(--bp-accent-soft); }
    .install li.current { border-color: var(--bp-accent); font-weight: 700; }
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

  private readonly router = inject(Router);
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
    computation: (stage) => (stage && !['discover', 'configure'].includes(stage) ? 'quote' : 'measure'),
  });
  /** The whole journey in one tracker: done before the project, this project's screens, then what follows the quote. */
  private readonly quoteGroups = computed(() => new Set<string>(['measure', ...(this.project()?.quote?.lines ?? []).map((l) => (l as { group?: string }).group ?? '')]));
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
      // After the deposit the step screens aren't shown, so the quote's own line groups say which steps it had.
      .filter((g) => this.pages().some((p) => p.group === g.id) || (this.locked() && this.quoteGroups().has(g.id)))
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
  /** Paid deposit onwards: the answers are the order (6a). */
  protected readonly locked = computed(() => ['measure', 'confirm', 'install', 'done'].includes(this.project()?.stage ?? ''));
  /** Where the tracker is: the screen while configuring, then the stage after the quote. */
  protected readonly trackerCurrent = computed(() => {
    const stage = this.project()?.stage;
    if (stage === 'deposit') return 'deposit';
    if (stage === 'measure' || stage === 'confirm') return 'measure-visit';
    if (stage === 'install' || stage === 'done') return 'install';
    return this.currentGroup();
  });
  protected readonly depositPercent = computed(() => this.project()?.deposit?.percent ?? 5);
  protected readonly missingSteps = computed(() => {
    const missing = this.project()?.preview?.missing ?? [];
    // The step names the customer saw ("Sink"), not the group ids ("ct-sink").
    const label = (id: string) => this.screens().find((s) => s.id === id)?.label ?? id;
    return missing.length ? missing.map(label).join(', ') : 'the measurements';
  });

  protected move(delta: number): void {
    const next = this.pages()[this.pageIndex() + delta];
    if (next) this.page.set(next.id);
  }
  protected readonly sketch = computed(() => layoutSketch(this.project()?.answers));
  protected readonly askText = signal<string | null>(null);

  /** Quote and accept are POSTs fired by the customer's buttons. */
  private readonly action = signal<{ kind: 'quote' | 'accept' | 'deposit' | 'confirm'; n: number } | null>(null);
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
      if (!this.actionResult.hasValue()) return;
      const result = this.actionResult.value() as unknown as Project & { checkoutUrl?: string };
      if (this.action()?.kind === 'deposit' && result.checkoutUrl) {
        // Stripe Checkout is another site; our test-mode page is a route here.
        if (/^https?:/.test(result.checkoutUrl)) leaveFor.go(result.checkoutUrl);
        else void this.router.navigateByUrl(result.checkoutUrl);
      } else {
        this.project.set(result);
      }
      this.action.set(null);
    });
  }

  protected issue(): void {
    this.action.set({ kind: 'quote', n: ++this.n });
    this.page.set('quote');
  }

  /** 6c: the installation steps the customer follows. */
  protected readonly installSteps = [
    { id: 'scheduled', label: 'Scheduled' },
    { id: 'fabricating', label: 'Being made' },
    { id: 'installing', label: 'Installing' },
    { id: 'done', label: 'Done' },
  ] as const;
  protected readonly installIndex = computed(() => {
    const step = this.project()?.install?.step;
    return step ? this.installSteps.findIndex((s) => s.id === step) : -1;
  });
  /** When a step happened (the last time it was recorded). */
  protected installDate(step: string): string | null {
    const events = this.project()?.install?.events ?? [];
    return [...events].reverse().find((e) => e.step === step)?.at ?? null;
  }

  /** 6b: the customer accepts the measured quote. */
  protected confirm(): void {
    this.action.set({ kind: 'confirm', n: ++this.n });
  }

  /** 6b: show the booked visit without reloading the project. */
  protected visitBooked(visit: VisitInfo): void {
    this.project.update((p) => (p ? { ...p, visit } : p));
  }

  /** Start (or resume) the 5 % deposit, then the payment page. */
  protected payDeposit(): void {
    this.action.set({ kind: 'deposit', n: ++this.n });
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
