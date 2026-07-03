<script lang="ts">
  // Canonical member avatar — the ONE copy island routes import (chores uses it).
  // Props = union of all callers: UserAvatar's set PLUS { relation }.
  //   relation is an accessible-label prefix, e.g. "Claimed by" / "Assigned to" /
  //   "Hand off to" — chores passes a string; treat as nullable.
  // Real callers first appear in Wave-3 chores (ChoreCard / HandOffPicker /
  // EquityBoard / CompleteDialog); ported verbatim from frontend/chores so the
  // existing prop shape is preserved by construction.
  interface Props {
    name: string | null;
    initials: string | null;
    pictureUrl: string | null;
    size?: number;
    relation?: string | null;
  }

  let { name, initials, pictureUrl, size = 24, relation = null }: Props = $props();

  // Fallback: derive initials from name if the server didn't provide them.
  let shown = $derived.by(() => {
    if (initials && initials.trim()) return initials.trim().slice(0, 2).toUpperCase();
    if (name && name.trim()) {
      const parts = name.trim().split(/\s+/);
      return parts.length >= 2
        ? (parts[0][0] + parts[1][0]).toUpperCase()
        : parts[0].slice(0, 2).toUpperCase();
    }
    return '?';
  });

  let label = $derived.by(() => {
    if (!name) return undefined;
    return relation ? `${relation} ${name}` : name;
  });
</script>

<span
  class="sh-avatar"
  style="--size: {size}px;"
  title={name ?? undefined}
  aria-label={label}
>
  {#if pictureUrl}
    <img src={pictureUrl} alt="" referrerpolicy="no-referrer" />
  {:else}
    <span class="sh-avatar-initials">{shown}</span>
  {/if}
</span>

<style>
  .sh-avatar {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    width: var(--size, 24px);
    height: var(--size, 24px);
    border-radius: 50%;
    background: var(--color-primary);
    color: #fff;
    font-size: calc(var(--size, 24px) * 0.4);
    font-weight: 600;
    overflow: hidden;
    flex-shrink: 0;
  }
  .sh-avatar img {
    width: 100%;
    height: 100%;
    object-fit: cover;
    display: block;
  }
  .sh-avatar-initials {
    line-height: 1;
    letter-spacing: 0.02em;
  }
</style>
