import { Category, Product } from '../catalog/catalog.api';

/** The default scenes (2l). Every category except labour/other maps to one (Category.scene, from the API). */
export type SceneId = 'kitchen' | 'house-roof' | 'backyard' | 'room-floor' | 'room-wall' | 'house-front';
export const SCENES: readonly SceneId[] = ['kitchen', 'house-roof', 'backyard', 'room-floor', 'room-wall', 'house-front'];

export function sceneFor(category: Category | undefined): SceneId | null {
  return category && (SCENES as readonly string[]).includes(category.scene) ? (category.scene as SceneId) : null;
}

/**
 * Which surface of the scene the product goes on, and how:
 *   texture: the product image tiles a surface (countertop, floor, roof, deck)
 *   object:  the product image is placed as a thing (shed, cabinet front, window, door, fence)
 *   colour:  the surface is painted (paint)
 */
export type Placement = { surface: string; mode: 'texture' | 'object' | 'colour' };

export function placementFor(category: string): Placement | null {
  switch (category) {
    case 'countertops': return { surface: 'countertop', mode: 'texture' };
    case 'cabinets': return { surface: 'cabinets', mode: 'object' };
    case 'roofing': return { surface: 'roof', mode: 'texture' };
    case 'sheds': return { surface: 'shed', mode: 'object' };
    case 'decking': return { surface: 'deck', mode: 'texture' };
    case 'fencing': return { surface: 'fence', mode: 'object' };
    case 'flooring': return { surface: 'floor', mode: 'texture' };
    case 'paint': return { surface: 'wall', mode: 'colour' };
    case 'windows-doors': return { surface: 'opening', mode: 'object' };
    default: return null;
  }
}

// Colour names used by the catalog -> a paint swatch. Unknown names fall back to a warm neutral.
const SWATCHES: [RegExp, string][] = [
  [/sage/i, '#9caf88'],
  [/navy/i, '#1f2f57'],
  [/bright white|white/i, '#f4f1ea'],
  [/grey|gray/i, '#9a9da1'],
  [/beige|sand/i, '#d8c7a6'],
  [/black/i, '#2b2d31'],
  [/blue/i, '#5b7db1'],
  [/green/i, '#7d9a6f'],
];

export function swatchFor(product: Pick<Product, 'attributes' | 'name'>): string {
  const text = `${product.attributes['color'] ?? ''} ${product.name}`;
  return SWATCHES.find(([re]) => re.test(text))?.[1] ?? '#d9cdb8';
}
