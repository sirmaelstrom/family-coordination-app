// The 15 pins in contracts.test.ts all PASS, so they exercise none of the matcher's
// failure paths. These do — one case per way a Shape must be able to fail.
import { describe, expect, it } from 'vitest';
import { arrayOf, bool, nullable, num, objectOf, oneOf, str } from './shape';

const item = objectOf({ name: str, size: nullable(num), kind: oneOf('a', 'b') });
const root = objectOf({ id: num, ok: bool, items: arrayOf(item) });

const valid = { id: 1, ok: true, items: [{ name: 'x', size: null, kind: 'a' }] };

describe('objectOf', () => {
  it('accepts a conforming value', () => {
    expect(root.check(valid, 'r')).toEqual([]);
  });

  it('reports a declared key the value omits', () => {
    const { ok: _drop, ...missing } = valid;
    expect(root.check(missing, 'r')).toEqual(['r.ok: declared in types.ts, missing from the fixture']);
  });

  // The anti-whitelist property: this matcher reports undeclared keys rather than
  // dropping them, which is the whole reason it does not rebuild the value.
  it('reports a key the value has and the Shape does not', () => {
    expect(root.check({ ...valid, extra: 1 }, 'r')).toEqual([
      'r.extra: in the fixture, not declared in types.ts',
    ]);
  });

  // `key in obj` would consult the prototype chain, so an inherited name could pass as
  // declared (or as known) in a guard whose entire job is that it does not.
  it('does not treat an inherited name as a declared or known key', () => {
    const inherited = objectOf({ toString: str });
    expect(inherited.check({}, 'r')).toEqual([
      'r.toString: declared in types.ts, missing from the fixture',
    ]);
    expect(root.check({ ...valid, constructor: 'nope' }, 'r')).toEqual([
      'r.constructor: in the fixture, not declared in types.ts',
    ]);
  });

  it('reports a non-object where an object is declared', () => {
    expect(root.check([], 'r')).toEqual(['r: expected object, got array']);
    expect(root.check(null, 'r')).toEqual(['r: expected object, got null']);
  });
});

describe('field shapes', () => {
  it('reports a wrong primitive, naming the nested path', () => {
    const drifted = { ...valid, items: [{ name: 7, size: null, kind: 'a' }] };
    expect(root.check(drifted, 'r')).toEqual(['r.items[0].name: expected string, got number']);
  });

  it('accepts null only where nullable', () => {
    expect(item.check({ name: 'x', size: 3, kind: 'a' }, 'i')).toEqual([]);
    expect(item.check({ name: null, size: null, kind: 'a' }, 'i')).toEqual([
      'i.name: expected string, got null',
    ]);
  });

  it('reports a value outside the union, including a casing variant', () => {
    expect(item.check({ name: 'x', size: null, kind: 'A' }, 'i')).toEqual([
      'i.kind: expected one of a | b, got "A"',
    ]);
  });

  it('reports a non-array where an array is declared', () => {
    expect(root.check({ ...valid, items: {} }, 'r')).toEqual(['r.items: expected array, got object']);
  });
});
