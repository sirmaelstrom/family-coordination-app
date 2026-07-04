// ─────────────────────────────────────────────────────────────────────────
// Quantity formatting + servings-scaling math. Client-only (no endpoint).
//
// ⚠ TWO DISTINCT FORMATTERS — do NOT merge them (spec §6 / §11.10):
//   • formatScaledQuantity — the DRAWER's *scaled* values. Uses RANGE
//     thresholds (Recipes.razor FormatQuantity, :897-905) because a scaled
//     value is fuzzy (e.g. 0.333… → "⅓"). Unicode-fraction glyphs.
//   • formatExactQuantity — the ingredient-LIST display. Uses EXACT-equality
//     checks (IngredientList.razor FormatQuantity, :106-132) because entered
//     values are precise. ASCII "1/2"-style fractions + mixed numbers.
// They differ on purpose; porting from intuition diverges.
// ─────────────────────────────────────────────────────────────────────────

// Unicode fraction glyphs used by the scaled formatter.
const QUARTER = '¼'; // ¼
const THIRD = '⅓'; // ⅓
const HALF = '½'; // ½
const TWO_THIRDS = '⅔'; // ⅔
const THREE_QUARTERS = '¾'; // ¾
const TIMES = '×'; // ×

/** Mirror C# "0.##": round to ≤2 decimals, trim trailing zeros. */
function formatDecimal(n: number): string {
  const rounded = Math.round(n * 100) / 100;
  return String(rounded);
}

/**
 * Format a SCALED quantity for the detail drawer (range thresholds — the value
 * is the product of user scaling, so it's approximate). Ports
 * Recipes.razor:887-912 exactly.
 */
export function formatScaledQuantity(qty: number): string {
  if (qty === Math.floor(qty)) {
    return String(Math.trunc(qty));
  }

  const fractionalPart = qty - Math.floor(qty);
  const wholePart = Math.floor(qty);

  let fraction: string | null = null;
  if (fractionalPart >= 0.2 && fractionalPart < 0.3) fraction = QUARTER;
  else if (fractionalPart >= 0.3 && fractionalPart < 0.4) fraction = THIRD;
  else if (fractionalPart >= 0.45 && fractionalPart < 0.55) fraction = HALF;
  else if (fractionalPart >= 0.6 && fractionalPart < 0.7) fraction = TWO_THIRDS;
  else if (fractionalPart >= 0.7 && fractionalPart < 0.8) fraction = THREE_QUARTERS;

  if (fraction != null) {
    return wholePart > 0 ? `${wholePart} ${fraction}` : fraction;
  }

  return formatDecimal(qty);
}

/**
 * Format an EXACT entered quantity for the ingredient list (exact-equality
 * fractions + mixed numbers). Ports IngredientList.razor:106-132 exactly.
 */
export function formatExactQuantity(qty: number): string {
  // Common fractions (exact).
  if (qty === 0.25) return '1/4';
  if (qty === 0.5) return '1/2';
  if (qty === 0.75) return '3/4';
  if (qty === 0.33 || qty === 1 / 3) return '1/3';
  if (qty === 0.67 || qty === 2 / 3) return '2/3';

  // Mixed fractions.
  const whole = Math.trunc(qty);
  const frac = qty - whole;

  if (frac === 0) return formatDecimal(whole);

  if (whole > 0) {
    if (frac === 0.25) return `${whole} 1/4`;
    if (frac === 0.5) return `${whole} 1/2`;
    if (frac === 0.75) return `${whole} 3/4`;
    if (frac === 0.33 || Math.abs(frac - 1 / 3) < 0.01) return `${whole} 1/3`;
    if (frac === 0.67 || Math.abs(frac - 2 / 3) < 0.01) return `${whole} 2/3`;
  }

  // Decimal fallback.
  return formatDecimal(qty);
}

/** Scaling factor = scaledServings / baseServings (1 when base is null/0). */
export function getScalingFactor(baseServings: number | null, scaledServings: number): number {
  if (baseServings == null || baseServings === 0) return 1;
  return scaledServings / baseServings;
}

/** Apply a scaling factor to one quantity. */
export function getScaledQuantity(original: number, factor: number): number {
  return original * factor;
}

/** The "×2" / "½×" / "1.5×" badge for the drawer. Ports Recipes.razor:930-937. */
export function getScalingLabel(factor: number): string {
  if (factor === 2) return `2${TIMES}`;
  if (factor === 0.5) return `${HALF}${TIMES}`;
  if (factor === 1.5) return `1.5${TIMES}`;
  return `${formatDecimal(factor)}${TIMES}`;
}
