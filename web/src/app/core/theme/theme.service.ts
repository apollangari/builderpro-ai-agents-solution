import { DOCUMENT } from '@angular/common';
import { Injectable, inject, signal } from '@angular/core';

/** The ONLY tokens a retailer may override. Everything else in the theme is BuilderPro's. */
export interface RetailerAccent {
  accent: string;
  accentHover: string;
  accentSoft: string;
}

/** What the shell shows for the current retailer. */
export interface RetailerBrand {
  retailerName: string;
  projectLabel: string;
  accent?: Partial<RetailerAccent>;
}

const ACCENT_VARS: Record<keyof RetailerAccent, string> = {
  accent: '--bp-accent',
  accentHover: '--bp-accent-hover',
  accentSoft: '--bp-accent-soft',
};

/** Placeholder until the brand comes from the tenant's configuration. */
export const DEFAULT_BRAND: RetailerBrand = {
  retailerName: 'BuilderPro Demo Store',
  projectLabel: 'Sample project',
};

/**
 * White-label hook. Sets the retailer's accent on <html> as inline CSS variables, which win over
 * the :root defaults in styles.css. Only the three accent tokens can change: a retailer can make
 * the app theirs without breaking contrast or layout.
 *
 * Static for now; later `apply()` will be called with the tenant's config.
 */
@Injectable({ providedIn: 'root' })
export class ThemeService {
  private readonly root = inject(DOCUMENT).documentElement;

  readonly retailerName = signal(DEFAULT_BRAND.retailerName);
  readonly projectLabel = signal(DEFAULT_BRAND.projectLabel);

  apply(brand: RetailerBrand = DEFAULT_BRAND): void {
    this.retailerName.set(brand.retailerName);
    this.projectLabel.set(brand.projectLabel);

    for (const key of Object.keys(ACCENT_VARS) as (keyof RetailerAccent)[]) {
      const value = brand.accent?.[key];
      if (value) {
        this.root.style.setProperty(ACCENT_VARS[key], value);
      } else {
        this.root.style.removeProperty(ACCENT_VARS[key]); // fall back to the BuilderPro default
      }
    }
  }
}
