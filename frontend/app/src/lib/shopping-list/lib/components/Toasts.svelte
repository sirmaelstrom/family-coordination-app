<script lang="ts">
  import { toasts, dismissToast, triggerToastAction } from '../toasts.svelte';
</script>

<div class="sl-toast-region" role="status" aria-live="polite">
  {#each toasts() as toast (toast.id)}
    <div class="sl-toast sl-toast-{toast.kind}">
      <span class="sl-toast-msg">{toast.message}</span>
      {#if toast.actionLabel}
        <button
          type="button"
          class="sl-toast-action"
          onclick={() => triggerToastAction(toast.id)}
        >
          {toast.actionLabel}
        </button>
      {/if}
      <button
        type="button"
        class="sl-toast-close"
        aria-label="Dismiss"
        onclick={() => dismissToast(toast.id)}
      >
        ×
      </button>
    </div>
  {/each}
</div>

<style>
  .sl-toast-region {
    position: fixed;
    left: 50%;
    bottom: 24px;
    transform: translateX(-50%);
    display: flex;
    flex-direction: column;
    gap: 8px;
    z-index: 100;
    pointer-events: none;
    max-width: min(560px, calc(100vw - 32px));
  }
  .sl-toast {
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
    animation: sl-toast-in 0.2s ease-out;
  }
  .sl-toast-success {
    background: var(--color-success);
  }
  .sl-toast-error {
    background: var(--color-error);
  }
  .sl-toast-msg {
    flex: 1;
    min-width: 0;
  }
  .sl-toast-action {
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
  }
  .sl-toast-action:hover {
    background: rgba(255, 255, 255, 0.15);
  }
  .sl-toast-close {
    background: transparent;
    border: none;
    color: rgba(255, 255, 255, 0.8);
    font-size: 1.25rem;
    line-height: 1;
    padding: 0 4px;
    cursor: pointer;
  }
  .sl-toast-close:hover {
    color: #fff;
  }
  @keyframes sl-toast-in {
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
