import { Category, Product } from './catalog.api';
import { applyPicker, categoryTabs, filterGroups, keyAttributes } from './picker';

const p = (sku: string, category: string, attributes: Record<string, string>, name = sku): Product => ({
  sku, name, description: null, unit: 'each', listPrice: 1, currency: 'USD', category, imageUrl: null, attributes,
});

const countertops: Category = {
  id: 'countertops', name: 'Countertops', defaultUnit: 'sqft', scene: 'kitchen',
  attributes: [
    { key: 'material', label: 'Material', kind: 'choice', required: true, options: ['quartz', 'granite', 'marble', 'butcher-block'], unit: null },
    { key: 'color', label: 'Colour', kind: 'text', required: true, options: null, unit: null },
    { key: 'finish', label: 'Finish', kind: 'choice', required: false, options: ['polished', 'honed', 'matte'], unit: null },
    { key: 'thicknessCm', label: 'Thickness', kind: 'number', required: false, options: null, unit: 'cm' },
  ],
};
const sheds: Category = { id: 'sheds', name: 'Sheds', defaultUnit: 'each', scene: 'backyard', attributes: [] };
const paint: Category = { id: 'paint', name: 'Paint', defaultUnit: 'each', scene: 'room-wall', attributes: [] };

const products = [
  p('Q1', 'countertops', { material: 'quartz', color: 'White', finish: 'polished', thicknessCm: '3' }, 'Quartz white'),
  p('Q2', 'countertops', { material: 'quartz', color: 'Grey', finish: 'matte' }, 'Quartz grey'),
  p('G1', 'countertops', { material: 'granite', color: 'Black', finish: 'polished' }, 'Granite black'),
  p('S1', 'sheds', {}, 'Wood shed'),
];

describe('picker logic', () => {
  it('tabs: All first, then only categories the store sells, in platform order, with counts', () => {
    expect(categoryTabs(products, [countertops, paint, sheds])).toEqual([
      { id: null, name: 'All', count: 4 },
      { id: 'countertops', name: 'Countertops', count: 3 },
      { id: 'sheds', name: 'Sheds', count: 1 },
    ]);
  });

  it('filters: choice attributes only, values that exist, in option order, with counts', () => {
    expect(filterGroups(products, countertops)).toEqual([
      { key: 'material', label: 'Material', values: [{ value: 'quartz', count: 2 }, { value: 'granite', count: 1 }] },
      { key: 'finish', label: 'Finish', values: [{ value: 'polished', count: 2 }, { value: 'matte', count: 1 }] },
    ]);
  });

  it('filters: an attribute with a single value offers no choice, so it is left out', () => {
    expect(filterGroups(products.filter((x) => x.sku !== 'G1' && x.sku !== 'Q2'), countertops)).toEqual([]);
    expect(filterGroups(products, undefined)).toEqual([]);
  });

  it('applies category, every chosen value and search together', () => {
    expect(applyPicker(products, 'countertops', {}, '').map((x) => x.sku)).toEqual(['Q1', 'Q2', 'G1']);
    expect(applyPicker(products, 'countertops', { material: 'quartz' }, '').map((x) => x.sku)).toEqual(['Q1', 'Q2']);
    expect(applyPicker(products, 'countertops', { material: 'quartz', finish: 'matte' }, '').map((x) => x.sku)).toEqual(['Q2']);
    expect(applyPicker(products, null, { material: '' }, 'shed').map((x) => x.sku)).toEqual(['S1']);
    expect(applyPicker(products, null, {}, 'g1').map((x) => x.sku)).toEqual(['G1']);
  });

  it('card badges: up to three non-numeric attributes in definition order, readable', () => {
    expect(keyAttributes(products[0]!, countertops)).toEqual(['quartz', 'White', 'polished']);
    expect(keyAttributes(p('B', 'countertops', { material: 'butcher-block' }), countertops)).toEqual(['butcher block']);
    expect(keyAttributes(products[3]!, undefined)).toEqual([]);
  });
});
