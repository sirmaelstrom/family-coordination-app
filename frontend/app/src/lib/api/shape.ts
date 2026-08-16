// ─────────────────────────────────────────────────────────────────────────
// A dependency-free structural matcher for the M9 wire contract.
//
// TS types are erased at runtime, so nothing can compare a checked-in fixture
// to an `interface` directly. A Shape is the runtime half of a declared type:
// it validates JSON, and `Equals<Infer<typeof s>, T>` makes the compiler reject
// any Shape that has drifted from the type it claims to mirror. Breaking either
// half fails a CI job — `npm test` for the fixture, `npm run check` for the type.
//
// A Shape only ever REPORTS on a value; it never rebuilds one. A validator that
// rebuilds is a whitelist, and silently drops fields it was not told about —
// here an undeclared key is a failure, which is the whole point of the pin.
// ─────────────────────────────────────────────────────────────────────────

export interface Shape<T> {
  readonly check: (value: unknown, path: string) => string[];
  /** Phantom carrier for `Infer`. Never read at runtime. */
  readonly __t: T;
}

export type Infer<S> = S extends Shape<infer T> ? T : never;

/** True only when A and B are the same type — not merely mutually assignable. */
export type Equals<A, B> =
  (<T>() => T extends A ? 1 : 2) extends (<T>() => T extends B ? 1 : 2) ? true : false;

/** Compile-time assertion; `type _ = Expect<Equals<…>>` fails the build when false. */
export type Expect<T extends true> = T;

function shape<T>(check: (value: unknown, path: string) => string[]): Shape<T> {
  return { check, __t: undefined as unknown as T };
}

function describe(value: unknown): string {
  if (value === null) return 'null';
  if (Array.isArray(value)) return 'array';
  return typeof value;
}

export const str: Shape<string> = shape((v, p) =>
  typeof v === 'string' ? [] : [`${p}: expected string, got ${describe(v)}`]);

export const num: Shape<number> = shape((v, p) =>
  typeof v === 'number' ? [] : [`${p}: expected number, got ${describe(v)}`]);

export const bool: Shape<boolean> = shape((v, p) =>
  typeof v === 'boolean' ? [] : [`${p}: expected boolean, got ${describe(v)}`]);

export function nullable<T>(inner: Shape<T>): Shape<T | null> {
  return shape((v, p) => (v === null ? [] : inner.check(v, p)));
}

export function arrayOf<T>(inner: Shape<T>): Shape<T[]> {
  return shape((v, p) =>
    Array.isArray(v)
      ? v.flatMap((item, i) => inner.check(item, `${p}[${i}]`))
      : [`${p}: expected array, got ${describe(v)}`]);
}

/** A string-literal union — the shape of a camelCase-serialized C# enum. */
export function oneOf<const L extends readonly string[]>(...literals: L): Shape<L[number]> {
  return shape((v, p) =>
    typeof v === 'string' && (literals as readonly string[]).includes(v)
      ? []
      : [`${p}: expected one of ${literals.join(' | ')}, got ${JSON.stringify(v)}`]);
}

export function objectOf<F extends Record<string, Shape<unknown>>>(
  fields: F
): Shape<{ [K in keyof F]: Infer<F[K]> }> {
  return shape((v, p) => {
    if (v === null || typeof v !== 'object' || Array.isArray(v)) {
      return [`${p}: expected object, got ${describe(v)}`];
    }
    const actual = v as Record<string, unknown>;
    const errors: string[] = [];
    for (const key of Object.keys(fields)) {
      if (key in actual) errors.push(...fields[key].check(actual[key], `${p}.${key}`));
      else errors.push(`${p}.${key}: declared in types.ts, missing from the fixture`);
    }
    for (const key of Object.keys(actual)) {
      if (!(key in fields)) errors.push(`${p}.${key}: in the fixture, not declared in types.ts`);
    }
    return errors;
  });
}
