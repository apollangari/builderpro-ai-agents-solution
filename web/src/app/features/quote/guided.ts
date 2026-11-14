import { Product } from '../catalog/catalog.api';

/**
 * Guided quote (2n-b..e): types mirroring the API and pure helpers (no Angular, no I/O).
 * The browser only collects answers and converts feet + inches to inches; every quantity and
 * price comes back from POST /api/quotes/guided/preview.
 */

// ---- GET /api/catalog/products/{sku}/options?zip= ----
export interface OptionItem {
  sku: string;
  name: string;
  unit: string;
  price: number;
  currency: string;
  imageUrl: string | null;
  included: number;
}
export interface OptionGroup {
  id: string;
  step: 'options' | 'protection';
  label: string;
  help: string | null;
  selection: 'single' | 'quantity';
  basis: string;
  required: boolean;
  noneLabel: string | null;
  options: OptionItem[];
}
export type MeasureKind = 'countertop' | 'roof' | 'area' | 'count';
export interface ProductOptions {
  product: Product;
  measure: MeasureKind;
  minimumQuantity: number | null;
  groups: OptionGroup[];
}

// ---- POST /api/quotes/guided/preview ----
export interface GuidedLine {
  sku: string;
  name: string;
  unit: string;
  quantity: number;
  unitPrice: number;
  lineTotal: number;
  group: 'product' | 'minimum' | 'options' | 'protection';
}
export interface GuidedQuote {
  measurement: { kind: MeasureKind; values: Record<string, number>; productBasis: string };
  lines: GuidedLine[];
  currency: string;
  subtotal: number;
  taxRate: number;
  tax: number;
  total: number;
  missing: string[];
  complete: boolean;
}

// ---- What the customer entered ----
export interface Length {
  ft: number;
  in: number;
}
export interface SectionForm {
  width: Length;
  depthIn: number;
}
export interface CountertopForm {
  layout: string;
  countertop: SectionForm[];
  island: SectionForm[];
  backsplash: SectionForm[]; // depthIn = height
}
export interface RoofForm {
  footprintSqFt: number;
  pitchRise: number;
  wastePercent: number;
  eavesAndRakesFt: number;
}
export interface RoomForm {
  lengthFt: number;
  widthFt: number;
}
export interface MeasureForm {
  countertop: CountertopForm;
  roof: RoofForm;
  rooms: RoomForm[];
  count: number;
}

export function defaultMeasureForm(): MeasureForm {
  return {
    countertop: { layout: 'straight', countertop: [{ width: { ft: 10, in: 0 }, depthIn: 25 }], island: [], backsplash: [] },
    roof: { footprintSqFt: 2000, pitchRise: 6, wastePercent: 10, eavesAndRakesFt: 0 },
    rooms: [{ lengthFt: 12, widthFt: 10 }],
    count: 1,
  };
}

/** 10 ft 6 in -> 126. Input conversion only; areas are computed by the server. */
export function inches(l: Length): number {
  return (Number(l.ft) || 0) * 12 + (Number(l.in) || 0);
}

/** The measurements part of the request for this product's measure kind. */
export function measurementsBody(kind: MeasureKind, f: MeasureForm): Record<string, unknown> {
  switch (kind) {
    case 'countertop': {
      const sections = (s: SectionForm[]) => s.map((x) => ({ widthIn: inches(x.width), depthIn: Number(x.depthIn) || 0 }));
      return {
        countertop: {
          layout: f.countertop.layout,
          countertop: sections(f.countertop.countertop),
          island: sections(f.countertop.island),
          backsplash: sections(f.countertop.backsplash),
        },
      };
    }
    case 'roof':
      return { roof: { ...f.roof } };
    case 'area':
      return { rooms: f.rooms.map((r) => ({ lengthFt: Number(r.lengthFt) || 0, widthFt: Number(r.widthFt) || 0 })) };
    case 'count':
      return { count: Number(f.count) || 0 };
  }
}

// ---- Answers ----
export type Answer = { kind: 'single'; sku: string | null } | { kind: 'quantity'; counts: Record<string, number>; none: boolean };
export type Answers = Readonly<Record<string, Answer>>;

export interface Choice {
  group: string;
  sku: string | null;
  quantity?: number;
}

/**
 * Answers -> the API's choices. A quantity group sends its non-zero counts; "none" (or, for an
 * optional group, nothing chosen) sends nothing or an explicit none so required steps count as answered.
 */
export function toChoices(groups: readonly OptionGroup[], answers: Answers): Choice[] {
  const choices: Choice[] = [];
  for (const g of groups) {
    const a = answers[g.id];
    if (!a) continue;
    if (a.kind === 'single') {
      choices.push({ group: g.id, sku: a.sku });
    } else if (a.none) {
      choices.push({ group: g.id, sku: null });
    } else {
      for (const [sku, quantity] of Object.entries(a.counts)) {
        if (quantity > 0) choices.push({ group: g.id, sku, quantity });
      }
    }
  }
  return choices;
}

/** Labels of the required steps the server says are still open, in the store's order. */
export function missingLabels(groups: readonly OptionGroup[], missing: readonly string[]): string[] {
  return groups.filter((g) => missing.includes(g.id)).map((g) => g.label);
}

export const GROUP_TITLES: Record<GuidedLine['group'], string> = {
  product: 'Product',
  minimum: 'Minimum charge',
  options: 'Options',
  protection: 'Protection plan',
};

/** The problem's per-field messages (invalid_measurements, invalid_guided_quote), if any. */
export function problemMessages(err: unknown): string[] {
  const body = (err as { error?: { errors?: { message: string }[] } } | undefined)?.error;
  return body?.errors?.map((e) => e.message) ?? [];
}

// ---- 2n-f: projects (GET/PUT /api/projects/...) ----
export type ProjectStage = 'discover' | 'configure' | 'quote' | 'deposit' | 'measure' | 'confirm' | 'install' | 'done';

export interface IssuedQuote {
  id: string;
  number: number;
  status: 'issued' | 'accepted';
  validUntil: string;
  acceptedAt: string | null;
  current: boolean;
  expired: boolean;
  currency: string;
  lines: GuidedLine[];
  subtotal: number;
  taxRate: number;
  tax: number;
  total: number;
  /** 6a: a paid deposit holds this quote's price. */
  held?: boolean;
}

/** The measure visit (6b). Mirrors VisitDto in C#. */
export interface VisitInfo {
  id: string;
  start: string;
  status: 'booked' | 'measured' | 'cancelled';
  measuredAt: string | null;
}

/** Installation progress (6c). Mirrors InstallDto in C#. */
export type InstallStep = 'scheduled' | 'fabricating' | 'installing' | 'done';
export interface InstallInfo {
  step: InstallStep | null;
  scheduledFor: string | null;
  events: { step: InstallStep; at: string; scheduledFor: string | null; note: string | null }[];
}

/** The 5 % deposit (6a). Mirrors DepositDto in C#. */
export interface DepositInfo {
  id: string;
  projectId: string;
  quoteNumber: number;
  quoteTotal: number;
  amount: number;
  currency: string;
  percent: number;
  status: 'pending' | 'paid';
  paidAt: string | null;
  checkoutUrl: string;
}

export interface ProjectAnswers {
  measurements?: Record<string, unknown> | null;
  choices?: Choice[];
}

export interface Project {
  id: string;
  title: string;
  zip: string;
  stage: ProjectStage;
  sku: string | null;
  answers: ProjectAnswers;
  preview: GuidedQuote | null;
  previewErrors: { code: string; message: string }[];
  quote: IssuedQuote | null;
  updatedAt: string;
  deposit?: DepositInfo | null;
  /** 1i: the salesperson who prepared it for the customer. */
  preparedBy?: string | null;
  /** 6b: the measure visit. */
  visit?: VisitInfo | null;
  /** 6c: installation steps. */
  install?: InstallInfo | null;
}

export interface ProjectSummary {
  id: string;
  title: string;
  zip: string;
  stage: ProjectStage;
  quoteNumber: number | null;
  quoteTotal: number | null;
  currency: string | null;
  updatedAt: string;
  /** 1i: the salesperson who prepared it. */
  preparedBy?: string | null;
  /** 1i: whose project (staff lists only). */
  customerId?: string | null;
}

/** The workspace's stage rail, in order. */
export const STAGES: { id: ProjectStage; label: string }[] = [
  { id: 'discover', label: 'Discover' },
  { id: 'configure', label: 'Configure' },
  { id: 'quote', label: 'Quote' },
  { id: 'deposit', label: 'Deposit' },
  { id: 'measure', label: 'Measure visit' },
  { id: 'install', label: 'Install' },
];

/** Saved measurements (inches, as the API takes them) back into the form (feet + inches). */
export function formFromAnswers(kind: MeasureKind, answers: ProjectAnswers | null | undefined): MeasureForm {
  const f = defaultMeasureForm();
  const m = (answers?.measurements ?? null) as Record<string, any> | null;
  if (!m) return f;
  const toSection = (s: { widthIn: number; depthIn: number }): SectionForm => ({
    width: { ft: Math.floor(s.widthIn / 12), in: Math.round((s.widthIn % 12) * 100) / 100 },
    depthIn: s.depthIn,
  });
  if (kind === 'countertop' && m['countertop']) {
    const c = m['countertop'];
    f.countertop = {
      layout: c.layout ?? 'straight',
      countertop: (c.countertop ?? []).map(toSection),
      island: (c.island ?? []).map(toSection),
      backsplash: (c.backsplash ?? []).map(toSection),
    };
  }
  if (kind === 'roof' && m['roof']) f.roof = { ...f.roof, ...m['roof'] };
  if (kind === 'area' && Array.isArray(m['rooms'])) f.rooms = m['rooms'];
  if (kind === 'count' && typeof m['count'] === 'number') f.count = m['count'];
  return f;
}

/** Saved choices back into per-step answers (the inverse of toChoices). */
export function answersFromChoices(groups: readonly OptionGroup[], choices: readonly Choice[] | null | undefined): Answers {
  const result: Record<string, Answer> = {};
  for (const g of groups) {
    const mine = (choices ?? []).filter((c) => c.group === g.id);
    if (!mine.length) continue;
    if (g.selection === 'single') {
      result[g.id] = { kind: 'single', sku: mine[0].sku };
    } else if (mine.some((c) => c.sku === null)) {
      result[g.id] = { kind: 'quantity', counts: {}, none: true };
    } else {
      result[g.id] = { kind: 'quantity', counts: Object.fromEntries(mine.map((c) => [c.sku!, c.quantity ?? 0])), none: false };
    }
  }
  return result;
}

/** Rectangles (inches) for the layout sketch: sections laid out as the layout says, island apart. */
export interface SketchRect {
  x: number;
  y: number;
  w: number;
  h: number;
  kind: 'top' | 'island';
}
export function layoutSketch(answers: ProjectAnswers | null | undefined): { rects: SketchRect[]; width: number; height: number } {
  const c = ((answers?.measurements ?? null) as Record<string, any> | null)?.['countertop'];
  if (!c) return { rects: [], width: 0, height: 0 };
  const tops: { widthIn: number; depthIn: number }[] = c.countertop ?? [];
  const islands: { widthIn: number; depthIn: number }[] = c.island ?? [];
  const rects: SketchRect[] = [];
  const first = tops[0];
  if (first) rects.push({ x: 0, y: 0, w: first.widthIn, h: first.depthIn, kind: 'top' });
  // Follow the chosen layout: straight runs continue the first; galley puts the second run opposite;
  // L turns down the left wall; U adds the right wall; peninsula sticks out from the left run's end.
  const layout: string = c.layout ?? 'l';
  let x = first?.widthIn ?? 0;
  tops.slice(1).forEach((s, i) => {
    if (layout === 'straight') { rects.push({ x, y: 0, w: s.widthIn, h: s.depthIn, kind: 'top' }); x += s.widthIn; }
    else if (layout === 'galley') rects.push({ x: 0, y: first.depthIn + 42, w: s.widthIn, h: s.depthIn, kind: 'top' });
    else if (i === 0) rects.push({ x: 0, y: first.depthIn, w: s.depthIn, h: s.widthIn, kind: 'top' });
    else if (i === 1 && layout === 'peninsula') rects.push({ x: tops[1].depthIn, y: first.depthIn + tops[1].widthIn - s.depthIn, w: s.widthIn, h: s.depthIn, kind: 'top' });
    else if (i === 1) rects.push({ x: first.widthIn - s.depthIn, y: first.depthIn, w: s.depthIn, h: s.widthIn, kind: 'top' });
    else rects.push({ x: first.widthIn + (i - 2) * 6, y: 0, w: s.widthIn, h: s.depthIn, kind: 'top' });
  });
  const right = Math.max(0, ...rects.map((r) => r.x + r.w));
  const bottom = Math.max(0, ...rects.map((r) => r.y + r.h));
  let ix = Math.max(36, (right - (islands[0]?.widthIn ?? 0)) / 2 + 18);
  for (const s of islands) {
    rects.push({ x: ix, y: (first?.depthIn ?? 0) + 42, w: s.widthIn, h: s.depthIn, kind: 'island' });
    ix += s.widthIn + 12;
  }
  return {
    rects,
    width: Math.max(right, ...rects.map((r) => r.x + r.w)),
    height: Math.max(bottom, ...rects.map((r) => r.y + r.h)),
  };
}
