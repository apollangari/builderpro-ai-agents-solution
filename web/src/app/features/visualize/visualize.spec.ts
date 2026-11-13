import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { Category, Product } from '../catalog/catalog.api';
import { SCENES, SceneId, placementFor, sceneFor, swatchFor } from './scene';
import { SceneComponent } from './scene.component';
import { VisualizePage } from './visualize.page';

const product = (sku: string, category: string, extra: Partial<Product> = {}): Product => ({
  sku, name: sku, description: null, unit: 'each', listPrice: 10, currency: 'USD', category,
  imageUrl: `/products/${sku.toLowerCase()}.svg`, attributes: {}, ...extra,
});

const cat = (id: string, scene: string): Category => ({ id, name: id, defaultUnit: 'each', scene, attributes: [], typical: null });

/** Every category the platform has, and the scene + surface it must end up on. */
const CASES: [string, SceneId, string][] = [
  ['countertops', 'kitchen', 'countertop'],
  ['cabinets', 'kitchen', 'cabinets'],
  ['roofing', 'house-roof', 'roof'],
  ['sheds', 'backyard', 'shed'],
  ['decking', 'backyard', 'deck'],
  ['fencing', 'backyard', 'fence'],
  ['flooring', 'room-floor', 'floor'],
  ['paint', 'room-wall', 'wall'],
  ['windows-doors', 'house-front', 'opening'],
];

describe('scene mapping', () => {
  it.each(CASES)('%s -> %s scene, %s surface', (category, scene, surface) => {
    expect(sceneFor(cat(category, scene))).toBe(scene);
    expect(placementFor(category)?.surface).toBe(surface);
  });

  it('labour and other have no scene', () => {
    expect(sceneFor(cat('labour', 'none'))).toBeNull();
    expect(placementFor('labour')).toBeNull();
  });

  it('paint swatches follow the colour name', () => {
    expect(swatchFor({ name: 'x', attributes: { color: 'Sage green' } })).toBe('#9caf88');
    expect(swatchFor({ name: 'x', attributes: { color: 'Mystery' } })).toBe('#d9cdb8');
  });

  it('every scene is covered by at least one category', () => {
    expect(new Set(CASES.map(([, s]) => s))).toEqual(new Set(SCENES));
  });
});

describe('SceneComponent', () => {
  it.each(CASES)('draws %s products on the %s scene (%s surface)', async (category, scene, surface) => {
    const fixture = TestBed.createComponent(SceneComponent);
    fixture.componentRef.setInput('scene', scene);
    fixture.componentRef.setInput('product', product('P', category, { attributes: { color: 'Sage green' } }));
    await fixture.whenStable();

    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('svg')?.getAttribute('data-scene')).toBe(scene);
    const target = el.querySelector(`[data-surface="${surface}"]`)!;
    expect(target).not.toBeNull();
    if (category === 'paint') {
      expect(target.getAttribute('fill')).toBe('#9caf88');                 // colour
    } else if (category === 'cabinets') {
      expect(target.getAttribute('fill')).toBe('#9caf88');                 // drawn fronts in the product colour
    } else if (['countertops', 'roofing', 'decking', 'flooring'].includes(category)) {
      expect(target.getAttribute('fill')).toMatch(/^url\(#bp-scene-pattern-\d+\)$/); // product texture
    } else {
      expect(target.getAttribute('href')).toBe('/products/p.svg');          // product image as an object
    }
  });
});

describe('VisualizePage', () => {
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ imports: [VisualizePage], providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  async function render(sku: string | undefined) {
    const fixture = TestBed.createComponent(VisualizePage);
    fixture.componentRef.setInput('sku', sku);
    TestBed.tick();
    http.expectOne('/api/catalog/products').flush([product('CT-Q', 'countertops'), product('CT-G', 'countertops'), product('SHED', 'sheds')]);
    http.expectOne('/api/catalog/categories').flush([cat('countertops', 'kitchen'), cat('sheds', 'backyard')]);
    await fixture.whenStable();
    return { fixture, el: fixture.nativeElement as HTMLElement };
  }

  it('shows the product from ?sku= in its default scene, with same-category options', async () => {
    const { el } = await render('CT-Q');

    expect(el.querySelector('[data-testid="scene"]')?.getAttribute('data-scene')).toBe('kitchen');
    expect([...el.querySelectorAll('[role="radio"]')].map((o) => o.getAttribute('data-testid'))).toEqual(['option-CT-Q', 'option-CT-G']);
    expect(el.querySelector('[data-testid="option-CT-Q"]')?.getAttribute('aria-checked')).toBe('true');
  });

  it('switching option re-renders the scene with the other product', async () => {
    const { fixture, el } = await render('CT-Q');

    el.querySelector<HTMLButtonElement>('[data-testid="option-CT-G"]')!.click();
    await fixture.whenStable();

    expect(el.querySelector('h1')?.textContent).toContain('CT-G');
    expect(el.querySelector('pattern image')?.getAttribute('href')).toBe('/products/ct-g.svg');
  });

  it('without a known product it points back to the catalog', async () => {
    const { el } = await render('NOPE');
    expect(el.querySelector('[data-testid="visualize-empty"]')).not.toBeNull();
  });
});

describe('PhotoOverlayComponent', () => {
  // jsdom has no canvas or image decoding: these tests cover the logic around the canvas.
  async function render() {
    const { PhotoOverlayComponent } = await import('./photo-overlay.component');
    const fixture = TestBed.createComponent(PhotoOverlayComponent);
    fixture.componentRef.setInput('product', product('CT-Q', 'countertops'));
    await fixture.whenStable();
    return { fixture, comp: fixture.componentInstance as unknown as { onFile(f: File | undefined): void }, el: fixture.nativeElement as HTMLElement };
  }

  it('says the photo stays on the device', async () => {
    const { el } = await render();
    expect(el.textContent).toContain("isn't uploaded anywhere");
  });

  it('refuses files that are not images, or larger than 15 MB, without reading them', async () => {
    const createUrl = vi.spyOn(URL, 'createObjectURL');
    const { fixture, comp, el } = await render();

    comp.onFile(new File(['x'], 'notes.pdf', { type: 'application/pdf' }));
    await fixture.whenStable();
    expect(el.querySelector('[data-testid="photo-error"]')?.textContent).toContain('not an image');

    const big = new File(['x'], 'huge.jpg', { type: 'image/jpeg' });
    Object.defineProperty(big, 'size', { value: 16 * 1024 * 1024 });
    comp.onFile(big);
    await fixture.whenStable();
    expect(el.querySelector('[data-testid="photo-error"]')?.textContent).toContain('15 MB');

    expect(createUrl).not.toHaveBeenCalled();
    createUrl.mockRestore();
  });

  it('reads an image into a local object URL (nothing is sent anywhere)', async () => {
    const createUrl = vi.spyOn(URL, 'createObjectURL').mockReturnValue('blob:local-photo');
    const { comp } = await render();

    comp.onFile(new File(['x'], 'kitchen.jpg', { type: 'image/jpeg' }));

    expect(createUrl).toHaveBeenCalledOnce();
    createUrl.mockRestore();
  });

  it('default corners form a usable quad, and arrow keys move a corner', async () => {
    const { defaultQuad, PhotoOverlayComponent } = await import('./photo-overlay.component');
    const { isUsable } = await import('./homography');
    expect(isUsable(defaultQuad(800, 600))).toBe(true);

    const fixture = TestBed.createComponent(PhotoOverlayComponent);
    fixture.componentRef.setInput('product', product('CT-Q', 'countertops'));
    const comp = fixture.componentInstance as unknown as { corners: () => { x: number; y: number }[]; nudge(i: number, e: KeyboardEvent): void };
    const before = comp.corners()[0]!;
    comp.nudge(0, new KeyboardEvent('keydown', { key: 'ArrowRight', shiftKey: true }));
    expect(comp.corners()[0]).toEqual({ x: before.x + 20, y: before.y });
    comp.nudge(0, new KeyboardEvent('keydown', { key: 'ArrowUp' }));
    expect(comp.corners()[0]).toEqual({ x: before.x + 20, y: before.y - 4 });
  });
});

describe('photo overlay rendering rules', () => {
  it('tiles only materials that repeat; countertops, sheds, paint are one piece', async () => {
    const { shouldTile } = await import('./photo-overlay.component');
    expect(['flooring', 'roofing', 'decking'].every(shouldTile)).toBe(true);
    expect(['countertops', 'cabinets', 'sheds', 'paint', 'windows-doors', 'fencing'].some(shouldTile)).toBe(false);
  });
});

describe('kitchen cabinets', () => {
  async function kitchen(attributes: Record<string, string>) {
    const fixture = TestBed.createComponent(SceneComponent);
    fixture.componentRef.setInput('scene', 'kitchen');
    fixture.componentRef.setInput('product', product('CAB', 'cabinets', { attributes }));
    await fixture.whenStable();
    return fixture.nativeElement as HTMLElement;
  }

  it('paints fronts in the cabinet colour, never tiles the product photo', async () => {
    const el = await kitchen({ style: 'flat-panel', color: 'Navy' });
    expect(el.querySelector('[data-surface="cabinets"]')?.getAttribute('fill')).toBe('#1f2f57');
    expect(el.querySelector('[data-surface="upper-cabinets"]')?.getAttribute('fill')).toBe('#1f2f57');
    expect(el.querySelector('[data-surface="cabinets"]')?.getAttribute('fill')).not.toContain('url(');
  });

  it('shaker doors get the recessed frame, flat-panel doors do not', async () => {
    const count = (el: HTMLElement) => el.querySelectorAll('rect[fill="none"]').length;
    expect(count(await kitchen({ style: 'shaker', color: 'White' }))).toBe(7);   // 3 upper + 4 base frames
    TestBed.resetTestingModule();
    expect(count(await kitchen({ style: 'flat-panel', color: 'White' }))).toBe(0);
  });
});

describe('photo mode per category', () => {
  it('recolours cabinets and walls, warps textures, places objects', async () => {
    const { photoModeFor } = await import('./photo-overlay.component');
    expect(photoModeFor('cabinets')).toBe('colour');
    expect(photoModeFor('paint')).toBe('colour');
    expect(photoModeFor('countertops')).toBe('texture');
    expect(photoModeFor('sheds')).toBe('object');
  });
});
