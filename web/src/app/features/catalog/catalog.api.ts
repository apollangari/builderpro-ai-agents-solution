import { HttpResourceRef, httpResource } from '@angular/common/http';

/** GET /api/catalog/products (Catalog service). Mirrors ProductDto in C#. */
export interface Product {
  sku: string;
  name: string;
  description: string | null;
  unit: string; // how listPrice is measured: "each", "m2", "hour", ...
  listPrice: number; // money from the server, displayed only: the UI never computes a price
  currency: string; // ISO 4217
  category: string; // a category id from GET /api/catalog/categories (2h)
  imageUrl: string | null;
  attributes: Record<string, string>;
}

/**
 * Products of the caller's tenant. The tenant is NOT a parameter: the Catalog service asks
 * the Identity service which tenant this signed-in user acts for (LEARN 2a).
 * `defaultValue: []` means value() is always an array, even before the first answer.
 */
export function productsResource(): HttpResourceRef<Product[]> {
  return httpResource<Product[]>(() => '/api/catalog/products', { defaultValue: [] });
}

/** GET /api/catalog/categories. Mirrors CategoryDto in C#. */
export interface AttributeDefinition {
  key: string;
  label: string;
  kind: 'choice' | 'number' | 'text';
  required: boolean;
  options: string[] | null;
  unit: string | null;
}

export interface Category {
  id: string;
  name: string;
  defaultUnit: string;
  scene: string; // the visualizer scene (2l)
  attributes: AttributeDefinition[];
}

/** The platform's categories and their attribute definitions; the same for every retailer. */
export function categoriesResource(): HttpResourceRef<Category[]> {
  return httpResource<Category[]>(() => '/api/catalog/categories', { defaultValue: [] });
}
