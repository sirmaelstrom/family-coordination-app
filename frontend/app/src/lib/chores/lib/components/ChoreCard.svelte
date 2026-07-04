<script lang="ts">
  import type { ChoreDto, ChoreSubtaskDto, ColorTier, DueState, EffortTier, RecurrenceMode, RosterState } from '../types';
  import { boardStore, memberFor, roomFor } from '../state.svelte';
  import { showPhoto } from '../lightbox.svelte';
  import MemberAvatar from '$lib/shared/MemberAvatar.svelte';
  import { dndzone, type DndEvent } from 'svelte-dnd-action';

  // ───────────────────────────────────────────────────────────────────────
  // The shared chore card — used across every lens.
  //
  // ⚠ M5/M6: this card renders SERVER-computed state. `colorTier`/`dueState`
  // come straight off the DTO and drive the visual; we NEVER recompute dueness
  // or decay client-side and NEVER construct a Date from a bare 'YYYY-MM-DD'.
  // The only Date use is locale formatting of a full ISO-8601 UTC timestamp for
  // DISPLAY (nextDueAt / lastCompletedAt), which is safe.
  //
  // The claim / drop / hand-off / Done affordances are wired here (WP-11). Each
  // calls a handler prop; the parent runs the optimistic store mutation + 409
  // reconciliation. The card disables its controls while a mutation is in flight
  // (`pending`) to prevent double-submit. The action elements keep their
  // data-action tags for a clear seam.
  // ───────────────────────────────────────────────────────────────────────

  interface Props {
    chore: ChoreDto;
    /** The viewing user's id, to label "you" on owner/assignee. */
    currentUserId: number;
    /** True while a mutation for this chore is in flight (disables controls). */
    pending?: boolean;
    /**
     * Show the room locator chip (which room this chore lives in). True for the
     * cross-room lenses (Needs-attention / Up-for-grabs / Mine); the Rooms lens
     * passes false since its cards are already grouped under a room header.
     */
    showRoom?: boolean;
    onClaim?: (chore: ChoreDto) => void;
    onDrop?: (chore: ChoreDto) => void;
    onComplete?: (chore: ChoreDto) => void;
    onHandOff?: (chore: ChoreDto) => void;
    /** Take a chore held by someone else — assign it to the current user (one tap). */
    onTake?: (chore: ChoreDto) => void;
    /** Assign an up-for-grabs chore to a chosen member (opens the member picker). */
    onAssign?: (chore: ChoreDto) => void;
    /** Commit ("I'm in") on a multi-person chore's roster. */
    onCommit?: (chore: ChoreDto) => void;
    /** Leave ("Not me") a multi-person chore's roster. */
    onLeave?: (chore: ChoreDto) => void;
    /** Open the edit sheet for this chore. */
    onEdit?: (chore: ChoreDto) => void;
    /** Snooze / un-snooze this chore. `request.days` = N days from today; `request.until` = explicit
     *  "YYYY-MM-DD"; both omitted ⇒ un-snooze. The server resolves the floor (MN4 — no client date math). */
    onSnooze?: (chore: ChoreDto, request: { days?: number; until?: string | null }) => void;
  }

  let {
    chore,
    currentUserId,
    pending = false,
    showRoom = true,
    onClaim,
    onDrop,
    onComplete,
    onHandOff,
    onTake,
    onAssign,
    onCommit,
    onLeave,
    onEdit,
    onSnooze,
  }: Props = $props();

  // ── Named effort tiers (P3 — never the raw points number as primary) ─────
  const EFFORT_LABEL: Record<EffortTier, string> = {
    Quick: 'Quick',
    Standard: 'Standard',
    BigJob: 'Big job',
  };

  // ── Dueness pill copy (server `dueState`) ────────────────────────────────
  const DUE_LABEL: Record<DueState, string> = {
    overdue: 'Overdue',
    dueToday: 'Due today',
    scheduled: 'Scheduled',
    notDue: 'On track',
  };

  let effortLabel = $derived(EFFORT_LABEL[chore.effortTier]);
  let dueLabel = $derived(DUE_LABEL[chore.dueState]);

  // colorTier (fresh|mid|due|overdue) is the decay accent — server-computed.
  let tier = $derived<ColorTier>(chore.colorTier);

  let owner = $derived(memberFor(chore.ownerUserId));
  let assignee = $derived(memberFor(chore.assigneeUserId));

  // ── Room locator (which room this chore lives in) ────────────────────────
  // Resolved off the board's room rollups. null for roomless chores (General)
  // or when `showRoom` is off (the Rooms lens, where cards are already grouped).
  let room = $derived(showRoom ? roomFor(chore.roomId) : null);

  let isUnclaimed = $derived(chore.assignmentKind === 'none' || chore.isClaimStale);
  let isClaimed = $derived(chore.assignmentKind === 'claimed' && !chore.isClaimStale);
  let isAssigned = $derived(chore.assignmentKind === 'assigned');

  // ── Multi-person named roster (rework) ───────────────────────────────────
  // SERVER fields only (MN3 — no client count/membership math). `roster` carries
  // each member's derived state (assigned/in/done); `completedCount`/`requiredCount`
  // are the authoritative "k of X" gate (fresh — the store reconciles via the board
  // GET after each mutation). The viewer's OWN roster state drives which actions show.
  let isMultiPerson = $derived(chore.requiredCount > 1);
  let myRosterState = $derived<RosterState | null>(
    chore.roster.find((m) => m.userId === currentUserId)?.state ?? null,
  );
  let iAmDone = $derived(myRosterState === 'done');
  let onRoster = $derived(myRosterState !== null);
  let rosterMembers = $derived(
    chore.roster.map((m) => ({ userId: m.userId, state: m.state, member: memberFor(m.userId) })),
  );

  const ROSTER_LABEL: Record<RosterState, string> = {
    assigned: 'Assigned',
    in: "I'm in",
    done: 'Done',
  };
  const ROSTER_GLYPH: Record<RosterState, string> = {
    assigned: '○',
    in: '●',
    done: '✓',
  };

  // ── Who can do what (drives the action buttons) ──────────────────────────
  // The viewing user "holds" the chore when they're the active (non-stale)
  // assignee. Drop is Claimed-only (a deliberate Assigned chore can't be
  // dropped — WP-04). The holder can hand off; ANYONE can take a chore held by
  // someone else or reassign it — the service has no holder guard on hand-off
  // ("anyone can reassign, no roles", ChoreService.HandOffAsync). Done is
  // available on any non-pile chore (any member may complete — WP-04 M8).
  let heldByMe = $derived(
    chore.assigneeUserId === currentUserId &&
      (isClaimed || isAssigned),
  );
  // Held (fresh, non-stale) by another member — surfaces Take it / Reassign so
  // you can grab or pass a chore without coordinating with the current holder.
  let heldByOther = $derived(!isUnclaimed && !heldByMe);
  let canDrop = $derived(heldByMe && isClaimed);

  // ── Recurrence hint (human, derived from the plain-string union) ─────────
  const RECURRENCE_HINT: Record<RecurrenceMode, string> = {
    OneOff: 'One-off',
    Fixed: 'Scheduled',
    Flexible: 'Recurring',
  };
  let recurrenceHint = $derived(RECURRENCE_HINT[chore.recurrenceMode]);

  // ── Display-only date formatting (safe: full ISO-8601 UTC strings) ───────
  // NEVER `new Date('YYYY-MM-DD')`. nextDueAt / lastCompletedAt are full
  // ISO-8601 timestamps with a Z suffix, so `new Date(iso)` is locale-safe.
  function formatDay(iso: string | null): string | null {
    if (!iso) return null;
    const d = new Date(iso);
    if (Number.isNaN(d.getTime())) return null;
    return d.toLocaleDateString(undefined, { month: 'short', day: 'numeric' });
  }

  let nextDueLabel = $derived(formatDay(chore.nextDueAt));
  let lastDoneLabel = $derived(formatDay(chore.lastCompletedAt));

  function nameOf(userId: number | null, displayName: string | null): string {
    if (userId != null && userId === currentUserId) return 'You';
    return displayName ?? 'Someone';
  }

  // ── Checklist / subtasks (Phase 14 — momentum aid, NEVER gates completion) ─
  // Rendered only when the chore already has at least one item; the FIRST item
  // is added from the edit sheet. All ops go straight to the versionless store
  // path (boardStore.{toggle,remove,add}Subtask) — last-write-wins, no version,
  // and they never disable the chore's own controls. No dueness/Date here.
  let subtasks = $derived(chore.subtasks);
  let hasChecklist = $derived(subtasks.length > 0);
  let doneCount = $derived(subtasks.filter((s) => s.isDone).length);
  let totalCount = $derived(subtasks.length);

  /** Local collapse state — the checklist starts collapsed; the chip toggles it. */
  let expanded = $state(false);
  /** Inline "add item" draft for the expanded checklist. */
  let newItemTitle = $state('');

  function submitNewItem() {
    const title = newItemTitle.trim();
    if (!title) return;
    boardStore.addSubtask(chore.id, title);
    newItemTitle = '';
  }

  // ── Drag-reorder (svelte-dnd-action — the room-manager house pattern) ──────
  // A local working list re-synced from the chore's subtasks except while a drag
  // is in flight (a guard so a mid-drag board reload can't clobber the order). On
  // drop we persist the new order via the versionless bulk reorder store path.
  let checkRows = $state<ChoreSubtaskDto[]>([]);
  let draggingChecklist = $state(false);
  $effect(() => {
    if (!draggingChecklist) checkRows = [...subtasks];
  });

  function handleChecklistConsider(e: CustomEvent<DndEvent<ChoreSubtaskDto>>) {
    draggingChecklist = true;
    checkRows = e.detail.items;
  }
  async function handleChecklistFinalize(e: CustomEvent<DndEvent<ChoreSubtaskDto>>) {
    checkRows = e.detail.items;
    const ordered = checkRows.map((s) => s.id);
    try {
      await boardStore.reorderSubtasks(chore.id, ordered);
    } finally {
      // Release the guard last so the resync lands on the persisted order.
      draggingChecklist = false;
    }
  }

  /** "Who ticked it" label for a done item — the member's initials (tooltip = name + when). */
  function actorLabel(s: ChoreSubtaskDto): { initials: string; tooltip: string } | null {
    if (!s.isDone || s.completedByUserId == null) return null;
    const member = memberFor(s.completedByUserId);
    if (!member) return null;
    const when = formatDay(s.completedAt);
    const you = s.completedByUserId === currentUserId;
    const name = you ? 'you' : member.displayName;
    return { initials: member.initials, tooltip: when ? `Done by ${name} · ${when}` : `Done by ${name}` };
  }

  // ── Snooze / set-next-due (board quick-snooze) ───────────────────────────
  // The "Snooze" button reveals a small preset row; a preset or a picked date
  // calls onSnooze with the RAW request — the server resolves the floor date in
  // the household timezone (MN4 — NO client date math, no min/default computed
  // here). The chip (when isSnoozed) binds the server `nextDueAt` (the
  // schedule-aware RESUME date), never snoozedUntil.
  let snoozeOpen = $state(false);

  function doSnooze(request: { days?: number; until?: string | null }) {
    onSnooze?.(chore, request);
    snoozeOpen = false;
  }

  function onPickDate(e: Event) {
    const input = e.currentTarget as HTMLInputElement;
    const value = input.value; // raw "YYYY-MM-DD" — sent unchanged; server validates > today
    if (value) doSnooze({ until: value });
  }
</script>

<article
  class="ch-card ch-tier-{tier}"
  class:ch-card-stale={chore.isClaimStale}
  class:ch-card-pending={pending}
  aria-label={chore.name}
  aria-busy={pending}
>
  <div class="ch-card-accent" aria-hidden="true"></div>

  {#if chore.photoPath}
    <!--
      Leading photo thumbnail (v1.2). Same-origin served URL (/uploads/…), so
      `src` works directly. Tap → shared lightbox. Fixed 56px square keeps row
      height steady regardless of photo orientation; the icon emoji still leads
      the name. No-photo chores render no thumbnail (unchanged from before).
    -->
    <button
      type="button"
      class="ch-card-thumb-btn"
      data-action="photo"
      onclick={() => showPhoto(chore.photoPath, chore.name)}
      aria-label="View photo for {chore.name}"
      title="View photo"
    >
      <img
        class="ch-card-thumb"
        src={chore.photoPath}
        alt=""
        width="56"
        height="56"
        loading="lazy"
      />
    </button>
  {/if}

  <div class="ch-card-main">
    <div class="ch-card-top">
      <h3 class="ch-card-name">
        {#if chore.icon}<span class="ch-card-icon" aria-hidden="true">{chore.icon}</span>{/if}{chore.name}
      </h3>
      <span class="ch-pill ch-pill-{tier}" title="Due state: {dueLabel}">{dueLabel}</span>
    </div>

    {#if chore.description}
      <p class="ch-card-desc">{chore.description}</p>
    {/if}

    <div class="ch-card-meta">
      {#if hasChecklist}
        <!--
          Checklist progress chip — a tappable button that expands/collapses the
          per-chore checklist below. Momentum surface only; never gates the
          Complete button. ✓ done/total.
        -->
        <button
          type="button"
          class="ch-tag ch-tag-checklist"
          class:ch-tag-checklist-done={doneCount === totalCount}
          data-action="toggle-checklist"
          aria-expanded={expanded}
          onclick={() => (expanded = !expanded)}
          title="Checklist: {doneCount} of {totalCount} done — tap to {expanded ? 'hide' : 'show'}"
        >
          <span aria-hidden="true">✓</span>
          {doneCount}/{totalCount}
        </button>
      {/if}
      {#if isMultiPerson}
        <span
          class="ch-tag ch-tag-cosign"
          title="{chore.completedCount} of {chore.requiredCount} people have marked this done"
        >
          <svg viewBox="0 0 24 24" width="14" height="14" aria-hidden="true">
            <path
              d="M16 11c1.66 0 2.99-1.34 2.99-3S17.66 5 16 5s-3 1.34-3 3 1.34 3 3 3zm-8 0c1.66 0 2.99-1.34 2.99-3S9.66 5 8 5 5 6.34 5 8s1.34 3 3 3zm0 2c-2.33 0-7 1.17-7 3.5V19h14v-2.5c0-2.33-4.67-3.5-7-3.5zm8 0c-.29 0-.62.02-.97.05 1.16.84 1.97 1.97 1.97 3.45V19h6v-2.5c0-2.33-4.67-3.5-7-3.5z"
              fill="currentColor"
            />
          </svg>
          {chore.completedCount} of {chore.requiredCount} done
        </span>
      {/if}
      {#if room}
        <span class="ch-tag ch-tag-room" title="Room: {room.name}">
          {#if room.icon}
            <span class="ch-tag-room-icon" aria-hidden="true">{room.icon}</span>
          {:else}
            <svg viewBox="0 0 24 24" width="14" height="14" aria-hidden="true">
              <path
                d="M12 2C8.13 2 5 5.13 5 9c0 5.25 7 13 7 13s7-7.75 7-13c0-3.87-3.13-7-7-7zm0 9.5A2.5 2.5 0 1 1 12 6.5a2.5 2.5 0 0 1 0 5z"
                fill="currentColor"
              />
            </svg>
          {/if}
          {room.name}
        </span>
      {/if}

      <span class="ch-tag ch-tag-effort" title="Effort: {effortLabel}">
        <svg viewBox="0 0 24 24" width="14" height="14" aria-hidden="true">
          <path d="M13 2 3 14h7l-1 8 10-12h-7l1-8z" fill="currentColor" />
        </svg>
        {effortLabel}
      </span>

      <span class="ch-tag ch-tag-recurrence" title="{recurrenceHint}">
        <svg viewBox="0 0 24 24" width="14" height="14" aria-hidden="true">
          <path d="M12 6V3L8 7l4 4V8a4 4 0 1 1-4 4H6a6 6 0 1 0 6-6z" fill="currentColor" />
        </svg>
        {recurrenceHint}
      </span>

      {#if chore.isSnoozed}
        <!--
          Snoozed chip — the date binds to the server `nextDueAt` (the schedule-aware
          RESUME date), NOT snoozedUntil: for a Fixed chore the two differ (the floor
          skips a slot; the resume lands on the next cadence day). Server value only (MN4).
        -->
        <span class="ch-tag ch-tag-snoozed" title="Snoozed — resumes {nextDueLabel ?? 'on its next day'}">
          💤 Snoozed{#if nextDueLabel} · due {nextDueLabel}{/if}
        </span>
      {:else if nextDueLabel}
        <span class="ch-tag ch-tag-due" title="Next due {nextDueLabel}">
          Next: {nextDueLabel}
        </span>
      {:else if lastDoneLabel}
        <span class="ch-tag ch-tag-done" title="Last done {lastDoneLabel}">
          Done {lastDoneLabel}
        </span>
      {/if}

      <!--
        Minder chip (the chore's ultimate owner — "makes sure it gets done").
        Deliberately lives UP HERE among the chore's attribute chips, NOT down in
        the footer next to the doer — the minder is a property of the chore, the
        doer is the live work state. Rendering it as an eye-icon chip (no avatar)
        keeps it visually distinct from the footer's avatar-led doer, so the two
        roles never read as the same thing.
      -->
      {#if owner}
        <span
          class="ch-tag ch-tag-minder"
          title="Minder — makes sure this gets done: {nameOf(chore.ownerUserId, owner.displayName)}"
        >
          <svg viewBox="0 0 24 24" width="14" height="14" aria-hidden="true">
            <path
              d="M12 5c-5 0-9.27 3.11-11 7 1.73 3.89 6 7 11 7s9.27-3.11 11-7c-1.73-3.89-6-7-11-7zm0 12a5 5 0 1 1 0-10 5 5 0 0 1 0 10zm0-8a3 3 0 1 0 0 6 3 3 0 0 0 0-6z"
              fill="currentColor"
            />
          </svg>
          <span class="ch-tag-minder-role">Minder</span>
          {nameOf(chore.ownerUserId, owner.displayName)}
        </span>
      {/if}
    </div>

    {#if hasChecklist && expanded}
      <!--
        Per-chore checklist (Phase 14). Each row is a tappable checkbox + title;
        tapping toggles via the versionless store path. A subtle "×" removes the
        item, and a done item shows "who ticked it". Rows drag to reorder
        (svelte-dnd-action; persisted via the bulk reorder path). The add row sits
        OUTSIDE the drag zone (every dndzone child is a draggable item). Subtasks
        are a momentum aid only — they never gate the Done/Complete button.
      -->
      <ul
        class="ch-checklist"
        aria-label="Checklist for {chore.name}"
        use:dndzone={{
          items: checkRows,
          type: `subtasks-${chore.id}`,
          flipDurationMs: 150,
          dropTargetStyle: {},
          delayTouchStart: 250,
        }}
        onconsider={handleChecklistConsider}
        onfinalize={handleChecklistFinalize}
      >
        {#each checkRows as s (s.id)}
          {@const actor = actorLabel(s)}
          <li class="ch-checkitem" class:ch-checkitem-done={s.isDone}>
            <button
              type="button"
              class="ch-check-toggle"
              data-action="subtask-toggle"
              aria-pressed={s.isDone}
              onclick={() => boardStore.toggleSubtask(chore.id, s.id, !s.isDone)}
              title={s.isDone ? 'Mark not done' : 'Mark done'}
            >
              <span class="ch-check-box" aria-hidden="true">{s.isDone ? '✓' : ''}</span>
              <span class="ch-check-title">{s.title}</span>
            </button>
            {#if actor}
              <span class="ch-check-actor" title={actor.tooltip}>{actor.initials}</span>
            {/if}
            <button
              type="button"
              class="ch-check-del"
              data-action="subtask-remove"
              onclick={() => boardStore.removeSubtask(chore.id, s.id)}
              title="Remove this item"
              aria-label="Remove {s.title}"
            >
              ×
            </button>
          </li>
        {/each}
      </ul>
      <div class="ch-checkadd">
        <input
          type="text"
          class="ch-checkadd-input"
          bind:value={newItemTitle}
          autocomplete="off"
          placeholder="Add an item…"
          aria-label="Add a checklist item"
          onkeydown={(e) => {
            if (e.key === 'Enter') {
              e.preventDefault();
              submitNewItem();
            }
          }}
        />
        <button
          type="button"
          class="ch-checkadd-btn"
          data-action="subtask-add"
          onclick={submitNewItem}
          disabled={!newItemTitle.trim()}
          title="Add item"
          aria-label="Add checklist item"
        >
          ＋
        </button>
      </div>
    {/if}

    <div class="ch-card-foot">
      <!--
        The footer people row is the DOER only — who's actively on it (roster /
        claimed / assigned) or "Up for grabs". The minder moved up to the chip row
        above so the two roles are visually separated (operator feedback).
      -->
      <div class="ch-people">
        {#if isMultiPerson}
          <div class="ch-roster" aria-label="Roster: {chore.completedCount} of {chore.requiredCount} done">
            {#each rosterMembers as r (r.userId)}
              {#if r.member}
                <span
                  class="ch-roster-member ch-roster-{r.state}"
                  title="{nameOf(r.userId, r.member.displayName)} — {ROSTER_LABEL[r.state]}"
                >
                  <MemberAvatar
                    name={r.member.displayName}
                    initials={r.member.initials}
                    pictureUrl={r.member.pictureUrl}
                    size={26}
                    relation={ROSTER_LABEL[r.state]}
                  />
                  <span class="ch-roster-badge" aria-hidden="true">{ROSTER_GLYPH[r.state]}</span>
                </span>
              {/if}
            {/each}
            {#if rosterMembers.length === 0}
              <span class="ch-claim ch-claim-open">Needs {chore.requiredCount} — no one yet</span>
            {/if}
          </div>
        {:else if isClaimed && assignee}
          <span class="ch-claim ch-claim-claimed">
            <MemberAvatar
              name={assignee.displayName}
              initials={assignee.initials}
              pictureUrl={assignee.pictureUrl}
              size={28}
              relation="Claimed by"
            />
            <span class="ch-claim-label">{nameOf(chore.assigneeUserId, assignee.displayName)}</span>
          </span>
        {:else if isAssigned && assignee}
          <span class="ch-claim ch-claim-assigned">
            <MemberAvatar
              name={assignee.displayName}
              initials={assignee.initials}
              pictureUrl={assignee.pictureUrl}
              size={28}
              relation="Assigned to"
            />
            <span class="ch-claim-label">{nameOf(chore.assigneeUserId, assignee.displayName)}</span>
          </span>
        {:else}
          <span class="ch-claim ch-claim-open">Up for grabs</span>
        {/if}
      </div>

      <!--
        Action affordances (WP-11). Optimistic — the parent runs the store
        mutation + 409 reconcile. Controls disable while a mutation is in flight
        (`pending`) to prevent double-submit.
      -->
      <div class="ch-actions">
        {#if isMultiPerson}
          <!--
            Multi-person chores are NOT claimable. Actions depend on the viewer's
            roster state: done → waiting; on-roster → mark-done / not-me; assigned
            or not-on-roster → I'm-in/I'll-help to join, plus mark-done (anyone may
            complete toward the count). The store routes the chore to Mine (on
            roster) or Up-for-grabs (not) — see state.svelte.ts.
          -->
          {#if iAmDone}
            <button
              type="button"
              class="ch-btn ch-btn-ghost"
              data-action="complete"
              disabled
              title="You've done your part — waiting on the others"
            >
              You're done ✓
            </button>
          {:else}
            {#if !onRoster || myRosterState === 'assigned'}
              <button
                type="button"
                class="ch-btn ch-btn-ghost"
                data-action="commit"
                onclick={() => onCommit?.(chore)}
                disabled={pending}
                title={onRoster ? "Confirm you're in" : "Join in — I'll help"}
              >
                {onRoster ? "I'm in" : "I'll help"}
              </button>
            {/if}
            <button
              type="button"
              class="ch-btn ch-btn-primary"
              data-action="complete"
              onclick={() => onComplete?.(chore)}
              disabled={pending}
              title="Mark your part done"
            >
              Mark my part done
            </button>
            {#if onRoster}
              <button
                type="button"
                class="ch-btn ch-btn-ghost"
                data-action="leave"
                onclick={() => onLeave?.(chore)}
                disabled={pending}
                title="I can't do this one — take me off"
              >
                Not me
              </button>
            {/if}
          {/if}
        {:else if isUnclaimed}
          <button
            type="button"
            class="ch-btn ch-btn-primary"
            data-action="claim"
            onclick={() => onClaim?.(chore)}
            disabled={pending}
            title="Claim this chore"
          >
            Claim
          </button>
          {#if onAssign}
            <!--
              Assign an up-for-grabs chore to a specific person (opens the member
              picker). Lands a deliberate Assigned via the hand-off endpoint — the
              same mechanism "Reassign" uses on held chores, just surfaced from the
              pile. Distinct from Claim (self) and Take (grab someone else's).
            -->
            <button
              type="button"
              class="ch-btn ch-btn-ghost"
              data-action="assign"
              onclick={() => onAssign?.(chore)}
              disabled={pending}
              title="Assign this chore to someone"
            >
              Assign
            </button>
          {/if}
        {:else}
          {#if heldByMe}
            <button
              type="button"
              class="ch-btn ch-btn-ghost"
              data-action="handoff"
              onclick={() => onHandOff?.(chore)}
              disabled={pending}
              title="Hand off or release this chore"
            >
              Hand off
            </button>
            {#if canDrop}
              <button
                type="button"
                class="ch-btn ch-btn-ghost"
                data-action="drop"
                onclick={() => onDrop?.(chore)}
                disabled={pending}
                title="Put this chore back in the pile"
              >
                Drop
              </button>
            {/if}
          {:else if heldByOther}
            <!--
              Held by someone else. Take it (claim it for yourself in one tap —
              covering for someone who's out/sick, no need to coordinate) or
              Reassign it to another member / release it via the shared hand-off
              picker. Take lands a self-CLAIM (not a sticky assignment), so a
              recurring chore returns to the pile after you finish it; completion
              already credits the completer, so Take-then-Done attributes the
              work to you.
            -->
            <button
              type="button"
              class="ch-btn ch-btn-ghost"
              data-action="take"
              onclick={() => onTake?.(chore)}
              disabled={pending}
              title="Take this chore — claim it for yourself"
            >
              Take it
            </button>
            <button
              type="button"
              class="ch-btn ch-btn-ghost"
              data-action="handoff"
              onclick={() => onHandOff?.(chore)}
              disabled={pending}
              title="Reassign this chore to someone else or release it"
            >
              Reassign
            </button>
          {/if}
          <!-- Single-person chore — today's exact one-tap Done (unchanged, P2). -->
          <button
            type="button"
            class="ch-btn ch-btn-primary"
            data-action="complete"
            onclick={() => onComplete?.(chore)}
            disabled={pending}
            title="Mark this chore done"
          >
            Done
          </button>
        {/if}
        {#if onSnooze}
          <!--
            Snooze / un-snooze — available on any chore regardless of claim state (a
            chore-level "not today" lever). Snooze opens an inline preset row below;
            Un-snooze clears the floor in one tap. Server resolves the date (MN4).
          -->
          {#if chore.isSnoozed}
            <button
              type="button"
              class="ch-btn ch-btn-ghost"
              data-action="unsnooze"
              onclick={() => doSnooze({ until: null })}
              disabled={pending}
              title="Un-snooze — make this active again now"
            >
              Un-snooze
            </button>
          {:else}
            <button
              type="button"
              class="ch-btn ch-btn-ghost"
              data-action="snooze"
              onclick={() => (snoozeOpen = !snoozeOpen)}
              disabled={pending}
              aria-expanded={snoozeOpen}
              title="Snooze — not today"
            >
              Snooze
            </button>
          {/if}
        {/if}
        {#if onEdit}
          <button
            type="button"
            class="ch-btn ch-btn-icon"
            data-action="edit"
            onclick={() => onEdit?.(chore)}
            disabled={pending}
            title="Edit this chore"
            aria-label="Edit {chore.name}"
          >
            <svg viewBox="0 0 24 24" width="16" height="16" aria-hidden="true">
              <path
                d="M3 17.25V21h3.75L17.81 9.94l-3.75-3.75L3 17.25zM20.71 7.04a1 1 0 0 0 0-1.41l-2.34-2.34a1 1 0 0 0-1.41 0l-1.83 1.83 3.75 3.75 1.83-1.83z"
                fill="currentColor"
              />
            </svg>
          </button>
        {/if}
      </div>
    </div>

    {#if onSnooze && snoozeOpen && !chore.isSnoozed}
      <!--
        Snooze preset row. Presets send {days} (server adds them to today in the
        household tz); "Pick a date" sends the RAW <input type="date"> value as
        {until} unchanged and relies on server validation for > today — no client
        min/default (MN4). The chip/date everywhere come from the server, never math.
      -->
      <div class="ch-snooze-menu" role="group" aria-label="Snooze until">
        <button type="button" class="ch-snooze-preset" onclick={() => doSnooze({ days: 1 })} disabled={pending}>
          Tomorrow
        </button>
        <button type="button" class="ch-snooze-preset" onclick={() => doSnooze({ days: 3 })} disabled={pending}>
          3 days
        </button>
        <button type="button" class="ch-snooze-preset" onclick={() => doSnooze({ days: 7 })} disabled={pending}>
          1 week
        </button>
        <label class="ch-snooze-pick">
          <span>Pick a date</span>
          <input type="date" onchange={onPickDate} disabled={pending} aria-label="Snooze until a date" />
        </label>
      </div>
    {/if}
  </div>
</article>

<style>
  /*
   * Decay accent is driven entirely by the server `colorTier`
   * (fresh|mid|due|overdue). We map each tier to a token-based accent color
   * via a per-tier `--accent` custom property; the card never derives the tier.
   */
  .ch-card {
    --accent: var(--color-line-strong);
    position: relative;
    display: flex;
    background: var(--color-surface);
    border-radius: var(--radius-md);
    box-shadow: var(--shadow-1);
    overflow: hidden;
    transition: box-shadow 0.15s;
  }
  .ch-card:hover {
    box-shadow: var(--shadow-2);
  }
  .ch-card-stale {
    /* A stale claim reads as effectively up-for-grabs (WP-04/05 UX). */
    opacity: 0.92;
  }
  .ch-card-pending {
    /* A mutation is in flight — dim the card while we await the server. */
    opacity: 0.6;
  }

  /* ── colorTier → accent (the ONLY place the tier maps to a color) ──────── */
  .ch-tier-fresh {
    --accent: var(--color-success);
  }
  .ch-tier-mid {
    --accent: var(--color-info);
  }
  .ch-tier-due {
    --accent: var(--color-warning);
  }
  .ch-tier-overdue {
    --accent: var(--color-error);
  }

  .ch-card-accent {
    flex-shrink: 0;
    width: 6px;
    background: var(--accent);
  }

  /* ── Leading photo thumbnail (v1.2) — tap to enlarge ───────────────────── */
  .ch-card-thumb-btn {
    flex-shrink: 0;
    align-self: flex-start;
    margin: 12px 0 12px 12px;
    padding: 0;
    border: none;
    background: transparent;
    border-radius: 8px;
    cursor: zoom-in;
    line-height: 0;
    -webkit-tap-highlight-color: transparent;
    touch-action: manipulation;
  }
  .ch-card-thumb {
    display: block;
    width: 56px;
    height: 56px;
    border-radius: 8px;
    object-fit: cover;
    background: var(--color-action-hover);
  }
  .ch-card-thumb-btn:hover .ch-card-thumb {
    filter: brightness(1.04);
  }
  .ch-card-thumb-btn:focus-visible {
    outline: 2px solid var(--color-primary);
    outline-offset: 2px;
  }

  .ch-card-main {
    flex: 1;
    min-width: 0;
    padding: 12px 16px;
    display: flex;
    flex-direction: column;
    gap: 8px;
    /* Query container so the footer can lay itself out against the CARD's own
       width (not the viewport) — a card is narrow on mobile and wide on desktop
       regardless of screen size. Drives the @container rule on .ch-card-foot. */
    container-type: inline-size;
  }

  .ch-card-top {
    display: flex;
    align-items: flex-start;
    justify-content: space-between;
    gap: 12px;
  }
  .ch-card-name {
    margin: 0;
    font-size: 1.0625rem;
    font-weight: 500;
    line-height: 1.3;
    color: var(--color-text);
    overflow-wrap: anywhere;
  }
  /* Optional chore icon leads the name (parity with room cards). */
  .ch-card-icon {
    margin-right: 6px;
  }

  /* ── Dueness pill — tinted by the same colorTier accent ────────────────── */
  .ch-pill {
    flex-shrink: 0;
    display: inline-flex;
    align-items: center;
    height: 22px;
    padding: 0 10px;
    border-radius: 11px;
    font-size: 0.6875rem;
    font-weight: 600;
    letter-spacing: 0.02em;
    text-transform: uppercase;
    white-space: nowrap;
    color: #fff;
    background: var(--accent);
  }
  /* Fresh/mid pills sit quieter — outline rather than solid. */
  .ch-pill-fresh,
  .ch-pill-mid {
    color: var(--accent);
    background: transparent;
    border: 1px solid var(--accent);
  }

  .ch-card-desc {
    margin: 0;
    font-size: 0.875rem;
    color: var(--color-text-muted);
    line-height: 1.4;
    overflow: hidden;
    display: -webkit-box;
    -webkit-line-clamp: 2;
    line-clamp: 2;
    -webkit-box-orient: vertical;
  }

  .ch-card-meta {
    display: flex;
    flex-wrap: wrap;
    gap: 6px;
  }
  .ch-tag {
    display: inline-flex;
    align-items: center;
    gap: 4px;
    height: 22px;
    padding: 0 8px;
    border-radius: var(--radius-sm);
    font-size: 0.75rem;
    font-weight: 500;
    color: var(--color-text-muted);
    background: var(--color-action-hover);
    white-space: nowrap;
  }
  .ch-tag svg {
    flex-shrink: 0;
  }
  /* Room locator chip — tinted distinct from the muted effort/recurrence tags
     so "which room" reads as the card's primary orientation cue. */
  .ch-tag-room {
    color: #fff;
    background: var(--color-primary-soft);
  }
  .ch-tag-room-icon {
    font-size: 0.875rem;
    line-height: 1;
  }
  /* Co-sign progress chip ("X of N done") — solid tint with white text so it
     reads as a status cue distinct from the muted effort/recurrence tags. The
     accent color carries the meaning; the same fill the room locator uses. */
  .ch-tag-cosign {
    color: #fff;
    background: var(--color-info);
    font-weight: 600;
  }
  /* Minder chip — the chore's ultimate owner. A quiet, bordered "accountability"
     cue (eye icon + uppercase role + name) that reads distinctly from both the
     muted attribute chips and the footer's avatar-led doer. */
  .ch-tag-minder {
    color: var(--color-text);
    background: transparent;
    border: 1px solid var(--color-line-strong);
    font-weight: 500;
  }
  .ch-tag-minder svg {
    color: var(--color-primary);
  }
  .ch-tag-minder-role {
    text-transform: uppercase;
    font-size: 0.625rem;
    font-weight: 600;
    letter-spacing: 0.04em;
    color: var(--color-text-muted);
  }
  /* Checklist progress chip — a tappable button styled like the other meta tags.
     A subtle bordered pill so it reads as interactive without shouting; goes
     green-tinted once every item is checked. Never gates completion. */
  .ch-tag-checklist {
    font: inherit;
    font-size: 0.75rem;
    font-weight: 600;
    color: var(--color-text-muted);
    border: 1px solid var(--color-line-strong);
    cursor: pointer;
    -webkit-tap-highlight-color: transparent;
    touch-action: manipulation;
  }
  .ch-tag-checklist:hover {
    background: var(--color-action-hover);
    color: var(--color-text);
  }
  .ch-tag-checklist-done {
    color: var(--color-success);
    border-color: var(--color-success);
  }

  /*
   * Footer layout — STACKED by default (mobile-first). The doer strip drops to
   * its own row at the BOTTOM via column-reverse (DOM order stays people→actions,
   * which keeps a sane tab order — the doer strip has no focusable elements). This
   * fixes the mobile overlap where a long assignee name ran under the "Take it"
   * button (the actions were flex-shrink:0 and the name had no room). The wide-card
   * @container rule below puts them back side-by-side when there's space.
   */
  .ch-card-foot {
    display: flex;
    flex-direction: column-reverse;
    align-items: stretch;
    gap: 10px;
    margin-top: 2px;
  }
  .ch-people {
    display: flex;
    align-items: center;
    gap: 12px;
    flex-wrap: wrap;
    /* The doer's own bespoke space — a divider sets it apart from the actions. */
    padding-top: 8px;
    border-top: 1px solid var(--color-line);
  }
  .ch-claim {
    display: inline-flex;
    align-items: center;
    gap: 6px;
    font-size: 0.8125rem;
    color: var(--color-text-muted);
  }
  .ch-claim-label {
    font-weight: 500;
    color: var(--color-text);
  }
  .ch-claim-open {
    font-style: italic;
  }

  /* ── Multi-person roster strip (avatars badged assigned ○ / in ● / done ✓) ── */
  .ch-roster {
    display: flex;
    align-items: center;
    gap: 8px;
    flex-wrap: wrap;
  }
  .ch-roster-member {
    position: relative;
    display: inline-flex;
  }
  /* An assigned (not-yet-confirmed) member reads quieter than a committed one. */
  .ch-roster-assigned {
    opacity: 0.65;
  }
  .ch-roster-badge {
    position: absolute;
    right: -3px;
    bottom: -3px;
    width: 14px;
    height: 14px;
    border-radius: 50%;
    display: grid;
    place-items: center;
    font-size: 9px;
    line-height: 1;
    color: #fff;
    background: var(--color-text-muted);
    box-shadow: 0 0 0 2px var(--color-surface);
  }
  .ch-roster-in .ch-roster-badge {
    background: var(--color-info);
  }
  .ch-roster-done .ch-roster-badge {
    background: var(--color-success);
  }

  /* ── Per-chore checklist (Phase 14) ─────────────────────────────────────── */
  .ch-checklist {
    list-style: none;
    margin: 0;
    padding: 8px 0 0;
    display: flex;
    flex-direction: column;
    gap: 2px;
    border-top: 1px solid var(--color-line);
  }
  .ch-checkitem {
    display: flex;
    align-items: center;
    gap: 4px;
  }
  /* The tappable label region (checkbox + title) is the whole left side — a
     comfortable mobile target. */
  .ch-check-toggle {
    flex: 1;
    min-width: 0;
    display: flex;
    align-items: center;
    /* A flex <button> centers its content under the UA stylesheet — text-align is
       ignored for flex children — so pin the checkbox + title hard-left. */
    justify-content: flex-start;
    gap: 10px;
    font: inherit;
    font-size: 0.875rem;
    text-align: left;
    color: var(--color-text);
    background: transparent;
    border: none;
    border-radius: var(--radius-sm);
    padding: 8px 6px;
    min-height: 40px;
    cursor: pointer;
    -webkit-tap-highlight-color: transparent;
    touch-action: manipulation;
  }
  .ch-check-toggle:hover {
    background: var(--color-action-hover);
  }
  .ch-check-box {
    flex-shrink: 0;
    width: 20px;
    height: 20px;
    display: grid;
    place-items: center;
    border: 1.5px solid var(--color-line-strong);
    border-radius: 5px;
    font-size: 13px;
    line-height: 1;
    color: #fff;
  }
  .ch-checkitem-done .ch-check-box {
    background: var(--color-success);
    border-color: var(--color-success);
  }
  .ch-check-title {
    overflow-wrap: anywhere;
  }
  .ch-checkitem-done .ch-check-title {
    text-decoration: line-through;
    color: var(--color-text-muted);
  }
  .ch-check-del {
    flex-shrink: 0;
    width: 32px;
    height: 32px;
    display: grid;
    place-items: center;
    font-size: 1.125rem;
    line-height: 1;
    color: var(--color-text-muted);
    background: transparent;
    border: none;
    border-radius: var(--radius-sm);
    cursor: pointer;
    -webkit-tap-highlight-color: transparent;
    touch-action: manipulation;
  }
  .ch-check-del:hover {
    background: var(--color-action-hover);
    color: var(--color-error);
  }
  /* "Who ticked it" tag — the actor's initials beside a done item. */
  .ch-check-actor {
    flex-shrink: 0;
    display: inline-flex;
    align-items: center;
    justify-content: center;
    min-width: 22px;
    height: 22px;
    padding: 0 6px;
    font-size: 0.6875rem;
    font-weight: 600;
    line-height: 1;
    color: var(--color-text-muted);
    background: var(--color-action-hover);
    border-radius: 999px;
    cursor: default;
  }
  .ch-checkadd {
    display: flex;
    align-items: center;
    gap: 6px;
    padding-top: 4px;
  }
  .ch-checkadd-input {
    flex: 1;
    min-width: 0;
    font: inherit;
    font-size: 0.875rem;
    color: inherit;
    padding: 8px 10px;
    border: 1px solid var(--color-line-strong);
    border-radius: var(--radius-sm);
    background: var(--color-surface);
    min-height: 40px;
  }
  .ch-checkadd-input:focus {
    outline: 2px solid var(--color-primary);
    outline-offset: -1px;
    border-color: var(--color-primary);
  }
  .ch-checkadd-btn {
    flex-shrink: 0;
    width: 40px;
    height: 40px;
    display: grid;
    place-items: center;
    font-size: 1.125rem;
    color: var(--color-primary);
    background: transparent;
    border: 1px solid var(--color-line-strong);
    border-radius: var(--radius-sm);
    cursor: pointer;
    -webkit-tap-highlight-color: transparent;
    touch-action: manipulation;
  }
  .ch-checkadd-btn:hover:not(:disabled) {
    background: var(--color-action-hover);
  }
  .ch-checkadd-btn:disabled {
    opacity: 0.5;
    cursor: not-allowed;
  }

  .ch-actions {
    display: flex;
    gap: 8px;
    flex-wrap: wrap;
    justify-content: flex-end;
  }

  /*
   * Wide card (desktop / tablet — the card itself is ≥460px): restore the
   * original single-row footer with the doer on the left and actions on the
   * right. Below this, the stacked layout above keeps the assignee name and the
   * action buttons from ever colliding regardless of how many buttons show.
   */
  @container (min-width: 460px) {
    .ch-card-foot {
      flex-direction: row;
      align-items: center;
      justify-content: space-between;
      gap: 12px;
    }
    .ch-people {
      min-width: 0;
      padding-top: 0;
      border-top: none;
    }
    .ch-actions {
      flex-wrap: nowrap;
      flex-shrink: 0;
    }
  }
  .ch-btn {
    font: inherit;
    font-size: 0.8125rem;
    font-weight: 500;
    border: 1px solid transparent;
    border-radius: var(--radius-sm);
    padding: 6px 14px;
    min-height: 34px;
    cursor: pointer;
    -webkit-tap-highlight-color: transparent;
    touch-action: manipulation;
  }
  .ch-btn-primary {
    background: var(--color-primary);
    color: #fff;
  }
  .ch-btn-primary:hover:not(:disabled) {
    background: var(--color-primary-hover);
  }
  .ch-btn-ghost {
    background: transparent;
    color: var(--color-primary);
    border-color: var(--color-line-strong);
  }
  .ch-btn-ghost:hover:not(:disabled) {
    background: var(--color-action-hover);
  }
  .ch-btn:disabled {
    opacity: 0.55;
    cursor: not-allowed;
  }
  .ch-btn-icon {
    background: transparent;
    color: var(--color-text-muted);
    border-color: var(--color-line-strong);
    padding: 6px 8px;
    display: grid;
    place-items: center;
  }
  .ch-btn-icon:hover:not(:disabled) {
    background: var(--color-action-hover);
    color: var(--color-text);
  }

  /* ── Snoozed chip — a quiet "resting" cue, distinct from the dueness tags. ── */
  .ch-tag-snoozed {
    color: var(--color-text);
    background: transparent;
    border: 1px solid var(--color-line-strong);
    font-weight: 500;
  }

  /* ── Snooze preset row (revealed under the actions when "Snooze" is tapped) ── */
  .ch-snooze-menu {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: 8px;
    margin-top: 10px;
    padding-top: 10px;
    border-top: 1px dashed var(--color-line);
  }
  .ch-snooze-preset {
    font: inherit;
    font-size: 0.8125rem;
    font-weight: 500;
    color: var(--color-primary);
    background: transparent;
    border: 1px solid var(--color-line-strong);
    border-radius: 18px;
    padding: 6px 14px;
    min-height: 34px;
    cursor: pointer;
    -webkit-tap-highlight-color: transparent;
    touch-action: manipulation;
  }
  .ch-snooze-preset:hover:not(:disabled) {
    background: var(--color-action-hover);
  }
  .ch-snooze-preset:disabled {
    opacity: 0.55;
    cursor: not-allowed;
  }
  .ch-snooze-pick {
    display: inline-flex;
    align-items: center;
    gap: 6px;
    font-size: 0.8125rem;
    color: var(--color-text-muted);
  }
  .ch-snooze-pick input[type='date'] {
    font: inherit;
    font-size: 0.8125rem;
    color: inherit;
    padding: 6px 8px;
    border: 1px solid var(--color-line-strong);
    border-radius: var(--radius-sm);
    background: var(--color-surface);
    min-height: 34px;
  }
</style>
