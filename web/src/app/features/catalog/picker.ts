import { Category, Product } from './catalog.api';

/** Pure picker logic (2j): no Angular, no I/O, so every rule is a plain unit test. */

export interface CategoryTab {
  id: string | null; // null = all categories
  name: string;
  count: number;
}

export interface FilterGroup {
  key: string;
  label: string;
  values: { value: string; count: number }[];
}

export type Filters = Readonly<Record<string, string>>;

/** "All" first, then only the categories this store actually sells, in the platform's order. */
export function categoryTabs(products: readonly Product[], categories: readonly Category[]): CategoryTab[] {
  const counts = new Map<string, number>();
  for (const p of products) counts.set(p.category, (counts.get(p.category) ?? 0) + 1);
  return [
    { id: null, name: 'All', count: products.length },
    ...categories.filter((c) => counts.has(c.id)).map((c) => ({ id: c.id, name: c.name, count: counts.get(c.id)! })),
  ];
}

/**
 * Filters for one category: its CHOICE attributes, offering only values that occur in the store's
 * products (no dead ends), in the definition's option order. Attributes with one value are left out.
 */
export function filterGroups(products: readonly Product[], category: Category | undefined): FilterGroup[] {
  if (!category) return [];
  const inCategory = products.filter((p) => p.category === category.id);
  return category.attributes
    .filter((a) => a.kind === 'choice' && a.options)
    .map((a) => ({
      key: a.key,
      label: a.label,
      values: a.options!
        .map((value) => ({ value, count: inCategory.filter((p) => p.attributes[a.key] === value).length }))
        .filter((v) => v.count > 0),
    }))
    .filter((g) => g.values.length > 1);
}

/** Category, then every chosen attribute value, then free-text search on name or SKU. */
export function applyPicker(products: readonly Product[], category: string | null, filters: Filters, query: string): Product[] {
  const q = query.trim().toLowerCase();
  return products.filter(
    (p) =>
      (category === null || p.category === category) &&
      Object.entries(filters).every(([key, value]) => !value || p.attributes[key] === value) &&
      (!q || p.name.toLowerCase().includes(q) || p.sku.toLowerCase().includes(q)),
  );
}

/** Up to three short badges for a card: the category's choice/text attributes the product has. */
export function keyAttributes(product: Product, category: Category | undefined, max = 3): string[] {
  if (!category) return [];
  return category.attributes
    .filter((a) => a.kind !== 'number' && product.attributes[a.key])
    .slice(0, max)
    .map((a) => product.attributes[a.key]!.replace(/-/g, ' '));
}
