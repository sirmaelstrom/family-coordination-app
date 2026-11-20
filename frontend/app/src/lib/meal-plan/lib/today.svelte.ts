// ─────────────────────────────────────────────────────────────────────────
// Today, as reactive state.
//
// `todayStr()` is a plain clock read: a `$derived(todayStr())` or a template
// `isToday(day)` has no reactive dependency, so it never runs again, and a
// meal plan left open across midnight kept yesterday's Today chip, kept
// "Jump to Today" disabled on yesterday, and kept yesterday's column
// highlighted (fca#170 review, quest 70f237a1).
//
// A TodayClock holds the local day in `$state` and moves it two ways:
//   • a timer armed for the next local midnight, re-armed each time it fires;
//   • `sync()`, a plain re-read, for when the page wakes. A sleeping phone runs
//     no timer, so the owner calls it on every liveness tick (each poll while
//     visible, and at once when the tab is shown again).
//
// App.svelte owns one per mount (so it is read fresh on every visit) and hands
// `value` to every component that marks today. Nothing else in the meal plan
// reads the clock for display.
// ─────────────────────────────────────────────────────────────────────────

import { todayStr } from './dates';

/** A timer that fires a hair early re-arms at least this far out, so it cannot spin. */
const MIN_REARM_MS = 1_000;

/** Milliseconds from `now` to the next LOCAL midnight (a 23- or 25-hour day across a DST change). */
export function msUntilNextDay(now: Date): number {
  return new Date(now.getFullYear(), now.getMonth(), now.getDate() + 1).getTime() - now.getTime();
}

export class TodayClock {
  /** The local calendar day, "YYYY-MM-DD". Reactive: it changes when the day does. */
  value = $state(todayStr());

  #timer: ReturnType<typeof setTimeout> | null = null;
  #holding: () => boolean = () => false;
  #linkDay = this.value;

  /**
   * The day a bare `/meal-plan` shows. It is `value`, except that it holds still while the `hold`
   * predicate is true, and catches up on the first read after the predicate turns false. The owner
   * holds it while anything open belongs to the week on screen: a sheet, a dialog, the picker or a
   * drag. Otherwise midnight could move the week out from under it. fca#175, design note
   * fca-175-rollover-design-2026-11-20.
   *
   * A getter, not a `$derived`: it reads `value` and the predicate's state, so a `$derived` that calls
   * it tracks both. The plain field only remembers the last day shown while nothing held it.
   */
  get linkDay(): string {
    if (!this.#holding()) this.#linkDay = this.value;
    return this.#linkDay;
  }

  /** Hold `linkDay` while `holding()` is true. Set once, before the first read. */
  hold(holding: () => boolean): void {
    this.#holding = holding;
  }

  /** Re-read the clock now. A no-op while the day is unchanged. */
  sync(): void {
    const now = todayStr();
    if (now !== this.value) this.value = now;
  }

  /** Follow the clock: re-read at every local midnight. Returns the stop function. */
  start(): () => void {
    this.sync();
    this.#arm();
    return () => this.#disarm();
  }

  #arm(): void {
    this.#disarm();
    this.#timer = setTimeout(
      () => {
        this.#timer = null;
        this.sync();
        this.#arm();
      },
      Math.max(msUntilNextDay(new Date()), MIN_REARM_MS),
    );
  }

  #disarm(): void {
    if (this.#timer != null) {
      clearTimeout(this.#timer);
      this.#timer = null;
    }
  }
}
