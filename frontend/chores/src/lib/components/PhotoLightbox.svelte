<script lang="ts">
  // ───────────────────────────────────────────────────────────────────────
  // PhotoLightbox (v1.2) — the single shared tap-to-enlarge overlay. Reads the
  // module lightbox store and renders the active photo in a native <dialog>,
  // the same pattern the sheets use (showModal() + ::backdrop). Tap anywhere
  // dismisses; Esc closes natively. Mounted ONCE in App.svelte.
  //
  // The <dialog> fills the viewport and centers the image so the whole surface
  // is a dismiss target (matching the zoom-out cursor on the photo).
  //
  // ⚠ Never trigger window.alert/confirm here — this is the <dialog> overlay.
  // ───────────────────────────────────────────────────────────────────────
  import { lightbox, closePhoto } from '../lightbox.svelte';

  let dialogEl: HTMLDialogElement | null = $state(null);
  let lb = $derived(lightbox());

  $effect(() => {
    if (!dialogEl) return;
    if (lb.src && !dialogEl.open) dialogEl.showModal();
    else if (!lb.src && dialogEl.open) dialogEl.close();
  });

  // A photo viewer dismisses on tap anywhere (image or backdrop) — the natural
  // mobile gesture; Esc also closes via the dialog's native `onclose`.
  function onClick() {
    closePhoto();
  }
</script>

<dialog
  bind:this={dialogEl}
  class="ch-lightbox"
  onclose={closePhoto}
  onclick={onClick}
  aria-label="Enlarged photo"
>
  {#if lb.src}
    <img class="ch-lightbox-img" src={lb.src} alt={lb.alt} />
  {/if}
</dialog>

<style>
  /*
   * ⚠ Only style `display` on the OPEN dialog. Setting `display` on the bare
   * `.ch-lightbox` would override the UA `dialog:not([open]) { display: none }`
   * rule (author > UA), leaving a transparent full-viewport layer on top that
   * swallows every click when the lightbox is closed. Gate it to [open].
   */
  .ch-lightbox[open] {
    display: grid;
    place-items: center;
  }
  .ch-lightbox {
    border: none;
    margin: 0;
    padding: 0;
    inset: 0;
    width: 100vw;
    max-width: 100vw;
    height: 100dvh;
    max-height: 100dvh;
    background: transparent;
    overflow: hidden;
  }
  .ch-lightbox::backdrop {
    background: rgba(0, 0, 0, 0.82);
  }
  .ch-lightbox-img {
    display: block;
    max-width: 92vw;
    max-height: 88dvh;
    width: auto;
    height: auto;
    border-radius: 8px;
    box-shadow: var(--shadow-4);
    cursor: zoom-out;
  }
</style>
