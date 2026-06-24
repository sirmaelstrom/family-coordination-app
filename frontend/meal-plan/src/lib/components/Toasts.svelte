<script lang="ts">
  // The toast region. Mirrors the chores island's Toasts component, re-scoped
  // to `mp-`. Mounted once at the App root.
  import { toasts, dismissToast } from '../toasts.svelte';
</script>

<div class="mp-toast-region" role="status" aria-live="polite">
  {#each toasts() as toast (toast.id)}
    <div class="mp-toast mp-toast-{toast.kind}">
      <span class="mp-toast-msg">{toast.message}</span>
      <button
        type="button"
        class="mp-toast-close"
        aria-label="Dismiss"
        onclick={() => dismissToast(toast.id)}
      >
        ×
      </button>
    </div>
  {/each}
</div>

<style>
  .mp-toast-region {
    position: fixed;
    left: 50%;
    bottom: 24px;
    transform: translateX(-50%);
    display: flex;
    flex-direction: column;
    gap: 8px;
    z-index: 1100;
    pointer-events: none;
    max-width: min(560px, calc(100vw - 32px));
  }
  /* Mobile: clear the MainLayout bottom nav so toasts aren't hidden behind it. */
  @media (max-width: 960px) {
    .mp-toast-region {
      bottom: calc(80px + env(safe-area-inset-bottom, 0px));
    }
  }
  .mp-toast {
    display: flex;
    align-items: center;
    gap: 12px;
    padding: 10px 12px 10px 16px;
    border-radius: var(--radius-sm);
    color: #fff;
    background: #323232;
    box-shadow: var(--shadow-4);
    font-size: 0.875rem;
    pointer-events: auto;
    animation: mp-toast-in 0.2s ease-out;
  }
  .mp-toast-success {
    background: var(--color-success);
  }
  .mp-toast-error {
    background: var(--color-error);
  }
  .mp-toast-msg {
    flex: 1;
    min-width: 0;
  }
  .mp-toast-close {
    background: transparent;
    border: none;
    color: rgba(255, 255, 255, 0.8);
    font-size: 1.25rem;
    line-height: 1;
    padding: 0 4px;
    cursor: pointer;
  }
  .mp-toast-close:hover {
    color: #fff;
  }
  @keyframes mp-toast-in {
    from {
      opacity: 0;
      transform: translateY(8px);
    }
    to {
      opacity: 1;
      transform: translateY(0);
    }
  }
</style>
