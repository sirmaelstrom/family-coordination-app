# Family Coordination App

## What This Is

A web application for family meal planning and coordination. Families maintain a recipe database, plan weekly meals, and automatically generate collaborative shopping lists from their meal plan. Designed for household use with mobile-first access, focusing on reducing coordination overhead and meal planning stress.

## Core Value

The integrated workflow: recipes → meal plan → shopping list. Manual coordination is replaced with automated aggregation and real-time collaboration, reducing mental load from scattered information and last-minute decisions.

## Requirements

### Validated

(None yet — ship to validate)

### Active

- [ ] Users can add recipes via URL import with automatic parsing
- [ ] Users can manually create recipes with ingredients and instructions
- [ ] Users can create a weekly meal plan by assigning recipes to dates/meals
- [ ] Users can generate a shopping list from the meal plan with smart ingredient aggregation
- [ ] Multiple users can collaboratively edit shopping lists with real-time updates
- [ ] Users can authenticate with Google OAuth (email whitelist for authorized family members)
- [ ] Mobile-responsive interface works on phones at grocery store
- [ ] Recipe ingredients are categorized (Meat, Produce, Dairy, etc.) for organized shopping
- [ ] Shopping list groups items by category matching typical store layout
- [ ] Users can check off items at the store with real-time sync across devices
- [ ] Users can scale recipe servings and ingredient quantities adjust automatically
- [ ] Users can search and filter recipes by name, tags, or ingredients

### Out of Scope

- Google Calendar integration — deferred to Phase 2 (app meal plan view is sufficient for MVP)
- Shared to-do lists — deferred to Phase 2 (separate from meal planning workflow)
- Recipe step images — deferred to Phase 2 (text instructions sufficient for MVP)
- Pantry inventory tracking — deferred to Phase 3 (reduces over-buying but not core workflow)
- Multi-tenant onboarding — Phase 1 is single household, designed for expansion
- OAuth providers beyond Google — email/password auth not needed, Google only for MVP
- Recipe ratings or favorites — deferred (nice-to-have social features)
- Meal plan templates — deferred (manual planning sufficient for MVP)

## Context

**Current state:** Family coordination is chaotic - last-minute "what's for dinner?" decisions, recipes scattered in browser bookmarks and photos, shopping lists via text messages, duplicate purchases, forgotten ingredients.

**Success metric:** Less mental load. Coordination feels easier and less stressful than the current ad-hoc approach. Qualitative measure: family actually uses it regularly instead of falling back to old habits.

**Home infrastructure:**
- [SERVER]: Ubuntu 24.04 server (Intel i3-4170, 23GB RAM, 10Gbps NIC)
- Domain: *.example.com (via Cloudflare, points to [SERVER])
- Existing: Docker Compose services, nginx reverse proxy, ZFS storage pool
- Development: flagg (Windows 11 WSL) with .NET 8 SDK

**Design approach:**
- Single-tenant MVP (one household: the family)
- Data model includes HouseholdId for future multi-tenant expansion
- Household = tenant unit (multiple households could use the app independently)

## Constraints

- **Tech stack**: Blazor Server (.NET 8), PostgreSQL, Docker — chosen for C# expertise everywhere and built-in SignalR for real-time collaboration
- **Deployment**: Must run on [SERVER] (Ubuntu 24.04) via Docker Compose, accessible at your-domain.example.com
- **Authentication**: Google OAuth only, email whitelist for authorized family members (no public signup)
- **Real-time**: SignalR over WebSocket for collaborative shopping list editing (must work on mobile with spotty WiFi)
- **Mobile-first**: Must be usable at grocery store on phone (large touch targets, fast load, offline-tolerant)
- **Storage**: PostgreSQL database and image uploads on ZFS pool (themanjesus) with daily snapshots
- **Timeline**: Flexible, quality over speed (original estimate: 4-6 weeks, but no hard deadline)

## Key Decisions

| Decision | Rationale | Outcome |
|----------|-----------|---------|
| Blazor Server over Blazor WASM | C# on both client and server (leverage .NET expertise), built-in SignalR for real-time (no WebSocket plumbing), simpler deployment (no client-side secrets) | — Pending |
| PostgreSQL over SQL Server | Linux deployment target ([SERVER] is Ubuntu), open source, Docker-friendly, sufficient for single-tenant scale | — Pending |
| Single-tenant MVP, designed for multi-tenant | Ship faster for family use (no tenant isolation complexity), but include HouseholdId in data model for future expansion | — Pending |
| Defer calendar sync and to-do lists to Phase 2 | Focus MVP on core meal planning workflow (recipes → meal plan → shopping), calendar sync is nice-to-have, to-do lists are separate concern | — Pending |
| Both URL import and manual recipe entry in Phase 1 | Family won't adopt if they have to manually type all recipes, but manual entry needed for custom/family recipes | — Pending |
| Google OAuth only (no email/password) | Family already uses Google, simplifies auth (no password reset flow), email whitelist for access control | — Pending |

---
*Last updated: 2026-01-22 after initialization*
