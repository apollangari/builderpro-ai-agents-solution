import { TestBed } from '@angular/core/testing';
import { DEFAULT_BRAND, ThemeService } from './theme.service';

describe('ThemeService', () => {
  const style = () => document.documentElement.style;

  afterEach(() => TestBed.inject(ThemeService).apply()); // back to defaults

  it('sets a retailer accent on <html>', () => {
    const theme = TestBed.inject(ThemeService);

    theme.apply({ retailerName: 'Acme', projectLabel: 'Kitchen', accent: { accent: '#0f766e', accentHover: '#115e59', accentSoft: '#e6f4f2' } });

    expect(style().getPropertyValue('--bp-accent')).toBe('#0f766e');
    expect(style().getPropertyValue('--bp-accent-hover')).toBe('#115e59');
    expect(style().getPropertyValue('--bp-accent-soft')).toBe('#e6f4f2');
    expect(theme.retailerName()).toBe('Acme');
    expect(theme.projectLabel()).toBe('Kitchen');
  });

  it('only touches the three accent tokens', () => {
    const theme = TestBed.inject(ThemeService);
    const accent = { accent: '#000001', bg: '#ff0000', ink: '#ff0000' } as unknown as { accent: string };

    theme.apply({ ...DEFAULT_BRAND, accent });

    expect(style().getPropertyValue('--bp-accent')).toBe('#000001');
    expect(style().getPropertyValue('--bp-bg')).toBe('');
    expect(style().getPropertyValue('--bp-ink')).toBe('');
  });

  it('falls back to the BuilderPro defaults when no accent is given', () => {
    const theme = TestBed.inject(ThemeService);
    theme.apply({ ...DEFAULT_BRAND, accent: { accent: '#123456' } });

    theme.apply();

    expect(style().getPropertyValue('--bp-accent')).toBe('');
    expect(theme.retailerName()).toBe(DEFAULT_BRAND.retailerName);
  });
});
