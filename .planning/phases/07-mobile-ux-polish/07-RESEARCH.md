# Phase 7 Research: Mobile & UX Polish

## PWA Implementation in Blazor Server

### Manifest and Service Worker

Blazor Server apps can be PWAs with some caveats:
- Service worker caches static assets (HTML, CSS, JS)
- Server connection still required for interactivity
- Offline mode = read-only cached shell with "offline" banner

**Files needed:**
1. `wwwroot/manifest.json` — App metadata for installation
2. `wwwroot/service-worker.js` — Cache strategy
3. Link manifest in `_Host.cshtml` or `App.razor`

**Manifest example:**
```json
{
  "name": "Family Coordination",
  "short_name": "Family",
  "start_url": "/",
  "display": "standalone",
  "background_color": "#1e1e1e",
  "theme_color": "#1e1e1e",
  "icons": [
    { "src": "/icons/icon-192.png", "sizes": "192x192", "type": "image/png" },
    { "src": "/icons/icon-512.png", "sizes": "512x512", "type": "image/png" }
  ]
}
```

**Service worker strategy:**
- Cache-first for static assets
- Network-first for API calls (Blazor SignalR)
- Show offline page when SignalR disconnects

### MudBlazor Theme Integration

Current theme from Phase 2:
- Dark gray background (not true black)
- Material Design components
- `MudThemeProvider` with custom palette

PWA theme color should match the app bar color.

## Touch Target Sizing

### WCAG Guidelines

- **WCAG 2.1 Level AAA:** 44×44 CSS pixels minimum
- **WCAG 2.2 Level AA:** 24×24 minimum with 24px spacing
- **Industry standard:** 40-48px for mobile

### MudBlazor Component Sizing

**MudCheckBox:**
```razor
<MudCheckBox T="bool" Size="Size.Large" />
```
Size.Large is approximately 40px.

**MudIconButton:**
```razor
<MudIconButton Size="Size.Large" />
```

**CSS overrides for shopping list:**
```css
.shopping-item .mud-checkbox-button {
    min-width: 44px;
    min-height: 44px;
}
```

## Responsive Utilities

### MudBlazor Breakpoints

| Name | Pixels |
|------|--------|
| xs | 0-599 |
| sm | 600-959 |
| md | 960-1279 |
| lg | 1280-1919 |
| xl | 1920+ |

### MudHidden Component

Already used in Phase 3 for meal plan views:
```razor
<MudHidden Breakpoint="Breakpoint.SmAndDown" Invert="true">
    <!-- Mobile content -->
</MudHidden>
```

### Grid Responsiveness

```razor
<MudItem xs="12" sm="6" md="4">
```

## Sync Status Patterns

### Existing Infrastructure (Phase 5)

- `PollingService` — Polls for changes every 5 seconds
- `DataNotifier` — Broadcasts change events
- `PresenceService` — Tracks online users

### Sync Status States

1. **Connected** — SignalR active, polling working
2. **Syncing** — Currently fetching updates
3. **Offline** — SignalR disconnected or no network
4. **Error** — Polling failed (server error, auth expired)

### Blazor SignalR Connection State

```csharp
// In App.razor or MainLayout
@inject NavigationManager Navigation

protected override void OnInitialized()
{
    // Access circuit/hub state
    // Blazor Server doesn't expose this directly - need workaround
}
```

**Alternative approach:** Track polling success/failure in PollingService:
```csharp
public class PollingService
{
    public bool IsConnected { get; private set; }
    public bool IsSyncing { get; private set; }
    public DateTime? LastSyncTime { get; private set; }
    public string? LastError { get; private set; }
}
```

## Icon Generation

### Requirements

- 192×192 PNG (standard)
- 512×512 PNG (splash screen)
- 16×16, 32×32 favicon

### Options

1. **realfavicongenerator.net** — Generate all sizes from one image
2. **Simple placeholder** — Solid color with text "F" for MVP
3. **Custom icon** — Rose emoji or meal icon (defer to v2)

For MVP: Generate simple icons with app initial or food emoji.

## Performance Considerations

### Slow 3G Target (Requirement)

- Initial load under 3 seconds
- Service worker caches after first load
- Subsequent loads should be near-instant

### Blazor Server Overhead

- SignalR connection adds ~100ms latency
- Not much we can do about this
- Focus on perceived performance (optimistic UI)

---

## Implementation Plan

1. **07-01: PWA Infrastructure** — manifest.json, service worker, icons
2. **07-02: Touch Target Optimization** — Audit and fix interactive elements
3. **07-03: Responsive Verification** — Test and fix mobile layouts
4. **07-04: Sync Status Indicator** — Header component with connection state
5. **07-05: Human Verification** — Mobile testing on real device

---
*Created: 2026-01-24*
