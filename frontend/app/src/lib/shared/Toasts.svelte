<script lang="ts">
  // Canonical toast region — mounted once in the shell +layout.svelte.
  // Renders the normalised `action` from the shared toast store.
  import { toasts, dismissToast, triggerToastAction } from './toast-store.svelte';
</script>

<div class="sh-toast-region" role="status" aria-live="polite">
  {#each toasts() as toast (toast.id)}
    <div class="sh-toast sh-toast-{toast.kind}">
      <span class="sh-toast-msg">{toast.message}</span>
      {#if toast.action}
        <button
          type="button"
          class="sh-toast-action"
          onclick={() => triggerToastAction(toast.id)}
        >
          {toast.action.label}
        </button>
      {/if}
      <button
        type="button"
        class="sh-toast-close"
        aria-label="Dismiss"
        onclick={() => dismissToast(toast.id)}
      >
        ×
      </button>
    </div>
  {/each}
</div>

<style>
  .sh-toast-region {
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
  /* Mobile: clear the bottom nav so toasts aren't hidden behind it. */
  @media (max-width: 960px) {
    .sh-toast-region {
      bottom: calc(var(--shell-mobile-nav-height, 56px) + 24px + env(safe-area-inset-bottom, 0px));
    }
  }
  .sh-toast {
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
    animation: sh-toast-in 0.2s ease-out;
  }
  .sh-toast-success {
    background: var(--color-success);
  }
  .sh-toast-error {
    background: var(--color-error);
  }
  .sh-toast-msg {
    flex: 1;
    min-width: 0;
  }
  .sh-toast-action {
    font: inherit;
    font-weight: 600;
    background: transparent;
    border: none;
    color: #fff;
    text-transform: uppercase;
    letter-spacing: 0.04em;
    padding: 4px 8px;
    border-radius: var(--radius-sm);
    cursor: pointer;
    flex-shrink: 0;
  }
  .sh-toast-action:hover {
    background: rgba(255, 255, 255, 0.15);
  }
  .sh-toast-close {
    background: transparent;
    border: none;
    color: rgba(255, 255, 255, 0.8);
    font-size: 1.25rem;
    line-height: 1;
    padding: 0 4px;
    cursor: pointer;
  }
  .sh-toast-close:hover {
    color: #fff;
  }
  @keyframes sh-toast-in {
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
