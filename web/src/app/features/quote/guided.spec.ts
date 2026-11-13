import { OptionGroup, defaultMeasureForm, inches, measurementsBody, missingLabels, toChoices } from './guided';

const group = (id: string, selection: 'single' | 'quantity', label = id): OptionGroup => ({
  id, step: 'options', label, help: null, selection, basis: selection === 'single' ? 'edgeFt' : 'choice', required: true, noneLabel: 'None', options: [],
});

describe('guided quote helpers', () => {
  it('converts feet and inches to inches', () => {
    expect(inches({ ft: 10, in: 6 })).toBe(126);
    expect(inches({ ft: 0, in: 0 })).toBe(0);
  });

  it('sends countertop sections in inches, nothing computed', () => {
    const f = defaultMeasureForm();
    f.countertop.island = [{ width: { ft: 6, in: 0 }, depthIn: 36 }];
    expect(measurementsBody('countertop', f)).toEqual({
      countertop: { layout: 'straight', countertop: [{ widthIn: 120, depthIn: 25 }], island: [{ widthIn: 72, depthIn: 36 }], backsplash: [] },
    });
    expect(measurementsBody('count', f)).toEqual({ count: 1 });
    expect(measurementsBody('area', f)).toEqual({ rooms: [{ lengthFt: 12, widthFt: 10 }] });
  });

  it('turns answers into choices: singles, counts above zero, explicit none', () => {
    const groups = [group('edge', 'single'), group('sink', 'quantity'), group('cutouts', 'quantity'), group('plan', 'single')];
    const choices = toChoices(groups, {
      edge: { kind: 'single', sku: 'EDGE-OGEE' },
      sink: { kind: 'quantity', counts: {}, none: true },
      cutouts: { kind: 'quantity', counts: { 'CUT-FAUCET': 5, 'CUT-COOKTOP': 0 }, none: false },
      plan: { kind: 'single', sku: null },
    });
    expect(choices).toEqual([
      { group: 'edge', sku: 'EDGE-OGEE' },
      { group: 'sink', sku: null },
      { group: 'cutouts', sku: 'CUT-FAUCET', quantity: 5 },
      { group: 'plan', sku: null },
    ]);
  });

  it('names open steps by label, in store order', () => {
    expect(missingLabels([group('edge', 'single', 'Edge style'), group('plan', 'single', 'Protection plan')], ['plan', 'edge']))
      .toEqual(['Edge style', 'Protection plan']);
  });
});
