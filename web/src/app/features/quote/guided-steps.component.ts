import { Component, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import { CurrencyPipe, DecimalPipe } from '@angular/common';
import { httpResource } from '@angular/common/http';
import { Router } from '@angular/router';
import { describeError } from '../../core/api/describe-error';
import {
  Answer, Answers, GROUP_TITLES, GuidedLine, GuidedQuote, Length, MeasureForm, OptionGroup, ProductOptions, Project, SectionForm,
  answersFromChoices, defaultMeasureForm, formFromAnswers, measurementsBody, missingLabels, problemMessages, toChoices,
} from './guided';

/** Kitchen layouts with a top-down picture: counter runs (and islands) as rects in a 60 x 44 box. */
const LAYOUTS: { id: string; label: string; runs: number[][]; islands: number[][] }[] = [
  { id: 'straight', label: 'Straight', runs: [[4, 4, 52, 9]], islands: [] },
  { id: 'galley', label: 'Galley', runs: [[4, 4, 52, 9], [4, 31, 52, 9]], islands: [] },
  { id: 'l', label: 'L-shaped', runs: [[4, 4, 52, 9], [4, 13, 9, 27]], islands: [] },
  { id: 'l-island', label: 'L with island', runs: [[4, 4, 52, 9], [4, 13, 9, 27]], islands: [[25, 24, 24, 10]] },
  { id: 'u', label: 'U-shaped', runs: [[4, 4, 52, 9], [4, 13, 9, 27], [47, 13, 9, 27]], islands: [] },
  { id: 'u-island', label: 'U with island', runs: [[4, 4, 52, 9], [4, 13, 9, 27], [47, 13, 9, 27]], islands: [[20, 25, 20, 9]] },
  { id: 'peninsula', label: 'Peninsula', runs: [[4, 4, 52, 9], [4, 13, 9, 27], [13, 31, 26, 9]], islands: [] },
];

/**
 * Guided quote steps 4-7 (2n-b..e) for one product at one ZIP: measurements, options, protection,
 * quote. Everything the customer sees as a quantity or price comes from POST /api/quotes/guided/preview,
 * re-asked whenever an answer changes (an httpResource driven by the request signal).
 */
@Component({
  selector: 'bp-guided-steps',
  imports: [CurrencyPipe, DecimalPipe],
  template: `
    @if (options.isLoading()) {
      <p class="muted">Loading the next steps…</p>
    } @else if (options.error()) {
      <div class="warning" role="alert"><span>{{ describe(options.error()) }}</span></div>
    } @else if (opts(); as o) {
      <!-- Step 4: measurements -->
      @if (page() === 'all' || page() === 'measure') {
      <section class="card step" data-testid="step-measure">
        <h2 class="card-title">Measurements</h2>
        @switch (o.measure) {
          @case ('countertop') {
            <fieldset class="layouts" data-testid="layout-picker">
              <legend>Layout</legend>
              @for (l of layouts; track l.id) {
                <label class="layout" [class.active]="form().countertop.layout === l.id" [attr.data-testid]="'layout-' + l.id">
                  <input type="radio" name="layout" class="sr-only" [checked]="form().countertop.layout === l.id" (change)="setLayout(l.id)" />
                  <svg viewBox="0 0 60 44" aria-hidden="true">
                    <rect x="1" y="1" width="58" height="42" rx="3" class="room" />
                    @for (r of l.runs; track $index) { <rect [attr.x]="r[0]" [attr.y]="r[1]" [attr.width]="r[2]" [attr.height]="r[3]" rx="1" class="run" /> }
                    @for (r of l.islands; track $index) { <rect [attr.x]="r[0]" [attr.y]="r[1]" [attr.width]="r[2]" [attr.height]="r[3]" rx="1" class="isl" /> }
                  </svg>
                  <span>{{ l.label }}</span>
                </label>
              }
            </fieldset>
            <p class="muted small">Split each run into rectangles. A standard countertop is 25 in deep.</p>
            @for (part of parts; track part.key) {
              @if (part.key === 'countertop' || form().countertop[part.key].length) {
              <fieldset [attr.data-testid]="'part-' + part.key">
                <legend>{{ part.label }}</legend>
                @for (s of form().countertop[part.key]; track $index; let i = $index) {
                  <div class="row section">
                    <span class="muted">Section {{ i + 1 }}</span>
                    <label>Width
                      <input type="number" min="0" class="num" [value]="s.width.ft" (change)="setSection(part.key, i, 'ft', $event)" [attr.aria-label]="part.label + ' section ' + (i + 1) + ' width, feet'" /> ft
                      <input type="number" min="0" max="11" class="num" [value]="s.width.in" (change)="setSection(part.key, i, 'in', $event)" [attr.aria-label]="part.label + ' section ' + (i + 1) + ' width, inches'" /> in
                    </label>
                    <label>{{ part.key === 'backsplash' ? 'Height' : 'Depth' }}
                      <input type="number" min="0" class="num" [value]="s.depthIn" (change)="setSection(part.key, i, 'depth', $event)" [attr.aria-label]="part.label + ' section ' + (i + 1) + (part.key === 'backsplash' ? ' height' : ' depth') + ', inches'" /> in
                    </label>
                    <button type="button" class="btn" (click)="removeSection(part.key, i)" [attr.aria-label]="'Remove ' + part.label + ' section ' + (i + 1)">Remove</button>
                  </div>
                }
                <button type="button" class="btn" (click)="addSection(part.key)" [attr.data-testid]="'add-' + part.key">Add {{ part.label.toLowerCase() }} section</button>
              </fieldset>
              }
            }
            <div class="row">
              @if (!form().countertop.island.length) { <button type="button" class="btn" (click)="addSection('island')" data-testid="add-island">Add an island or peninsula</button> }
              @if (!form().countertop.backsplash.length) { <button type="button" class="btn" (click)="addSection('backsplash')" data-testid="add-backsplash">Add a backsplash</button> }
            </div>
          }
          @case ('roof') {
            <div class="row">
              <label>Footprint <input type="number" min="1" class="num wide" [value]="form().roof.footprintSqFt" (change)="setRoof('footprintSqFt', $event)" /> sq ft</label>
              <label>Pitch
                <select [value]="form().roof.pitchRise" (change)="setRoof('pitchRise', $event)">
                  @for (r of pitches; track r) { <option [value]="r">{{ r }}/12</option> }
                </select>
              </label>
              <label>Waste <input type="number" min="0" max="30" class="num" [value]="form().roof.wastePercent" (change)="setRoof('wastePercent', $event)" /> %</label>
              <label>Eaves + rakes <input type="number" min="0" class="num wide" [value]="form().roof.eavesAndRakesFt" (change)="setRoof('eavesAndRakesFt', $event)" /> ft</label>
            </div>
          }
          @case ('area') {
            @for (r of form().rooms; track $index; let i = $index) {
              <div class="row section">
                <span class="muted">Room {{ i + 1 }}</span>
                <label>Length <input type="number" min="0" class="num" [value]="r.lengthFt" (change)="setRoom(i, 'lengthFt', $event)" /> ft</label>
                <label>Width <input type="number" min="0" class="num" [value]="r.widthFt" (change)="setRoom(i, 'widthFt', $event)" /> ft</label>
                @if (form().rooms.length > 1) { <button type="button" class="btn" (click)="removeRoom(i)">Remove</button> }
              </div>
            }
            <button type="button" class="btn" (click)="addRoom()">Add a room</button>
          }
          @case ('count') {
            <label>How many? <input type="number" min="1" step="1" class="num" [value]="form().count" (change)="setCount($event)" data-testid="count-input" /></label>
          }
        }

        @if (pq(); as pqv) {
          @let m = pqv.measurement;
          <p class="measured" data-testid="measured">
            @switch (m.kind) {
              @case ('countertop') { {{ m.values['sqft'] | number: '1.0-2' }} sq ft in total, {{ m.values['edgeFt'] }} ft of finished edge }
              @case ('roof') { {{ m.values['bundles'] }} bundles ({{ m.values['squares'] }} squares), {{ m.values['underlaymentRolls'] }} underlayment rolls }
              @case ('area') { {{ m.values['roomSqFt'] | number: '1.0-2' }} sq ft of floor, {{ m.values['sqft'] }} sq ft with 10% waste }
              @case ('count') { {{ m.values['count'] }} }
            }
          </p>
          @if (o.minimumQuantity && m.values[m.productBasis] < o.minimumQuantity) {
            <p class="muted">Orders under {{ o.minimumQuantity }} {{ unitLabel(o.product.unit) }} are charged the {{ o.minimumQuantity }} {{ unitLabel(o.product.unit) }} minimum.</p>
          }
        }
        @for (msg of measureProblems(); track msg) {
          <div class="warning" role="alert" data-testid="measure-error"><span>{{ msg }}</span></div>
        }
      </section>

      }

      <!-- Steps 5 and 6: options, then protection -->
      @for (stage of stages; track stage.step) {
        @if (shownGroups(stage.step).length) {
          <section class="card step" [attr.data-testid]="'step-' + stage.step">
            @if (page() === 'all') { <h2 class="card-title">{{ stage.title }}</h2> }
            @for (g of shownGroups(stage.step); track g.id) {
              <fieldset [attr.data-testid]="'group-' + g.id">
                <legend>{{ g.label }} @if (g.required) { <span class="muted">(required)</span> }</legend>
                @if (g.help) { <p class="muted">{{ g.help }}</p> }
                @if (g.selection === 'single') {
                  <div class="choices">
                    @for (x of g.options; track x.sku) {
                      <label class="choice" [class.active]="singleSku(g.id) === x.sku">
                        <input type="radio" [name]="g.id" [checked]="singleSku(g.id) === x.sku" (change)="pickSingle(g.id, x.sku)" [attr.data-testid]="'opt-' + x.sku" />
                        @if (x.imageUrl) { <img [src]="x.imageUrl" alt="" width="120" height="75" /> }
                        <span>{{ x.name }}</span>
                        <span class="muted">{{ x.price === 0 ? 'Included' : (x.price | currency: x.currency) + ' / ' + unitLabel(x.unit) }}</span>
                      </label>
                    }
                    @if (g.noneLabel) {
                      <label class="choice" [class.active]="isNone(g.id)">
                        <input type="radio" [name]="g.id" [checked]="isNone(g.id)" (change)="pickSingle(g.id, null)" [attr.data-testid]="'none-' + g.id" />
                        <span>{{ g.noneLabel }}</span>
                      </label>
                    }
                  </div>
                } @else {
                  <div class="choices">
                    @for (x of g.options; track x.sku) {
                      <div class="choice qty">
                        <span>{{ x.name }}</span>
                        <span class="muted">{{ x.price | currency: x.currency }} each@if (x.included) { · {{ x.included }} included }</span>
                        <span class="stepper">
                          <button type="button" class="btn" (click)="bump(g.id, x.sku, -1)" [attr.aria-label]="'One fewer ' + x.name">−</button>
                          <output [attr.data-testid]="'count-' + x.sku">{{ countOf(g.id, x.sku) }}</output>
                          <button type="button" class="btn" (click)="bump(g.id, x.sku, 1)" [attr.aria-label]="'One more ' + x.name" [attr.data-testid]="'more-' + x.sku">+</button>
                        </span>
                      </div>
                    }
                    @if (g.noneLabel) {
                      <label class="choice">
                        <input type="checkbox" [checked]="isNone(g.id)" (change)="setNone(g.id, $any($event.target).checked)" [attr.data-testid]="'none-' + g.id" />
                        <span>{{ g.noneLabel }}</span>
                      </label>
                    }
                  </div>
                }
              </fieldset>
            }
          </section>
        }
      }

      <!-- Step 7: the quote (in a project, the workspace shows the quote card instead) -->
      @if ((!projectId() || showPreview()) && page() === 'all') {
      <section class="card step quote" data-testid="step-quote">
        <h2 class="card-title">Your quote <span class="muted">· ZIP {{ zip() }}</span></h2>
        @if (pq(); as q) {
          @for (grp of lineGroups(q.lines); track grp.group) {
            <h3 class="group-title">{{ titles[grp.group] }}</h3>
            <table class="lines">
              <tbody>
                @for (l of grp.lines; track l.sku) {
                  <tr data-testid="guided-line">
                    <td>{{ l.name }} <span class="muted">{{ l.quantity | number: '1.0-3' }} {{ unitLabel(l.unit) }} × {{ l.unitPrice | currency: q.currency }}</span></td>
                    <td class="num">{{ l.lineTotal | currency: q.currency }}</td>
                  </tr>
                }
              </tbody>
            </table>
          }
          <dl class="totals" data-testid="guided-totals">
            <dt>Subtotal</dt><dd>{{ q.subtotal | currency: q.currency }}</dd>
            <dt>Tax ({{ q.taxRate * 100 | number: '1.0-3' }}%)</dt><dd>{{ q.tax | currency: q.currency }}</dd>
            <dt class="total">Total</dt><dd class="total" data-testid="guided-total">{{ q.total | currency: q.currency }}</dd>
          </dl>
          <p class="muted">Priced by your store for ZIP {{ zip() }}. Final numbers are confirmed when we measure.</p>
          @if (open().length) {
            <p class="warning" data-testid="missing"><span>Still to choose: {{ open().join(', ') }}.</span></p>
          }
          @if (!projectId()) {
            <div class="actions">
              <button type="button" class="btn btn-primary" (click)="save()" [disabled]="!q.complete || saving()" data-testid="save-guided">
                {{ saving() ? 'Saving…' : 'Save quote' }}
              </button>
            </div>
          }
          @if (saved.error()) {
            <div class="warning" role="alert"><span>{{ describe(saved.error()) }}</span></div>
          }
        } @else if (pricingLoading()) {
          <p class="muted">Pricing…</p>
        } @else if (pricingError() && !measureProblems().length) {
          <div class="warning" role="alert" data-testid="preview-problem"><span>{{ previewProblem() }}</span></div>
        }
      </section>
      }
    }
  `,
  styles: `
    :host { display: grid; gap: 1rem; }
    .step { display: grid; gap: .75rem; }
    fieldset { border: 1px solid var(--bp-border); border-radius: var(--bp-radius-md); padding: .75rem 1rem; display: grid; gap: .6rem; }
    legend { font-weight: 600; padding: 0 .35rem; }
    .row { display: flex; flex-wrap: wrap; align-items: center; gap: .6rem 1rem; }
    .chips { display: flex; flex-wrap: wrap; gap: .5rem; }
    .layouts { display: grid; grid-template-columns: repeat(auto-fill, minmax(5.5rem, 1fr)); gap: .4rem; }
    .section { gap: .4rem .8rem; }
    fieldset { padding: .6rem .8rem; gap: .45rem; }
    .layout { display: grid; justify-items: center; gap: .25rem; padding: .4rem; border: 1px solid var(--bp-border); border-radius: var(--bp-radius-md); cursor: pointer; font-size: .85rem; text-align: center; }
    .layout:hover { border-color: var(--bp-accent); }
    .layout.active { border-color: var(--bp-accent); box-shadow: 0 0 0 2px var(--bp-accent-soft); font-weight: 600; }
    .layout:has(input:focus-visible) { outline: 2px solid var(--bp-accent); outline-offset: 2px; }
    .layout svg { width: 100%; max-width: 5.5rem; height: auto; }
    .layout .room { fill: var(--bp-surface); stroke: var(--bp-border-strong); stroke-width: .8; }
    .layout .run { fill: var(--bp-ink-muted); }
    .layout .isl { fill: var(--bp-accent); }
    .small { font-size: .85rem; margin: 0; }
    input.num { width: 4.5rem; min-height: var(--bp-tap); padding: 0 .5rem; border-radius: var(--bp-radius-md); border: 1px solid var(--bp-border-strong); background: var(--bp-surface); color: var(--bp-ink); font: inherit; }
    input.num.wide { width: 7rem; }
    select { min-height: var(--bp-tap); border-radius: var(--bp-radius-md); border: 1px solid var(--bp-border-strong); background: var(--bp-surface); color: var(--bp-ink); font: inherit; padding: 0 .5rem; }
    .measured { font-weight: 600; margin: 0; }
    .choices { display: grid; grid-template-columns: repeat(auto-fill, minmax(12rem, 1fr)); gap: .6rem; }
    .choice { display: grid; gap: .25rem; align-content: start; min-height: var(--bp-tap); padding: .6rem .75rem; border: 1px solid var(--bp-border); border-radius: var(--bp-radius-md); cursor: pointer; }
    .choice.active { border-color: var(--bp-accent); box-shadow: 0 0 0 2px var(--bp-accent-soft); }
    .choice img { width: 100%; height: 4.5rem; object-fit: contain; }
    .choice.qty { cursor: default; }
    .stepper { display: flex; align-items: center; gap: .5rem; }
    .stepper output { min-width: 2ch; text-align: center; font-weight: 700; }
    .group-title { margin: .5rem 0 0; font-size: .95rem; color: var(--bp-ink-muted); }
    .lines { width: 100%; border-collapse: collapse; }
    .lines td { padding: .35rem 0; border-bottom: 1px solid var(--bp-border); }
    .num { text-align: right; }
    .totals { display: grid; grid-template-columns: 1fr auto; gap: .25rem 1rem; margin: .5rem 0 0; }
    .totals dd { margin: 0; text-align: right; }
    .totals .total { font-weight: 700; font-size: 1.1rem; }
    .actions { display: flex; gap: .6rem; }
  `,
})
export class GuidedStepsComponent {
  readonly zip = input.required<string>();
  readonly sku = input.required<string>();
  /** 2n-f: in a project, saved answers are restored and every change is saved to it (PUT answers). */
  readonly projectId = input<string | null>(null);
  readonly project = input<Project | null>(null);
  /** In a project: show the live preview card too (the workspace usually shows its own quote card). */
  readonly showPreview = input(false);
  /** Which screen to show: everything (Start a project before 2n-f), or one step per screen (workspace). */
  readonly page = input<string>('all');
  /** The screens this product needs, for the workspace's tabs: Measure, then one per option step. */
  readonly screens = output<{ id: string; label: string }[]>();
  readonly projectChange = output<Project>();

  private readonly router = inject(Router);
  protected readonly layouts = LAYOUTS;
  protected readonly titles = GROUP_TITLES;
  protected readonly pitches = Array.from({ length: 13 }, (_, i) => i);
  protected readonly parts = [
    { key: 'countertop' as const, label: 'Countertop' },
    { key: 'island' as const, label: 'Island / peninsula' },
    { key: 'backsplash' as const, label: 'Backsplash' },
  ];
  protected readonly stages = [
    { step: 'options' as const, title: 'Options' },
    { step: 'protection' as const, title: 'Protection plan' },
  ];

  protected readonly options = httpResource<ProductOptions>(
    () => `/api/catalog/products/${encodeURIComponent(this.sku())}/options?zip=${encodeURIComponent(this.zip())}`,
  );
  protected readonly opts = computed(() => (this.options.hasValue() ? this.options.value()! : null));

  protected readonly form = signal<MeasureForm>(defaultMeasureForm());
  protected readonly answers = signal<Answers>({});
  /** In a project, nothing is sent until the saved answers are back in the form (else defaults would overwrite them). */
  private readonly restored = signal(false);
  /** The request as restored from the project: not saved again unless something actually changes. */
  private restoredBody: string | null = null;

  /** The preview request: re-sent whenever a measurement or answer changes. */
  private readonly body = computed(() => {
    const o = this.opts();
    if (!o) return null;
    return {
      zip: this.zip(),
      sku: this.sku(),
      measurements: measurementsBody(o.measure, this.form()),
      choices: toChoices(o.groups, this.answers()),
    };
  });
  protected readonly preview = httpResource<GuidedQuote>(() => {
    const body = this.body();
    return body && !this.projectId() ? { url: '/api/quotes/guided/preview', method: 'POST', body } : undefined;
  });

  /** Project mode: save the answers; the server answers with the project, re-priced. */
  protected readonly projectSave = httpResource<Project>(() => {
    const id = this.projectId();
    const body = this.body();
    if (!id || !body || !this.restored()) return undefined;
    if (JSON.stringify(body) === this.restoredBody) return undefined;
    return { url: `/api/projects/${encodeURIComponent(id)}/answers`, method: 'PUT', body: { sku: body.sku, measurements: body.measurements, choices: body.choices } };
  });

  /** The priced quote to show, from whichever mode. */
  protected readonly pq = computed<GuidedQuote | null>(() => {
    if (this.projectId()) return this.projectSave.hasValue() ? this.projectSave.value()!.preview : (this.project()?.preview ?? null);
    return this.preview.hasValue() ? this.preview.value()! : null;
  });
  protected readonly pricingLoading = computed(() => (this.projectId() ? this.projectSave.isLoading() : this.preview.isLoading()));
  protected readonly pricingError = computed(() => (this.projectId() ? this.projectSave.error() : this.preview.error()));

  private readonly saveRequest = signal<ReturnType<typeof this.body> | null>(null);
  protected readonly saved = httpResource<{ id: string }>(() => {
    const body = this.saveRequest();
    return body ? { url: '/api/quotes/guided', method: 'POST', body } : undefined;
  });
  protected readonly saving = computed(() => this.saved.isLoading());

  protected readonly open = computed(() => (this.pq() && this.opts() ? missingLabels(this.opts()!.groups, this.pq()!.missing) : []));
  protected readonly measureProblems = computed(() => {
    if (this.projectId()) {
      const p = this.projectSave.hasValue() ? this.projectSave.value()! : this.project();
      return p?.previewErrors.map((e) => e.message) ?? [];
    }
    const err = this.preview.error() as { error?: { code?: string } } | undefined;
    return err?.error?.code === 'invalid_measurements' ? problemMessages(err) : [];
  });

  constructor() {
    // Project mode: put the saved answers into the form when the steps are known, and again whenever
    // the project is re-read from the server (e.g. the concierge changed it), so we never overwrite it.
    effect(() => {
      const o = this.opts();
      const p = this.project();
      if (!o) return;
      untracked(() => {
        if (this.projectId() && p) {
          this.form.set(formFromAnswers(o.measure, p.answers));
          this.answers.set(answersFromChoices(o.groups, p.answers.choices));
          // A new project has no measurements yet: save the starting ones so it's priced right away.
          this.restoredBody = p.answers.measurements ? JSON.stringify(this.body()) : null;
        }
        this.restored.set(true);
      });
    });
    effect(() => {
      if (this.projectSave.hasValue()) this.projectChange.emit(this.projectSave.value()!);
    });
    effect(() => {
      const o = this.opts();
      if (o) this.screens.emit([{ id: 'measure', label: 'Measure' }, ...o.groups.map((g) => ({ id: 'group:' + g.id, label: g.label }))]);
    });
    // Saved: go to My quotes, where it's listed with its ZIP.
    effect(() => {
      if (this.saved.hasValue()) void this.router.navigate(['/quotes'], { queryParams: { saved: this.saved.value()!.id } });
    });
  }

  // ---- measurements ----
  protected setLayout(layout: string): void {
    this.form.update((f) => ({ ...f, countertop: { ...f.countertop, layout } }));
  }
  protected addSection(part: 'countertop' | 'island' | 'backsplash'): void {
    const fresh: SectionForm = part === 'backsplash' ? { width: { ft: 10, in: 0 }, depthIn: 4 } : part === 'island' ? { width: { ft: 6, in: 0 }, depthIn: 36 } : { width: { ft: 8, in: 0 }, depthIn: 25 };
    this.form.update((f) => ({ ...f, countertop: { ...f.countertop, [part]: [...f.countertop[part], fresh] } }));
  }
  protected removeSection(part: 'countertop' | 'island' | 'backsplash', i: number): void {
    this.form.update((f) => ({ ...f, countertop: { ...f.countertop, [part]: f.countertop[part].filter((_, j) => j !== i) } }));
  }
  protected setSection(part: 'countertop' | 'island' | 'backsplash', i: number, field: 'ft' | 'in' | 'depth', e: Event): void {
    const v = Number((e.target as HTMLInputElement).value) || 0;
    this.form.update((f) => ({
      ...f,
      countertop: {
        ...f.countertop,
        [part]: f.countertop[part].map((s, j): SectionForm => {
          if (j !== i) return s;
          if (field === 'depth') return { ...s, depthIn: v };
          const width: Length = { ...s.width, [field]: v };
          return { ...s, width };
        }),
      },
    }));
  }
  protected setRoof(field: keyof MeasureForm['roof'], e: Event): void {
    const v = Number((e.target as HTMLInputElement | HTMLSelectElement).value) || 0;
    this.form.update((f) => ({ ...f, roof: { ...f.roof, [field]: v } }));
  }
  protected setRoom(i: number, field: 'lengthFt' | 'widthFt', e: Event): void {
    const v = Number((e.target as HTMLInputElement).value) || 0;
    this.form.update((f) => ({ ...f, rooms: f.rooms.map((r, j) => (j === i ? { ...r, [field]: v } : r)) }));
  }
  protected addRoom(): void {
    this.form.update((f) => ({ ...f, rooms: [...f.rooms, { lengthFt: 10, widthFt: 10 }] }));
  }
  protected removeRoom(i: number): void {
    this.form.update((f) => ({ ...f, rooms: f.rooms.filter((_, j) => j !== i) }));
  }
  protected setCount(e: Event): void {
    const v = Number((e.target as HTMLInputElement).value) || 0;
    this.form.update((f) => ({ ...f, count: v }));
  }

  // ---- answers ----
  protected groupsFor(step: OptionGroup['step']): OptionGroup[] {
    return this.opts()?.groups.filter((g) => g.step === step) ?? [];
  }
  /** On a one-step screen (group:<id>) only that step; on 'all' every step. */
  protected shownGroups(step: OptionGroup['step']): OptionGroup[] {
    const page = this.page();
    return this.groupsFor(step).filter((g) => page === 'all' || page === 'group:' + g.id);
  }
  protected singleSku(group: string): string | null | undefined {
    const a = this.answers()[group];
    return a?.kind === 'single' ? a.sku : undefined;
  }
  protected isNone(group: string): boolean {
    const a = this.answers()[group];
    return a?.kind === 'single' ? a.sku === null : a?.kind === 'quantity' ? a.none : false;
  }
  protected countOf(group: string, sku: string): number {
    const a = this.answers()[group];
    return a?.kind === 'quantity' ? (a.counts[sku] ?? 0) : 0;
  }
  protected pickSingle(group: string, sku: string | null): void {
    this.answers.update((a) => ({ ...a, [group]: { kind: 'single', sku } }));
  }
  protected bump(group: string, sku: string, delta: number): void {
    this.answers.update((all) => {
      const a = all[group];
      const counts = a?.kind === 'quantity' ? { ...a.counts } : {};
      counts[sku] = Math.min(100, Math.max(0, (counts[sku] ?? 0) + delta));
      const next: Answer = { kind: 'quantity', counts, none: false };
      return { ...all, [group]: next };
    });
  }
  protected setNone(group: string, none: boolean): void {
    this.answers.update((all) => ({ ...all, [group]: { kind: 'quantity', counts: {}, none } }));
  }

  // ---- quote ----
  protected lineGroups(lines: GuidedLine[]): { group: GuidedLine['group']; lines: GuidedLine[] }[] {
    const order: GuidedLine['group'][] = ['product', 'minimum', 'options', 'protection'];
    return order.map((group) => ({ group, lines: lines.filter((l) => l.group === group) })).filter((g) => g.lines.length);
  }
  protected save(): void {
    if (this.pq()?.complete) this.saveRequest.set(this.body());
  }
  protected previewProblem(): string {
    const messages = problemMessages(this.pricingError());
    return messages.length ? messages.join(' ') : describeError(this.pricingError() as Parameters<typeof describeError>[0]);
  }
  protected unitLabel(unit: string): string {
    return unit === 'sqft' ? 'sq ft' : unit;
  }
  protected describe(err: unknown): string {
    return describeError(err as Parameters<typeof describeError>[0]);
  }
}
