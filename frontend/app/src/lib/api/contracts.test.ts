import { describe, expect, it } from 'vitest';
import { CONTRACT_PINS, SERVER_ONLY_FIXTURES } from './contracts';

const FIXTURE_MODULES = import.meta.glob<unknown>(
  '../../../../../tests/FamilyCoordinationApp.Tests/Fixtures/**/*.json',
  { eager: true, import: 'default' }
);

/** Keyed by the fixture's path below Fixtures/, matching CONTRACT_PINS. */
const fixtures = new Map(
  Object.entries(FIXTURE_MODULES).map(([key, value]) => [key.split('Fixtures/')[1], value])
);

describe('M9 wire contract', () => {
  it.each(CONTRACT_PINS)('$fixture matches $type', ({ fixture, shape }) => {
    const json = fixtures.get(fixture);
    expect(json, `${fixture} is pinned but not checked in`).toBeDefined();
    expect(shape.check(json, fixture.replace('.json', ''))).toEqual([]);
  });

  // Without this, deleting a pin — or adding a fixture nobody pins — reads as a pass.
  it('pins every checked-in fixture', () => {
    const accounted = new Set([...CONTRACT_PINS.map((p) => p.fixture), ...SERVER_ONLY_FIXTURES]);
    const unpinned = [...fixtures.keys()].filter((f) => !accounted.has(f));

    expect(unpinned, 'add a pin in contracts.ts, or name it in SERVER_ONLY_FIXTURES').toEqual([]);
    expect(fixtures.size).toBe(accounted.size);
  });
});
