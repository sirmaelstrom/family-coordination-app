# Shopping List Island

A self-contained Svelte 5 + Vite frontend that renders inside the Blazor app
at `/shopping-list`. The Razor shell (`Components/Pages/ShoppingList.razor`)
emits a `<div id="shopping-list-root">` with data attributes and loads the
compiled bundle from `/islands/shopping-list/`.

This directory is designed to port into the eventual SvelteKit rewrite
(`family-kitchen-svelte`) with minimal change — the components and API
client live under `src/lib/` matching the SvelteKit convention.

## Commands

```bash
npm install
npm run dev     # Vite dev server on :5173; proxies /api to :5000
npm run build   # Emits to ./dist/ — picked up by MSBuild target on dotnet build
npm run check   # Type-check Svelte + TS
```

## Production build flow

1. `npm run build` writes `dist/index.js`, `dist/index.css`, and `dist/chunks/`.
2. On `dotnet build` (local or Docker), the `CopyShoppingListIsland` MSBuild
   target in `FamilyCoordinationApp.csproj` copies `dist/` into
   `src/FamilyCoordinationApp/wwwroot/islands/shopping-list/`.
3. ASP.NET static files serve it at `/islands/shopping-list/index.js`.

The wwwroot destination is gitignored — the source of truth is `dist/`, which
is also gitignored. Run `npm run build` before `dotnet build` locally.

## Feature flag

The Razor shell only loads the island when `SHOPPING_LIST_USE_ISLAND=true`
(env var or appsettings key). Otherwise the original Blazor page renders.
