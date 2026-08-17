import { describe, expect, it } from 'vitest';
import {
  CAPACITY_LADDER_FIXTURE,
  CONTRACT_PINS,
  SERVER_ONLY_FIXTURES,
  WIRE_ENUMS,
  WIRE_ENUM_FIXTURE,
} from './contracts';

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

  // List-equality, not membership: oneOf can only reject values outside the union, so a NEW
  // C# enum member would sail through every fixture check. The wire-enums fixture carries
  // Enum.GetValues from the server; each list here must equal it exactly.
  it(`wire-enum vocabularies equal the server's enum members (${WIRE_ENUM_FIXTURE})`, () => {
    const json = fixtures.get(WIRE_ENUM_FIXTURE) as Record<string, string[]> | undefined;
    expect(json, `${WIRE_ENUM_FIXTURE} is pinned but not checked in`).toBeDefined();

    expect(Object.keys(json!).sort(), 'enum set drifted between fixture and WIRE_ENUMS').toEqual(
      Object.keys(WIRE_ENUMS).sort()
    );
    for (const [name, members] of Object.entries(WIRE_ENUMS)) {
      expect(json![name], `${name}: server members vs WIRE_ENUMS`).toEqual([...members]);
    }
  });

  // Without this, deleting a pin — or adding a fixture nobody pins — reads as a pass.
  it('pins every checked-in JSON fixture', () => {
    const declared = [
      ...CONTRACT_PINS.map((p) => p.fixture),
      ...SERVER_ONLY_FIXTURES,
      WIRE_ENUM_FIXTURE,
      CAPACITY_LADDER_FIXTURE, // checked by capacity-fit.test.ts + ChoreCapacityTests
    ];

    const unaccounted = [...fixtures.keys()].filter((f) => !declared.includes(f));
    expect(unaccounted, 'add a pin in contracts.ts, or name it in SERVER_ONLY_FIXTURES').toEqual([]);

    const stale = declared.filter((f) => !fixtures.has(f));
    expect(stale, 'declared in contracts.ts but absent on disk — renamed or deleted').toEqual([]);
  });
});
