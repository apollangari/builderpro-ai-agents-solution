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
