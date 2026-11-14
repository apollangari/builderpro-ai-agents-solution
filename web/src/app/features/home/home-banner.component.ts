import { Component } from '@angular/core';

/**
 * The home page banner: a blueprint sheet with a house elevation, a kitchen run and dimension lines.
 *
 * Pure inline SVG, no image file. It is drawn only with theme tokens: the sheet is --bp-accent and the
 * lines are --bp-on-accent, so a retailer's own accent colour (ThemeService) recolours it too.
 * Decorative: hidden from screen readers; the page heading says what the page is.
 */
@Component({
  selector: 'bp-home-banner',
  template: `
    <figure class="banner" aria-hidden="true" data-testid="home-banner">
      <svg viewBox="0 0 1200 320" preserveAspectRatio="xMinYMid slice" focusable="false">
        <defs>
          <pattern id="bp-grid" width="20" height="20" patternUnits="userSpaceOnUse">
            <path d="M20 0H0V20" class="grid" />
          </pattern>
          <pattern id="bp-grid-major" width="100" height="100" patternUnits="userSpaceOnUse">
            <path d="M100 0H0V100" class="grid major" />
          </pattern>
        </defs>
        <rect width="1200" height="320" class="sheet" />
        <rect width="1200" height="320" fill="url(#bp-grid)" />
        <rect width="1200" height="320" fill="url(#bp-grid-major)" />

        <!-- House elevation -->
        <g class="line">
          <path d="M150 262 V150 L300 62 L450 150 V262" />
          <path d="M122 166 L300 50 L478 166" />
          <path d="M380 92 V60 H410 V112" />
          <rect x="278" y="186" width="48" height="76" />
          <circle cx="317" cy="226" r="2.5" class="dot" />
          <rect x="182" y="178" width="62" height="48" />
          <path d="M213 178 V226 M182 202 H244" class="thin" />
          <rect x="356" y="178" width="62" height="48" />
          <path d="M387 178 V226 M356 202 H418" class="thin" />
          <path d="M60 262 H560" />
        </g>
        <g class="dim">
          <path d="M150 286 H450 M150 279 V293 M450 279 V293" />
          <path d="M486 150 V262 M479 150 H493 M479 262 H493" />
        </g>
        <text x="270" y="306" class="label">32 ft 0 in</text>
        <text x="498" y="210" class="label">10 ft</text>

        <!-- Kitchen run: upper cabinets, counter, base cabinets, sink -->
        <g class="line">
          <path d="M700 262 V88 H1110 V262" class="thin" />
          <rect x="740" y="100" width="300" height="62" />
          <path d="M815 100 V162 M890 100 V162 M965 100 V162" class="thin" />
          <rect x="732" y="196" width="316" height="10" />
          <rect x="740" y="206" width="300" height="56" />
          <path d="M840 206 V262 M940 206 V262" class="thin" />
          <path d="M868 196 v-14 h44 v14" class="thin" />
          <path d="M890 182 v-16 h10" class="thin" />
          <path d="M770 120 v22 M845 120 v22 M920 120 v22 M995 120 v22" class="thin" />
          <path d="M660 262 H1150" />
        </g>
        <g class="dim">
          <path d="M740 76 H1040 M740 69 V83 M1040 69 V83" />
          <path d="M1070 196 V262 M1063 196 H1077 M1063 262 H1077" />
        </g>
        <text x="852" y="66" class="label">12 ft 6 in</text>
        <text x="1082" y="234" class="label">36 in</text>

        <!-- Measuring marks between the two drawings -->
        <g class="dim">
          <circle cx="600" cy="150" r="18" />
          <path d="M600 124 V176 M574 150 H626" />
        </g>
      </svg>
    </figure>
  `,
  styles: `
    :host { display: block; }
    .banner {
      margin: 0;
      border-radius: var(--bp-radius-lg);
      overflow: hidden;
      background: var(--bp-accent);
      border: 1px solid var(--bp-border);
    }
    svg { display: block; width: 100%; height: clamp(9rem, 22vw, 15rem); }
    .sheet { fill: var(--bp-accent); }
    .grid { fill: none; stroke: color-mix(in srgb, var(--bp-on-accent) 8%, transparent); stroke-width: 1; }
    .major { stroke: color-mix(in srgb, var(--bp-on-accent) 15%, transparent); }
    .line path, .line rect { fill: none; stroke: var(--bp-on-accent); stroke-width: 2.4; stroke-linejoin: round; stroke-linecap: round; }
    .line .thin { stroke-width: 1.5; opacity: .8; }
    .dot { fill: var(--bp-on-accent); }
    .dim path, .dim circle { fill: none; stroke: color-mix(in srgb, var(--bp-on-accent) 60%, transparent); stroke-width: 1.2; }
    .label { fill: color-mix(in srgb, var(--bp-on-accent) 75%, transparent); font-family: var(--bp-font-heading); font-size: 14px; letter-spacing: .04em; }
  `,
})
export class HomeBanner {}
