import { Injectable, computed, signal } from '@angular/core';
import { Product } from '../catalog/catalog.api';

export interface DraftLine {
  sku: string;
  quantity: number;
}

const KEY = 'bp.quoteDraft';
const MAX_LINES = 100; // same limit as QuotePricer

/**
 * The quote being put together (2m): SKUs and quantities ONLY. Prices and totals always come from
 * the server (POST /api/quotes/preview), so the draft can never hold a stale or invented price.
 * Kept per tab, so a reload or a trip to the catalog doesn't lose it.
 */
@Injectable({ providedIn: 'root' })
export class QuoteDraft {
  private readonly state = signal<DraftLine[]>(QuoteDraft.read());

  readonly lines = this.state.asReadonly();
  readonly count = computed(() => this.state().length);

  quantityOf(sku: string): number {
    return this.state().find((l) => l.sku === sku)?.quantity ?? 0;
  }

  /** Adds to the existing quantity of that SKU (one line per SKU, like the server requires). */
  add(sku: string, quantity = 1): void {
    if (!(quantity > 0)) return;
    const lines = this.state();
    const existing = lines.find((l) => l.sku === sku);
    if (existing) this.write(lines.map((l) => (l.sku === sku ? { ...l, quantity: round3(l.quantity + quantity) } : l)));
    else if (lines.length < MAX_LINES) this.write([...lines, { sku, quantity: round3(quantity) }]);
  }

  /** Sets a quantity; 0 or less removes the line. */
  set(sku: string, quantity: number): void {
    if (!(quantity > 0)) { this.remove(sku); return; }
    this.write(this.state().map((l) => (l.sku === sku ? { ...l, quantity: round3(quantity) } : l)));
  }

  remove(sku: string): void {
    this.write(this.state().filter((l) => l.sku !== sku));
  }

  clear(): void {
    this.write([]);
  }

  private write(lines: DraftLine[]): void {
    this.state.set(lines);
    try {
      sessionStorage.setItem(KEY, JSON.stringify(lines));
    } catch {
      /* storage blocked: the draft lasts until reload */
    }
  }

  private static read(): DraftLine[] {
    try {
      const v = JSON.parse(sessionStorage.getItem(KEY) ?? '[]') as unknown;
      return Array.isArray(v)
        ? v.filter((l): l is DraftLine => typeof l?.sku === 'string' && typeof l?.quantity === 'number' && l.quantity > 0).slice(0, MAX_LINES)
        : [];
    } catch {
      return [];
    }
  }
}

/** Quantities have at most 3 decimals (QuotePricer rule); avoid 0.1 + 0.2 float noise. */
function round3(n: number): number {
  return Math.round(n * 1000) / 1000;
}

/** POST /api/quotes/estimators/roof answer. Mirrors RoofQuantities in C#. */
export interface RoofQuantities {
  pitchFactor: number;
  roofAreaSqFt: number;
  squares: number;
  shingleBundles: number;
  underlaymentRolls: number;
  dripEdgePieces: number;
}

/**
 * Turns a roof take-off into quote lines using the store's OWN products, found by attributes
 * (not hard-coded SKUs): the chosen shingle, an underlayment, a flashing/drip edge, roofing labour.
 * Anything the store doesn't sell is simply left out (and reported as missing).
 */
export function roofLines(q: RoofQuantities, products: readonly Product[], shingleSku: string): { lines: DraftLine[]; missing: string[] } {
  const roofing = products.filter((p) => p.category === 'roofing');
  const pick = (pred: (p: Product) => boolean) => roofing.find(pred);
  const shingle = roofing.find((p) => p.sku === shingleSku && p.attributes['type'] === 'shingle');
  const underlay = pick((p) => p.attributes['type'] === 'underlayment');
  const drip = pick((p) => p.attributes['type'] === 'flashing');
  const labour = products.find((p) => p.category === 'labour' && p.attributes['trade'] === 'roofing' && p.unit === 'square');

  const lines: DraftLine[] = [];
  const missing: string[] = [];
  const put = (p: Product | undefined, qty: number, what: string) => {
    if (qty <= 0) return;
    if (p) lines.push({ sku: p.sku, quantity: qty });
    else missing.push(what);
  };
  put(shingle, q.shingleBundles, 'shingles');
  put(underlay, q.underlaymentRolls, 'underlayment');
  put(drip, q.dripEdgePieces, 'drip edge');
  put(labour, q.squares, 'roofing labour');
  return { lines, missing };
}
