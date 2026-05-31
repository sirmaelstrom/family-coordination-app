<script lang="ts">
  // Member avatar — initials or picture, mirroring the shopping-list
  // UserAvatar convention. Renders from a MemberDto's initials/pictureUrl.

  interface Props {
    name: string | null;
    initials: string | null;
    pictureUrl: string | null;
    size?: number;
    /** Accessible label prefix, e.g. "Minded by" / "Claimed by". */
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
  class="ch-avatar"
  style="--size: {size}px;"
  title={name ?? undefined}
  aria-label={label}
>
  {#if pictureUrl}
    <img src={pictureUrl} alt="" referrerpolicy="no-referrer" />
  {:else}
    <span class="ch-avatar-initials">{shown}</span>
  {/if}
</span>

<style>
  .ch-avatar {
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
  .ch-avatar img {
    width: 100%;
    height: 100%;
    object-fit: cover;
    display: block;
  }
  .ch-avatar-initials {
    line-height: 1;
    letter-spacing: 0.02em;
  }
</style>
