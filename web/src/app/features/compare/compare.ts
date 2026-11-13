import { Injectable, computed, signal } from '@angular/core';
import { Category, Product } from '../catalog/catalog.api';

export const MAX_COMPARE = 4;
const KEY = 'bp.compare';

/**
 * What the customer picked to compare (2k): 2-4 products of ONE category. Picking a product from
 * another category starts a new comparison (comparing a shed with a countertop means nothing).
 * Kept per tab so a reload or a trip to the compare page keeps it.
 */
@Injectable({ providedIn: 'root' })
export class CompareSelection {
  private readonly state = signal<{ category: string | null; skus: string[] }>(CompareSelection.read());

  readonly skus = computed(() => this.state().skus);
  readonly category = computed(() => this.state().category);
  readonly count = computed(() => this.state().skus.length);
  readonly full = computed(() => this.state().skus.length >= MAX_COMPARE);

  has(sku: string): boolean {
    return this.state().skus.includes(sku);
  }

  toggle(product: Pick<Product, 'sku' | 'category'>): void {
    const { category, skus } = this.state();
    if (skus.includes(product.sku)) {
      this.write({ category: skus.length === 1 ? null : category, skus: skus.filter((s) => s !== product.sku) });
    } else if (category !== product.category) {
      this.write({ category: product.category, skus: [product.sku] });
    } else if (skus.length < MAX_COMPARE) {
      this.write({ category, skus: [...skus, product.sku] });
    }
  }

  clear(): void {
    this.write({ category: null, skus: [] });
  }

  private write(next: { category: string | null; skus: string[] }): void {
    this.state.set(next);
    try {
      sessionStorage.setItem(KEY, JSON.stringify(next));
    } catch {
      /* storage blocked: the selection lasts until reload */
    }
  }

  private static read(): { category: string | null; skus: string[] } {
    try {
      const v = JSON.parse(sessionStorage.getItem(KEY) ?? 'null') as { category?: unknown; skus?: unknown } | null;
      if (v && typeof v.category === 'string' && Array.isArray(v.skus) && v.skus.every((s) => typeof s === 'string')) {
        return { category: v.category, skus: (v.skus as string[]).slice(0, MAX_COMPARE) };
      }
    } catch {
      /* ignore */
    }
    return { category: null, skus: [] };
  }
}

export interface CompareRow {
  label: string;
  values: string[];
}

/** One row per attribute of the category (definition order), "—" where a product doesn't say. */
export function compareRows(products: readonly Product[], category: Category | undefined): CompareRow[] {
  if (!category) return [];
  return category.attributes.map((a) => ({
    label: a.unit ? `${a.label} (${a.unit})` : a.label,
    values: products.map((p) => p.attributes[a.key]?.replace(/-/g, ' ') ?? '—'),
  }));
}

/**
 * What to ask POST /api/quotes/estimates for: each product at the category's typical size. Only
 * products sold in the category's default unit can be priced that way (72 "bundles" means nothing
 * for a product sold per roll); the server does all the arithmetic.
 */
export function estimateItems(products: readonly Product[], category: Category | undefined): { sku: string; quantity: number }[] {
  const typical = category?.typical;
  if (!typical) return [];
  return products.filter((p) => p.unit === category!.defaultUnit).map((p) => ({ sku: p.sku, quantity: typical.quantity }));
}
