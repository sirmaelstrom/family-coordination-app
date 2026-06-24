<script lang="ts">
  // The toast region. Mirrors the meal-plan island's Toasts component, re-scoped
  // to `rc-`. Mounted once at each App root (ListApp / EditApp).
  import { toasts, dismissToast } from '../toasts.svelte';
</script>

<div class="rc-toast-region" role="status" aria-live="polite">
  {#each toasts() as toast (toast.id)}
    <div class="rc-toast rc-toast-{toast.kind}">
      <span class="rc-toast-msg">{toast.message}</span>
      {#if toast.action}
        <button
          type="button"
          class="rc-toast-action"
          onclick={() => {
            toast.action?.onClick();
            dismissToast(toast.id);
          }}
        >
          {toast.action.label}
        </button>
      {/if}
      <button
        type="button"
        class="rc-toast-close"
        aria-label="Dismiss"
        onclick={() => dismissToast(toast.id)}
      >
        ×
      </button>
    </div>
  {/each}
</div>

<style>
  .rc-toast-region {
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
    .rc-toast-region {
      bottom: calc(80px + env(safe-area-inset-bottom, 0px));
    }
  }
  .rc-toast {
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
    animation: rc-toast-in 0.2s ease-out;
  }
  .rc-toast-success {
    background: var(--color-success);
  }
  .rc-toast-error {
    background: var(--color-error);
  }
  .rc-toast-msg {
    flex: 1;
    min-width: 0;
  }
  .rc-toast-action {
    background: transparent;
    border: none;
    color: #fff;
    font: inherit;
    font-weight: 600;
    text-transform: uppercase;
    font-size: 0.8125rem;
    cursor: pointer;
    padding: 0 4px;
    flex-shrink: 0;
  }
  .rc-toast-action:hover {
    text-decoration: underline;
  }
  .rc-toast-close {
    background: transparent;
    border: none;
    color: rgba(255, 255, 255, 0.8);
    font-size: 1.25rem;
    line-height: 1;
    padding: 0 4px;
    cursor: pointer;
  }
  .rc-toast-close:hover {
    color: #fff;
  }
  @keyframes rc-toast-in {
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
