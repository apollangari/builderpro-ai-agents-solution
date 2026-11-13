import { Point, Quad, affine, homography, isUsable, project } from './homography';

const rect = (w: number, h: number): Quad => [{ x: 0, y: 0 }, { x: w, y: 0 }, { x: w, y: h }, { x: 0, y: h }];
const close = (a: Point, b: Point) => {
  expect(a.x).toBeCloseTo(b.x, 6);
  expect(a.y).toBeCloseTo(b.y, 6);
};

describe('homography', () => {
  it('maps each source corner exactly onto its target corner', () => {
    const dst: Quad = [{ x: 120, y: 80 }, { x: 610, y: 95 }, { x: 700, y: 330 }, { x: 60, y: 300 }];
    const h = homography(rect(400, 300), dst);
    rect(400, 300).forEach((p, i) => close(project(h, p), dst[i]!));
  });

  it('is the identity for the same rectangle', () => {
    const h = homography(rect(400, 300), rect(400, 300));
    close(project(h, { x: 123, y: 45 }), { x: 123, y: 45 });
  });

  it('is a plain scale + shift for an axis-aligned target', () => {
    const h = homography(rect(100, 100), [{ x: 10, y: 20 }, { x: 210, y: 20 }, { x: 210, y: 120 }, { x: 10, y: 120 }]);
    close(project(h, { x: 50, y: 50 }), { x: 110, y: 70 });
  });

  it('applies real perspective: the centre maps to where the diagonals cross, not the average', () => {
    const dst: Quad = [{ x: 300, y: 100 }, { x: 500, y: 100 }, { x: 800, y: 400 }, { x: 0, y: 400 }]; // a countertop seen from the front
    const c = project(homography(rect(400, 300), dst), { x: 200, y: 150 });
    // Diagonals (300,100)-(800,400) and (500,100)-(0,400) cross at t = 0.2: (400, 160). The corner average would be y = 250.
    close(c, { x: 400, y: 160 });
  });

  it('refuses degenerate corners', () => {
    expect(() => homography(rect(1, 1), [{ x: 0, y: 0 }, { x: 1, y: 1 }, { x: 2, y: 2 }, { x: 3, y: 3 }])).toThrow(/Degenerate/);
  });
});

describe('affine', () => {
  it('maps a triangle exactly onto another', () => {
    const s: [Point, Point, Point] = [{ x: 0, y: 0 }, { x: 10, y: 0 }, { x: 0, y: 10 }];
    const d: [Point, Point, Point] = [{ x: 5, y: 5 }, { x: 25, y: 9 }, { x: 3, y: 30 }];
    const [a, b, c, dd, e, f] = affine(s, d);
    s.forEach((p, i) => close({ x: a * p.x + c * p.y + e, y: b * p.x + dd * p.y + f }, d[i]!));
  });
});

describe('isUsable', () => {
  it('accepts a convex quad, rejects crossed, collinear or tiny ones', () => {
    expect(isUsable([{ x: 100, y: 100 }, { x: 500, y: 120 }, { x: 560, y: 380 }, { x: 60, y: 360 }])).toBe(true);
    expect(isUsable([{ x: 100, y: 100 }, { x: 500, y: 120 }, { x: 60, y: 360 }, { x: 560, y: 380 }])).toBe(false); // crossed
    expect(isUsable([{ x: 0, y: 0 }, { x: 10, y: 0 }, { x: 20, y: 0 }, { x: 0, y: 10 }])).toBe(false); // three in a line
    expect(isUsable([{ x: 0, y: 0 }, { x: 5, y: 0 }, { x: 5, y: 5 }, { x: 0, y: 5 }])).toBe(false); // tiny
  });
});
