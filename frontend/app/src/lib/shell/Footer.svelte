<script lang="ts">
  // Footer. Mirrors MainLayout.razor's footer: Privacy · Terms · ©.
  // Privacy/Terms are the still server-side static Razor pages at the site root,
  // so these are root links (NOT base-prefixed SPA routes).
  const year = new Date().getFullYear();
</script>

<footer class="sh-footer">
  <a class="sh-footer-link" href="/privacy">Privacy Policy</a>
  <span class="sh-footer-sep">•</span>
  <a class="sh-footer-link" href="/terms">Terms of Service</a>
  <span class="sh-footer-sep">•</span>
  <span class="sh-footer-copy">© {year} sirmaelstrom</span>
</footer>

<style>
  .sh-footer {
    display: flex;
    justify-content: center;
    align-items: center;
    flex-wrap: wrap;
    gap: 16px;
    padding: 16px;
    color: var(--color-text-muted);
    font-size: 0.75rem;
  }
  .sh-footer-link {
    color: var(--color-secondary);
    text-decoration: none;
  }
  .sh-footer-link:hover {
    text-decoration: underline;
  }
  .sh-footer-sep {
    color: var(--color-text-muted);
  }
</style>
