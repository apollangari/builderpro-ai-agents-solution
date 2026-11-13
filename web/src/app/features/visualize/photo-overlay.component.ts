import { Component, DestroyRef, ElementRef, computed, effect, inject, input, signal, viewChild } from '@angular/core';
import { Product } from '../catalog/catalog.api';
import { Point, Quad, drawWarped, isUsable } from './homography';
import { placementFor, swatchFor } from './scene';

export const MAX_PHOTO_BYTES = 15 * 1024 * 1024;
const MAX_WIDTH = 900;
const CORNER_LABELS = ['top-left', 'top-right', 'bottom-right', 'bottom-left'];

/**
 * "Use your own photo" (2l-b). The photo never leaves the browser: it is read into an object URL
 * and drawn on a canvas. The customer drags 4 corners onto the surface (countertop, floor, wall,
 * roof...) and the selected product is drawn into that shape with perspective (homography.ts).
 */
@Component({
  selector: 'bp-photo-overlay',
  template: `
    <div class="tools">
      <label class="btn btn-primary upload">
        <input type="file" accept="image/*" (change)="onFile($any($event.target).files?.[0])" data-testid="photo-input" />
        {{ photo() ? 'Choose another photo' : 'Upload a photo' }}
      </label>
      @if (photo()) {
        <button type="button" class="btn" (click)="resetCorners()">Reset corners</button>
        <button type="button" class="btn" (click)="removePhoto()" data-testid="photo-remove">Remove photo</button>
      }
    </div>
    <p class="privacy">Your photo stays on this device; it isn't uploaded anywhere.</p>

    @if (error(); as e) {
      <div class="warning" role="alert" data-testid="photo-error"><span>{{ e }}</span></div>
    }

    @if (photo()) {
      <div class="stage" [style.aspect-ratio]="size().w + ' / ' + size().h">
        <canvas #canvas [width]="size().w" [height]="size().h" data-testid="photo-canvas" aria-label="Your photo with the product applied"></canvas>
        @for (c of corners(); track $index) {
          <button
            type="button"
            class="handle"
            [style.left.%]="(c.x / size().w) * 100"
            [style.top.%]="(c.y / size().h) * 100"
            [attr.aria-label]="'Move ' + cornerLabels[$index] + ' corner (arrow keys)'"
            [attr.data-testid]="'corner-' + $index"
            (pointerdown)="startDrag($index, $event)"
            (keydown)="nudge($index, $event)"
          ></button>
        }
      </div>
      @if (!usable()) {
        <p class="hint" data-testid="photo-hint">Drag the four corners onto the surface, in order around it (they can't cross).</p>
      } @else {
        <p class="hint">Drag the corners onto the {{ surfaceName() }} in your photo.</p>
      }
    }
  `,
  styles: `
    .tools { display: flex; gap: .6rem; flex-wrap: wrap; align-items: center; }
    .upload { position: relative; overflow: hidden; }
    .upload input { position: absolute; inset: 0; opacity: 0; cursor: pointer; }
    .upload:focus-within { outline: 2px solid var(--bp-accent); outline-offset: 2px; }
    .privacy, .hint { color: var(--bp-ink-muted); font-size: .85rem; margin: .5rem 0; }
    .stage { position: relative; width: 100%; max-width: 900px; touch-action: none; user-select: none; }
    canvas { display: block; width: 100%; height: 100%; border-radius: var(--bp-radius-lg); border: 1px solid var(--bp-border); }
    .handle {
      position: absolute; width: 44px; height: 44px; margin: -22px 0 0 -22px; padding: 0; border-radius: 50%;
      background: transparent; border: 0; cursor: grab;
    }
    .handle::after {
      content: ''; position: absolute; inset: 12px; border-radius: 50%;
      background: var(--bp-accent); border: 3px solid var(--bp-on-accent); box-shadow: 0 1px 4px rgba(0, 0, 0, .35);
    }
    .handle:active { cursor: grabbing; }
  `,
})
export class PhotoOverlayComponent {
  readonly product = input.required<Product>();

  protected readonly cornerLabels = CORNER_LABELS;
  protected readonly photo = signal<HTMLImageElement | null>(null);
  protected readonly size = signal({ w: 800, h: 600 });
  protected readonly corners = signal<Quad>(defaultQuad(800, 600));
  protected readonly error = signal<string | null>(null);
  protected readonly usable = computed(() => isUsable(this.corners()));
  protected readonly surfaceName = computed(() => placementFor(this.product().category)?.surface ?? 'surface');

  private readonly canvas = viewChild<ElementRef<HTMLCanvasElement>>('canvas');
  private readonly texture = signal<HTMLImageElement | null>(null);
  private objectUrl: string | null = null;
  private drag: { index: number; pointerId: number } | null = null;

  constructor() {
    // Load the product image whenever the product changes.
    effect((onCleanup) => {
      const url = this.product().imageUrl;
      if (!url) { this.texture.set(null); return; }
      const img = new Image();
      let live = true;
      img.onload = () => live && this.texture.set(img);
      img.src = url;
      onCleanup(() => (live = false));
    });

    // Redraw whenever anything visible changes.
    effect(() => this.draw(this.canvas()?.nativeElement, this.photo(), this.texture(), this.corners()));

    inject(DestroyRef).onDestroy(() => this.revoke());
  }

  onFile(file: File | undefined): void {
    this.error.set(null);
    if (!file) return;
    if (!file.type.startsWith('image/')) { this.error.set('That file is not an image. Choose a JPG, PNG or HEIC photo.'); return; }
    if (file.size > MAX_PHOTO_BYTES) { this.error.set('That photo is larger than 15 MB. Choose a smaller one.'); return; }

    this.revoke();
    this.objectUrl = URL.createObjectURL(file);
    const img = new Image();
    img.onload = () => {
      const scale = Math.min(1, MAX_WIDTH / img.naturalWidth);
      const w = Math.round(img.naturalWidth * scale), h = Math.round(img.naturalHeight * scale);
      this.size.set({ w, h });
      this.corners.set(defaultQuad(w, h));
      this.photo.set(img);
    };
    img.onerror = () => this.error.set("That photo couldn't be opened. Try another one.");
    img.src = this.objectUrl;
  }

  removePhoto(): void {
    this.revoke();
    this.photo.set(null);
  }

  resetCorners(): void {
    this.corners.set(defaultQuad(this.size().w, this.size().h));
  }

  startDrag(index: number, e: PointerEvent): void {
    const handle = e.currentTarget as HTMLElement;
    try {
      handle.setPointerCapture(e.pointerId); // keep receiving moves when the pointer leaves the handle
    } catch {
      /* pointer not active (synthetic events, some styluses): dragging still works without capture */
    }
    this.drag = { index, pointerId: e.pointerId };
    const move = (ev: PointerEvent) => this.drag?.pointerId === ev.pointerId && this.moveTo(index, this.toCanvas(ev));
    const up = (ev: PointerEvent) => {
      if (this.drag?.pointerId !== ev.pointerId) return;
      this.drag = null;
      handle.removeEventListener('pointermove', move);
      handle.removeEventListener('pointerup', up);
    };
    handle.addEventListener('pointermove', move);
    handle.addEventListener('pointerup', up);
  }

  /** Arrow keys move a corner 4 px (Shift: 20 px), so the overlay works without a mouse. */
  nudge(index: number, e: KeyboardEvent): void {
    const step = e.shiftKey ? 20 : 4;
    const delta = ({ ArrowLeft: [-step, 0], ArrowRight: [step, 0], ArrowUp: [0, -step], ArrowDown: [0, step] } as Record<string, [number, number]>)[e.key];
    if (!delta) return;
    e.preventDefault();
    const c = this.corners()[index]!;
    this.moveTo(index, { x: c.x + delta[0], y: c.y + delta[1] });
  }

  private moveTo(index: number, p: Point): void {
    const { w, h } = this.size();
    const clamped = { x: Math.max(0, Math.min(w, p.x)), y: Math.max(0, Math.min(h, p.y)) };
    this.corners.update((q) => q.map((c, i) => (i === index ? clamped : c)) as unknown as Quad);
  }

  private toCanvas(e: PointerEvent): Point {
    const r = this.canvas()!.nativeElement.getBoundingClientRect();
    return { x: ((e.clientX - r.left) / r.width) * this.size().w, y: ((e.clientY - r.top) / r.height) * this.size().h };
  }

  private draw(canvas: HTMLCanvasElement | undefined, photo: HTMLImageElement | null, texture: HTMLImageElement | null, quad: Quad): void {
    const ctx = canvas?.getContext('2d');
    if (!ctx || !photo) return; // (no canvas in unit tests)
    const { w, h } = this.size();
    ctx.setTransform(1, 0, 0, 1, 0, 0);
    ctx.clearRect(0, 0, w, h);
    ctx.drawImage(photo, 0, 0, w, h);
    if (!isUsable(quad)) return;

    const category = this.product().category;
    const mode = photoModeFor(category);
    const outline = () => {
      ctx.beginPath();
      quad.forEach((p, i) => (i ? ctx.lineTo(p.x, p.y) : ctx.moveTo(p.x, p.y)));
      ctx.closePath();
    };

    if (mode === 'colour') {
      ctx.globalCompositeOperation = 'multiply'; // paint, cabinets: recolour, keeping the light, shadows and detail
      ctx.globalAlpha = 0.9;
      ctx.fillStyle = swatchFor(this.product());
      outline();
      ctx.fill();
    } else if (texture) {
      // 1) Warp onto a separate, fully OPAQUE layer: the triangles overlap by a hair to hide gaps,
      //    and at full opacity an overlap paints the same pixel twice with no visible seam.
      const layer = document.createElement('canvas');
      layer.width = w;
      layer.height = h;
      const lctx = layer.getContext('2d');
      if (lctx) {
        const source = shouldTile(category) ? tiled(texture) : texture;
        drawWarped(lctx, source, source.width, source.height, quad, 24);
        // 2) Put the product on the photo ONCE (no see-through, no double edges).
        ctx.drawImage(layer, 0, 0);
        // 3) Bring the room's light back: multiply the photo over the new surface, clipped to it,
        //    so shadows, reflections and edges in the photo still read on the countertop/floor.
        ctx.save();
        outline();
        ctx.clip();
        ctx.globalCompositeOperation = 'multiply';
        ctx.globalAlpha = SHADING;
        ctx.drawImage(photo, 0, 0, w, h);
        ctx.restore();
      }
    }
    ctx.globalAlpha = 1;
    ctx.globalCompositeOperation = 'source-over';
    ctx.strokeStyle = 'rgba(255,255,255,.9)';
    ctx.lineWidth = 2;
    ctx.beginPath();
    quad.forEach((p, i) => (i ? ctx.lineTo(p.x, p.y) : ctx.moveTo(p.x, p.y)));
    ctx.closePath();
    ctx.stroke();
  }

  private revoke(): void {
    if (this.objectUrl) URL.revokeObjectURL(this.objectUrl);
    this.objectUrl = null;
  }
}

/** A centred quad covering the middle of the photo: a sensible start to drag from. */
export function defaultQuad(w: number, h: number): Quad {
  return [
    { x: w * 0.25, y: h * 0.45 },
    { x: w * 0.75, y: h * 0.45 },
    { x: w * 0.85, y: h * 0.75 },
    { x: w * 0.15, y: h * 0.75 },
  ];
}

/**
 * How a product goes onto a PHOTO. Mostly the scene placement, except cabinets: real cabinets are
 * already in the photo, so pasting a product picture over them can't look right. Recolouring the
 * existing fronts (multiply, like paint) keeps their doors, handles and shadows.
 */
export function photoModeFor(category: string): 'texture' | 'object' | 'colour' {
  if (category === 'cabinets') return 'colour';
  return placementFor(category)?.mode ?? 'texture';
}

/** How much of the photo's own light and shadow shows through the new surface (multiply pass). */
export const SHADING = 0.45;

/**
 * Repeating materials (planks, tiles, shingle courses, deck boards) are tiled so they keep a
 * realistic scale over a large surface. Countertops are one continuous slab: tiling them shows a
 * visible repeat, so they are drawn as a single piece.
 */
export function shouldTile(category: string): boolean {
  return category === 'flooring' || category === 'roofing' || category === 'decking';
}

/** Tiles a texture 3 x 3 so a large surface shows repeating material, not one stretched swatch. */
function tiled(img: HTMLImageElement): HTMLCanvasElement {
  const c = document.createElement('canvas');
  c.width = img.naturalWidth * 3 || 1200;
  c.height = img.naturalHeight * 3 || 900;
  const ctx = c.getContext('2d');
  if (ctx) for (let x = 0; x < 3; x++) for (let y = 0; y < 3; y++) ctx.drawImage(img, x * c.width / 3, y * c.height / 3, c.width / 3, c.height / 3);
  return c;
}
