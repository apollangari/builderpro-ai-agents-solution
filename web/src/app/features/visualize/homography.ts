/**
 * Perspective maths for the photo visualizer (2l-b). Pure functions, no DOM: unit-tested exactly.
 *
 * A homography H maps the texture's rectangle onto the 4 corners the customer dragged onto their
 * photo (a countertop seen at an angle is a trapezoid, not a rectangle). Canvas 2D can only draw
 * affine transforms, so drawWarped() splits the rectangle into a grid of small triangles and draws
 * each with the affine transform that matches H at its three corners. Fine grids look perspective-
 * correct to the eye.
 */
export interface Point {
  x: number;
  y: number;
}

/** Corners in order: top-left, top-right, bottom-right, bottom-left. */
export type Quad = readonly [Point, Point, Point, Point];

/** 3x3 matrix, row-major, h[8] = 1. */
export type Homography = readonly number[];

/** Solves A x = b (n x n) by Gaussian elimination with partial pivoting. Throws if singular. */
function solve(a: number[][], b: number[]): number[] {
  const n = b.length;
  const m = a.map((row, i) => [...row, b[i]!]);
  for (let col = 0; col < n; col++) {
    let pivot = col;
    for (let r = col + 1; r < n; r++) if (Math.abs(m[r]![col]!) > Math.abs(m[pivot]![col]!)) pivot = r;
    if (Math.abs(m[pivot]![col]!) < 1e-12) throw new Error('Degenerate corners: three of them are on one line.');
    [m[col], m[pivot]] = [m[pivot]!, m[col]!];
    for (let r = 0; r < n; r++) {
      if (r === col) continue;
      const f = m[r]![col]! / m[col]![col]!;
      for (let c = col; c <= n; c++) m[r]![c]! -= f * m[col]![c]!;
    }
  }
  return m.map((row, i) => row[n]! / row[i]!);
}

/** The homography taking each src corner to the matching dst corner. */
export function homography(src: Quad, dst: Quad): Homography {
  const a: number[][] = [];
  const b: number[] = [];
  for (let i = 0; i < 4; i++) {
    const { x, y } = src[i]!;
    const { x: u, y: v } = dst[i]!;
    a.push([x, y, 1, 0, 0, 0, -u * x, -u * y]);
    b.push(u);
    a.push([0, 0, 0, x, y, 1, -v * x, -v * y]);
    b.push(v);
  }
  return [...solve(a, b), 1];
}

export function project(h: Homography, p: Point): Point {
  const w = h[6]! * p.x + h[7]! * p.y + h[8]!;
  return { x: (h[0]! * p.x + h[1]! * p.y + h[2]!) / w, y: (h[3]! * p.x + h[4]! * p.y + h[5]!) / w };
}

/** The affine transform (canvas setTransform a..f) mapping triangle s onto triangle d. */
export function affine(s: [Point, Point, Point], d: [Point, Point, Point]): [number, number, number, number, number, number] {
  const [p0, p1, p2] = s;
  const det = (p1.x - p0.x) * (p2.y - p0.y) - (p2.x - p0.x) * (p1.y - p0.y);
  if (Math.abs(det) < 1e-12) throw new Error('Degenerate triangle');
  const sx1 = p1.x - p0.x, sy1 = p1.y - p0.y, sx2 = p2.x - p0.x, sy2 = p2.y - p0.y;
  const dx1 = d[1].x - d[0].x, dy1 = d[1].y - d[0].y, dx2 = d[2].x - d[0].x, dy2 = d[2].y - d[0].y;
  const a = (dx1 * sy2 - dx2 * sy1) / det;
  const b = (dy1 * sy2 - dy2 * sy1) / det;
  const c = (dx2 * sx1 - dx1 * sx2) / det;
  const dd = (dy2 * sx1 - dy1 * sx2) / det;
  return [a, b, c, dd, d[0].x - a * p0.x - c * p0.y, d[0].y - b * p0.x - dd * p0.y];
}

/** A quad is usable if it is convex and not tiny (corners crossed or bunched up = no warp). */
export function isUsable(q: Quad, minArea = 400): boolean {
  let sign = 0;
  for (let i = 0; i < 4; i++) {
    const a = q[i]!, b = q[(i + 1) % 4]!, c = q[(i + 2) % 4]!;
    const cross = (b.x - a.x) * (c.y - b.y) - (b.y - a.y) * (c.x - b.x);
    if (Math.abs(cross) < 1e-9) return false;
    if (sign === 0) sign = Math.sign(cross);
    else if (Math.sign(cross) !== sign) return false;
  }
  const area = Math.abs(q.reduce((s, p, i) => s + p.x * q[(i + 1) % 4]!.y - q[(i + 1) % 4]!.x * p.y, 0)) / 2;
  return area >= minArea;
}

/**
 * Draws `source` (w x h) warped into `quad` on ctx, as `grid` x `grid` cells of two triangles.
 * Each triangle is clipped and drawn with its own affine transform (slightly expanded to hide seams).
 */
export function drawWarped(ctx: CanvasRenderingContext2D, source: CanvasImageSource, w: number, h: number, quad: Quad, grid = 16): void {
  const H = homography([{ x: 0, y: 0 }, { x: w, y: 0 }, { x: w, y: h }, { x: 0, y: h }], quad);
  const pt = (i: number, j: number): Point => ({ x: (w * i) / grid, y: (h * j) / grid });
  const tri = (s: [Point, Point, Point]) => {
    const d = s.map((p) => project(H, p)) as [Point, Point, Point];
    ctx.save();
    ctx.beginPath();
    const cx = (d[0].x + d[1].x + d[2].x) / 3, cy = (d[0].y + d[1].y + d[2].y) / 3;
    d.forEach((p, k) => {
      const ex = p.x + Math.sign(p.x - cx) * 0.6, ey = p.y + Math.sign(p.y - cy) * 0.6; // overlap seams
      if (k === 0) ctx.moveTo(ex, ey); else ctx.lineTo(ex, ey);
    });
    ctx.closePath();
    ctx.clip();
    ctx.setTransform(...affine(s, d));
    ctx.drawImage(source, 0, 0, w, h);
    ctx.restore();
  };
  for (let i = 0; i < grid; i++) {
    for (let j = 0; j < grid; j++) {
      tri([pt(i, j), pt(i + 1, j), pt(i, j + 1)]);
      tri([pt(i + 1, j), pt(i + 1, j + 1), pt(i, j + 1)]);
    }
  }
}
