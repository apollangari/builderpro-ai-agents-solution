import { Component, computed, input } from '@angular/core';
import { Product } from '../catalog/catalog.api';
import { SceneId, placementFor, swatchFor } from './scene';

let nextId = 0;

function isDark(hex: string): boolean {
  const n = parseInt(hex.slice(1), 16);
  return 0.299 * (n >> 16) + 0.587 * ((n >> 8) & 255) + 0.114 * (n & 255) < 110;
}

/**
 * A default scene (2l) with the selected product applied to its surface. Pure SVG: no photo needed,
 * works for every category that has a scene. Surfaces the product doesn't cover get neutral
 * defaults, so e.g. a countertop shows on neutral cabinets and a cabinet under a neutral top.
 */
@Component({
  selector: 'bp-scene',
  template: `
    <svg viewBox="0 0 800 500" role="img" [attr.aria-label]="label()" data-testid="scene" [attr.data-scene]="scene()">
      <defs>
        <pattern [attr.id]="patternId" patternUnits="userSpaceOnUse" [attr.width]="tile()" [attr.height]="tile() * 0.75">
          <image [attr.href]="product().imageUrl" [attr.width]="tile()" [attr.height]="tile() * 0.75" preserveAspectRatio="xMidYMid slice" />
        </pattern>
      </defs>

      @switch (scene()) {
        @case ('kitchen') {
          <rect width="800" height="500" fill="#efe9dd" />
          <rect x="430" y="60" width="200" height="140" fill="#cfe1ea" stroke="#fffdf8" stroke-width="10" />
          <!-- upper cabinets: 3 doors, same finish as the base cabinets -->
          <rect x="60" y="40" width="330" height="150" rx="4" [attr.fill]="cabinetFace()" stroke="#d6cdb8" stroke-width="3" data-surface="upper-cabinets" />
          @for (x of [60, 170, 280]; track x) {
            <rect [attr.x]="x + 8" y="48" width="94" height="134" rx="3" [attr.fill]="cabinetFace()" [attr.stroke]="cabinetEdge()" stroke-width="3" />
            @if (shaker()) {
              <rect [attr.x]="x + 20" y="60" width="70" height="110" fill="none" [attr.stroke]="cabinetEdge()" stroke-width="3" />
            }
            <rect [attr.x]="x + 88" y="140" width="6" height="26" rx="3" fill="#b9a77e" />
          }
          <rect x="60" y="250" width="680" height="60" fill="#e3dccc" />
          <!-- countertop: top surface + front edge -->
          <polygon points="40,300 760,300 740,270 60,270" [attr.fill]="on('countertop') ? fill() : '#ddd8cc'" data-surface="countertop" />
          <rect x="40" y="300" width="720" height="22" [attr.fill]="on('countertop') ? fill() : '#cfc9bb'" data-surface="countertop-edge" />
          <rect x="40" y="300" width="720" height="22" fill="#000" opacity=".12" />
          <!-- base cabinets: drawn in the product's colour and door style (a product photo can't tile as fronts) -->
          <rect x="60" y="322" width="680" height="168" [attr.fill]="cabinetFace()" stroke="#d6cdb8" stroke-width="3" data-surface="cabinets" />
          @for (x of [60, 230, 400, 570]; track x) {
            <rect [attr.x]="x + 10" y="332" width="150" height="148" rx="3" [attr.fill]="cabinetFace()" [attr.stroke]="cabinetEdge()" stroke-width="3" />
            @if (shaker()) {
              <rect [attr.x]="x + 26" y="348" width="118" height="116" fill="none" [attr.stroke]="cabinetEdge()" stroke-width="3" />
            }
            <rect [attr.x]="x + 135" y="390" width="8" height="34" rx="3" fill="#b9a77e" />
          }
        }
        @case ('house-roof') {
          <rect width="800" height="500" fill="#d9e6ef" />
          <rect y="430" width="800" height="70" fill="#a8b88f" />
          <rect x="190" y="250" width="420" height="190" fill="#efe9dd" />
          <polygon points="150,260 400,90 650,260" [attr.fill]="on('roof') ? fill() : '#6b6e74'" data-surface="roof" />
          <polygon points="150,260 400,90 650,260" fill="none" stroke="#14213d" stroke-opacity=".25" stroke-width="4" />
          <rect x="370" y="340" width="70" height="100" fill="#2b4a7a" />
          <rect x="230" y="300" width="90" height="70" fill="#cfe1ea" stroke="#fffdf8" stroke-width="6" />
          <rect x="480" y="300" width="90" height="70" fill="#cfe1ea" stroke="#fffdf8" stroke-width="6" />
        }
        @case ('backyard') {
          <rect width="800" height="500" fill="#d9e6ef" />
          <rect y="300" width="800" height="200" fill="#a8b88f" />
          @if (on('fence')) {
            <g data-surface="fence" [attr.href]="product().imageUrl">
              @for (x of [0, 200, 400, 600]; track x) {
                <image [attr.href]="product().imageUrl" [attr.x]="x" y="160" width="200" height="150" preserveAspectRatio="none" />
              }
            </g>
          } @else {
            <rect x="0" y="230" width="800" height="80" fill="#c7a27c" opacity=".7" />
          }
          <polygon points="60,470 520,470 470,360 110,360" [attr.fill]="on('deck') ? fill() : '#b08a62'" data-surface="deck" />
          @if (on('shed')) {
            <image [attr.href]="product().imageUrl" x="470" y="120" width="320" height="240" preserveAspectRatio="xMidYMid meet" data-surface="shed" />
          } @else {
            <rect x="560" y="220" width="160" height="110" fill="#b07a48" /><polygon points="545,225 640,160 735,225" fill="#6b4a2f" />
          }
        }
        @case ('room-floor') {
          <rect width="800" height="500" fill="#efe9dd" />
          <rect x="300" y="60" width="200" height="170" fill="#cfe1ea" stroke="#fffdf8" stroke-width="10" />
          <polygon points="0,500 800,500 650,300 150,300" [attr.fill]="on('floor') ? fill() : '#c8b79a'" data-surface="floor" />
          <rect x="150" y="296" width="500" height="8" fill="#fffdf8" />
          <rect x="250" y="330" width="300" height="70" rx="20" fill="#5e6b7d" opacity=".85" />
        }
        @case ('room-wall') {
          <rect width="800" height="500" [attr.fill]="on('wall') ? swatch() : '#efe9dd'" data-surface="wall" />
          <rect x="0" y="390" width="800" height="110" fill="#c49a6c" />
          <rect x="0" y="384" width="800" height="10" fill="#fffdf8" />
          <rect x="230" y="300" width="340" height="90" rx="24" fill="#5e6b7d" />
          <rect x="250" y="250" width="300" height="70" rx="20" fill="#6f7d91" />
          <rect x="320" y="110" width="160" height="110" fill="#fffdf8" stroke="#14213d" stroke-opacity=".2" stroke-width="6" />
        }
        @case ('house-front') {
          <rect width="800" height="500" fill="#d9e6ef" />
          <rect y="440" width="800" height="60" fill="#a8b88f" />
          <rect x="120" y="120" width="560" height="330" fill="#e7dfcf" />
          <polygon points="90,130 400,30 710,130" fill="#6b6e74" />
          @if (on('opening')) {
            <image [attr.href]="product().imageUrl" x="300" y="170" width="200" height="270" preserveAspectRatio="xMidYMid slice" data-surface="opening" />
          }
          <rect x="160" y="190" width="100" height="120" fill="#cfe1ea" stroke="#fffdf8" stroke-width="8" />
          <rect x="540" y="190" width="100" height="120" fill="#cfe1ea" stroke="#fffdf8" stroke-width="8" />
        }
      }
    </svg>
  `,
  styles: `
    :host { display: block; }
    svg { display: block; width: 100%; height: auto; border-radius: var(--bp-radius-lg); border: 1px solid var(--bp-border); background: var(--bp-surface-muted); }
  `,
})
export class SceneComponent {
  readonly scene = input.required<SceneId>();
  readonly product = input.required<Product>();

  protected readonly patternId = `bp-scene-pattern-${++nextId}`;
  private readonly placement = computed(() => placementFor(this.product().category));

  /** Texture tile size: bigger for stone slabs, smaller for repeating shingles and planks. */
  protected readonly tile = computed(() => (this.product().category === 'countertops' ? 260 : 160));
  protected readonly fill = computed(() => `url(#${this.patternId})`);
  protected readonly swatch = computed(() => swatchFor(this.product()));
  protected readonly label = computed(() => `${this.product().name}, shown in a ${this.scene().replace('-', ' ')} scene`);

  /** Cabinet fronts: the product's colour when cabinets are shown, a neutral otherwise. */
  protected readonly cabinetFace = computed(() => (this.on('cabinets') ? swatchFor(this.product()) : '#f4f1ea'));
  /** Door edges: a darker line on light doors, a lighter one on dark doors, so panels read on both. */
  protected readonly cabinetEdge = computed(() => (isDark(this.cabinetFace()) ? 'rgba(255,255,255,.22)' : 'rgba(20,33,61,.18)'));
  protected readonly shaker = computed(() => this.on('cabinets') && this.product().attributes['style'] === 'shaker');

  protected on(surface: string): boolean {
    return this.placement()?.surface === surface;
  }
}
