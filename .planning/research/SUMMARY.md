# Project Research Summary

**Project:** Family Coordination Web App
**Domain:** Family Meal Planning and Coordination
**Researched:** 2026-01-22
**Confidence:** HIGH

## Executive Summary

Family meal planning apps are a well-established domain with proven patterns. Successful implementations focus on integrated workflows (recipe storage → meal planning → shopping list generation) rather than isolated features. The recommended approach is Blazor Server with PostgreSQL, leveraging SignalR's built-in real-time capabilities for family collaboration. This stack provides native mobile responsiveness, real-time sync, and strong .NET integration without the complexity of separate client/server codebases.

The critical success factor is mobile-first design with reliable real-time collaboration. Users plan on desktop but consume (shopping lists) on mobile in grocery stores. This requires robust offline handling, low-latency updates, and touch-optimized UX. The primary technical risks are SignalR circuit memory leaks, recipe scraping reliability collapse, and mobile network latency breaking real-time features. These are well-documented problems with established mitigation patterns.

Start with core CRUD (recipes, meal plans, shopping lists) before adding real-time collaboration. Recipe scraping should use schema.org JSON-LD extraction with graceful degradation to manual entry. Multi-tenant isolation must use composite foreign keys from day one—retrofitting is painful and creates security vulnerabilities. Ship a focused MVP (recipe → plan → list workflow) before adding advanced features like AI suggestions, nutrition tracking, or delivery integration.

## Key Findings

### Recommended Stack

**Core: Blazor Server (.NET 8) + PostgreSQL + SignalR**

Blazor Server provides server-side rendering with built-in real-time capabilities via SignalR, eliminating the need for separate WebSocket infrastructure. PostgreSQL offers advanced data types (arrays for tags, JSON columns for flexible recipe metadata) with excellent .NET support via Npgsql EF Core provider. The stack is production-ready with mature tooling, LTS support through November 2026, and well-documented patterns.

**Core technologies:**
- **.NET 8 / Blazor Server**: Component-based UI with server-side rendering, native SignalR integration for real-time updates, reduces client payload
- **PostgreSQL 16+**: Advanced data types (arrays, JSON), open-source, excellent .NET support via Npgsql (8.0.10)
- **Entity Framework Core 8**: Type-safe queries, migrations, PostgreSQL-specific features (DbContextFactory pattern required for Blazor Server)
- **MudBlazor 7.21**: Material Design component library, mobile-first responsive design, 100% C# implementation
- **HtmlAgilityPack / AngleSharp**: Recipe URL scraping with schema.org JSON-LD extraction (80%+ of sites), CSS selector fallback

**Critical version requirement**: Npgsql.EntityFrameworkCore.PostgreSQL 8.0.10 is compatible with EF Core 8.x only (not 9+).

### Expected Features

**Must have (table stakes):**
- **Recipe URL import** — Industry standard, users expect to save from any website (web scraping inherently fragile, 10-15% require weekly fixes)
- **Manual recipe entry** — Fallback for family recipes, handwritten cards, non-digital sources
- **Recipe organization/tagging** — Users need to find recipes later via tags, collections, search
- **Weekly meal calendar** — Core workflow, drag/drop recipes onto days (careful mobile touch target sizing required)
- **Auto-generated shopping list** — Expected integration: recipes → ingredients → consolidated list
- **Smart ingredient consolidation** — Critical for usability: "chicken breast" appears once, not 3x from different recipes (NLP/semantic parsing required)
- **Shopping list editing** — Users always add non-recipe items (paper towels, dog food)
- **Cross-device sync** — Users plan on desktop, shop on mobile—sync non-negotiable
- **Mobile-first responsive design** — Mobile dominates use case (grocery store = phone in hand)

**Should have (competitive):**
- **Real-time collaborative shopping list** — Core differentiator: family sees updates live (high complexity: WebSockets, conflict resolution, offline support)
- **Smart ingredient consolidation** — NLP-based matching ("grilled chicken breast" = "chicken breast"), unit conversion (2 cups + 1 pint)
- **Flexible meal moving/swapping** — Drag-and-drop calendar updates, regenerate shopping list on changes
- **Offline-first mobile UX** — Shop in signal-dead stores (service workers, sync on reconnect—complex but valuable)
- **Leftover planning** — Reduce food waste: mark "leftover from Monday," don't re-buy ingredients

**Defer (v2+):**
- **Nutrition tracking/calorie counting** — Different user intent (fitness vs. coordination), feature creep risk, manual logging causes retention drop
- **AI meal plan generation** — Premature for MVP, requires taste preference training data, users often reject auto-plans
- **Grocery delivery integration** — Complex APIs, business partnerships, not core differentiator (export to clipboard instead)
- **Video recipe imports (TikTok, Instagram)** — Extremely complex parsing, high error rate, maintenance nightmare
- **Recipe rating/review system** — Creates community management burden, SEO focus shifts, scope creep to social network

### Architecture Approach

**Clean Architecture: Blazor Server (UI) → Application Services → Infrastructure (EF Core) → Domain Entities**

The standard pattern separates concerns with Domain (entities like Recipe, MealPlan, ShoppingList), Infrastructure (data access via repositories), Application (business logic services), and Server (Blazor components). SignalR Hub layer handles real-time broadcasts isolated from business services. DbContextFactory pattern is mandatory for Blazor Server to avoid concurrent operation errors on shared DbContext instances.

**Major components:**
1. **Blazor Components (UI layer)** — Razor components with code-behind for recipes, meal planning, shopping list; inject scoped services, keep thin (UI logic only)
2. **SignalR MealPlanHub** — Real-time broadcast of state changes using groups for family isolation (prevent cross-tenant leaks), strongly-typed hub with auto-reconnect
3. **Application Services** — Business logic (RecipeService, MealPlanService, ShoppingListService), orchestrate data, inject repositories, handle validation via FluentValidation
4. **Recipe Scraper Service** — Async background service with queue-based processing (avoids blocking UI), uses HtmlAgilityPack/AngleSharp for schema.org extraction, retry logic for transient failures
5. **Repositories (Data layer)** — EF Core query composition using DbContextFactory pattern, encapsulate PostgreSQL-specific features (array columns for tags, JSON columns for metadata)
6. **PostgreSQL + ZFS Storage** — Metadata in PostgreSQL, recipe images on ZFS (/zfs/meal-planner/images/), served via nginx static file handler

**Key patterns:**
- **DbContextFactory** — Required for Blazor Server: create short-lived `await using var context = await factory.CreateDbContextAsync()` to avoid concurrency exceptions
- **SignalR Groups for Family Isolation** — `Groups.AddToGroupAsync(Context.ConnectionId, $"Family_{familyId}")` ensures updates only broadcast to family members
- **Aggregate Root for Shopping List** — MealPlan entity generates shopping list by traversing assigned recipes, aggregating ingredients in domain model
- **Microservice-Style Scraper Isolation** — Background service processes scrape jobs from queue, non-blocking UI, retry logic, failure isolation

### Critical Pitfalls

1. **SignalR Circuit Memory Leaks** — Blazor Server circuits hold state in server memory; without proper disposal, memory grows with every user and never shrinks. Implement IDisposable/IAsyncDisposable for all components subscribing to events, use scoped services, configure circuit retention periods, monitor memory usage in production. Warning signs: memory usage steadily increases, out of memory exceptions after hours/days.

2. **Recipe Scraping Reliability Collapse** — Web scraping works in dev but silently fails in production due to anti-bot detection, dynamic AJAX content, structure changes. Use managed scraping with rotation (not simple HttpClient), implement AI-native extraction (LLM-based) as fallback to brittle selectors, design for graceful degradation to manual entry, monitor success rates per domain. Warning signs: scraping success rate declining, empty recipe data with no error message, specific popular sites failing.

3. **Mobile Network Latency Breaking Real-Time Collaboration** — Blazor Server requires round-trip to server for every interaction; at 200ms+ latency, app breaks. Implement optimistic UI updates (reflect action immediately, reconcile async), use version-based optimistic concurrency, extend circuit timeout for mobile, test on throttled network (Slow 3G), consider Blazor WebAssembly hybrid for shopping list only. Warning signs: users complain "slow" on mobile, duplicate check-offs, timeouts during shopping trips.

4. **Missing Composite Foreign Keys Breaking Multi-Tenant Isolation** — Application-level filtering (WHERE household_id = X) is only protection; forgetting filter in one query exposes Household A's data to Household B. Use composite primary keys `PRIMARY KEY (household_id, id)` and composite foreign keys `FOREIGN KEY (household_id, recipe_id) REFERENCES recipes (household_id, id)` on all tenant-scoped tables for database-level enforcement. Must be in initial schema design—retrofitting is painful migration.

5. **Feature Creep Killing MVP Momentum** — Project starts with core workflow but accumulates "good ideas" (calendar integration, pantry tracking, recipe ratings, nutrition analysis), stretching 6 weeks to 6 months. Define hard MVP scope in PROJECT.md before starting, implement MoSCoW prioritization (Must/Should/Could/Won't), ship MVP first then add features after real users validate product. Warning signs: roadmap growing during development, "just one more feature" conversations weekly, no user has seen working version.

6. **Mobile UX Friction Causing Abandonment** — Touch targets too small (< 44px), initial load 5+ seconds on 3G, horizontal scrolling hijacks vertical scroll, no virtualization for long lists. Touch targets minimum 44-48px, implement Blazor Virtualize component for lists > 20 items, test on throttled network ("Slow 3G"), optimize initial load with code splitting/lazy loading, test on actual low-end Android device. Warning signs: users complain "slow" on mobile, accidentally tapping wrong items, bounce rate high on mobile vs desktop.

7. **Unhandled Exceptions Crashing Circuits** — Unhandled exception in component crashes entire SignalR circuit; user sees "Disconnected" modal, refresh loses all unsaved work. Implement global ErrorBoundary component, wrap all async operations in try-catch, use circuit handlers to intercept exceptions, implement retry logic for transient failures, preserve state in external store (not just in-memory). Warning signs: users reporting "page froze," circuit disposal events in logs, lost shopping list edits.

## Implications for Roadmap

Based on research, suggested phase structure (7-8 phases):

### Phase 1: Data Foundation & Recipe CRUD
**Rationale:** Everything depends on data access; build foundation first before complex features. Recipe management provides immediate value with no complex dependencies.

**Delivers:**
- Domain entities (Recipe, Ingredient, MealPlan, ShoppingList)
- DbContext, migrations, repository pattern
- Recipe CRUD services and Blazor components
- Manual recipe entry (no scraping yet)

**Addresses:**
- Recipe manual entry (table stakes)
- Recipe organization/tagging (table stakes)
- Basic search/filtering (table stakes)

**Avoids:**
- **Pitfall #4** (composite foreign keys)—multi-tenant isolation designed from start with composite PKs/FKs
- **Pitfall #1** (memory leaks)—proper disposal patterns established early

**Research Flag:** Standard CRUD patterns, well-documented. Skip phase research.

---

### Phase 2: Meal Planning Core
**Rationale:** Core workflow depends on recipes existing. Simpler to build before adding drag-drop and real-time features.

**Delivers:**
- Meal plan service and repository
- Weekly planner component with static cards
- Click-based recipe assignment to days (no drag-drop yet)
- Generate meal plan for week

**Addresses:**
- Weekly meal calendar (table stakes)
- Meal moving/swapping (partial—click-based, not drag-drop)

**Uses:**
- EF Core with DbContextFactory pattern
- MudBlazor calendar components

**Avoids:**
- **Pitfall #5** (feature creep)—defer drag-drop UX polish to later phase
- **Pitfall #1** (memory leaks)—scoped services, proper component disposal

**Research Flag:** Standard patterns. Skip phase research.

---

### Phase 3: Shopping List Generation
**Rationale:** Depends on meal plan existing, completes core workflow. No external dependencies, focused on domain logic.

**Delivers:**
- Shopping list service with ingredient aggregation
- Smart consolidation logic (NLP-based matching, unit conversion)
- Shopping list component with categorization
- Generate from meal plan, manual editing

**Addresses:**
- Auto-generated shopping list (table stakes)
- Smart ingredient consolidation (differentiator, critical for usability)
- Shopping list editing (table stakes)

**Implements:**
- Aggregate Root pattern—MealPlan.GenerateShoppingList()
- PostgreSQL array functions for aggregation

**Avoids:**
- **Pitfall #5** (feature creep)—no real-time yet, polling-based sync acceptable for MVP

**Research Flag:** **Needs phase research** for ingredient parsing/matching strategies (NLP libraries, semantic similarity, unit conversion).

---

### Phase 4: Multi-User Household Sharing (Basic Sync)
**Rationale:** Validates family collaboration value before investing in real-time infrastructure. Polling-based sync simpler than WebSockets.

**Delivers:**
- Household/family entity and user association
- Shared access to meal plans and shopping lists
- Polling-based sync (refresh to see updates)
- Basic authentication (Google OAuth)

**Addresses:**
- Cross-device sync (table stakes)—simplified version
- Multi-user household sharing (differentiator)

**Uses:**
- ASP.NET Core authentication middleware
- SignalR groups for family isolation (prep for Phase 5)

**Avoids:**
- **Pitfall #4** (cross-tenant leaks)—composite FKs already in schema, add query filters
- **Pitfall #5** (feature creep)—defer real-time to Phase 5 after validating basic sharing

**Research Flag:** Standard authentication patterns. Skip phase research.

---

### Phase 5: Real-Time Collaboration (SignalR)
**Rationale:** Enhancement to validated sharing workflow. Complex but isolated—doesn't break existing features if delayed.

**Delivers:**
- MealPlanHub with strongly-typed clients
- Real-time broadcast of meal plan/shopping list updates
- Optimistic UI updates (reflect changes immediately)
- Version-based optimistic concurrency for conflict resolution

**Addresses:**
- Real-time collaborative shopping list (core differentiator)

**Implements:**
- SignalR Hub with family groups
- WebSocket connections with automatic reconnect
- Optimistic UI pattern

**Avoids:**
- **Pitfall #3** (mobile network latency)—optimistic updates, extend circuit timeout, reconnect UI
- **Pitfall #1** (memory leaks)—monitor active circuits, circuit disposal on timeout
- **Pitfall #6** (unhandled exceptions)—error boundaries, circuit handlers

**Research Flag:** **Needs phase research** for conflict resolution strategies (operational transformation, CRDTs, version vectors).

---

### Phase 6: Recipe Scraping
**Rationale:** Most complex, highest failure risk. Builds on recipes existing. Can ship MVP without this (manual entry fallback).

**Delivers:**
- Background scraper service with queue-based processing
- Schema.org JSON-LD extraction (AngleSharp)
- CSS selector fallback for non-compliant sites
- Image download and storage to ZFS
- Graceful degradation to manual entry on failure

**Addresses:**
- Recipe URL import (table stakes)

**Uses:**
- HtmlAgilityPack / AngleSharp for HTML parsing
- Background service with Channels for job queue

**Avoids:**
- **Pitfall #2** (scraping reliability collapse)—monitor success rates, graceful degradation, queue retries
- **Pitfall #6** (unhandled exceptions)—scraper failures isolated, don't crash circuits

**Research Flag:** **Needs phase research** for anti-bot mitigation strategies, managed scraping infrastructure, LLM-based extraction as fallback.

---

### Phase 7: Mobile Optimization & PWA
**Rationale:** Polish phase requires stable core features. Mobile-first design throughout, but final optimization after workflow proven.

**Delivers:**
- Service worker configuration for offline UI shell
- Responsive CSS refinement (touch targets 44-48px minimum)
- Virtualization for long lists (Blazor Virtualize)
- Touch gesture support for drag-drop
- Offline fallback page ("You're offline")

**Addresses:**
- Mobile-first responsive design (table stakes)—final polish
- Offline-first mobile UX (differentiator)—partial (Blazor Server limitation)

**Uses:**
- Service workers for static asset caching
- Blazor Virtualize component
- Touch event handling via JS interop

**Avoids:**
- **Pitfall #7** (mobile UX friction)—touch targets, virtualization, TTI < 3s
- **Pitfall #3** (mobile latency)—test on Slow 3G, optimize bundle size

**Research Flag:** Standard PWA patterns. Skip phase research.

---

### Phase 8: Drag-Drop UX Enhancement
**Rationale:** UX polish, depends on meal plan existing. Nice-to-have after core workflow validated.

**Delivers:**
- Drag-drop meal planner component (desktop + mobile touch support)
- Swap meals between days
- Visual feedback for drag operations

**Addresses:**
- Meal moving/swapping (differentiator)—complete implementation

**Uses:**
- MudBlazor drag-drop or JS interop with touch support
- SignalR for real-time updates on drag-drop

**Avoids:**
- **Pitfall #7** (mobile UX friction)—ensure touch events work, not just mouse

**Research Flag:** Standard patterns. Skip phase research.

---

### Phase Ordering Rationale

1. **Dependencies flow bottom-up**: Data layer → Recipes → Meal plans → Shopping lists → Collaboration → Polish
2. **Risk isolation**: Complex features (scraping, real-time) deferred until core workflow proven
3. **Value delivery**: Each phase delivers user-visible features, enabling incremental validation
4. **Pitfall avoidance**: Composite FKs and disposal patterns established in Phase 1 before data exists

**Recommended MVP scope**: Phases 1-4 (data, recipes, meal plans, shopping lists, basic sharing). Test core workflow hypothesis before investing in real-time (Phase 5) or scraping (Phase 6).

### Research Flags

**Phases needing deeper research during planning:**
- **Phase 3 (Shopping List)**: Ingredient parsing/matching strategies, NLP libraries, semantic similarity approaches, unit conversion
- **Phase 5 (Real-Time)**: Conflict resolution patterns (operational transformation vs CRDTs), optimistic concurrency implementation
- **Phase 6 (Recipe Scraping)**: Anti-bot mitigation, managed scraping infrastructure (ScrapingBee, BrightData), LLM-based extraction fallback

**Phases with standard patterns (skip research-phase):**
- **Phase 1**: Standard EF Core CRUD, well-documented
- **Phase 2**: Standard Blazor component patterns
- **Phase 4**: ASP.NET Core authentication, established patterns
- **Phase 7**: PWA service workers, standard mobile optimization
- **Phase 8**: Drag-drop libraries with documentation

## Confidence Assessment

| Area | Confidence | Notes |
|------|------------|-------|
| Stack | HIGH | Verified with official Microsoft docs, NuGet package versions checked, production-ready stack with LTS support |
| Features | MEDIUM | Based on competitor analysis and domain research; table stakes well-established, differentiators inferred from market gaps |
| Architecture | HIGH | Standard Blazor Server patterns documented by Microsoft, DbContextFactory pattern verified, SignalR architecture proven |
| Pitfalls | HIGH (Blazor/SignalR), MEDIUM (domain) | Technical pitfalls verified with official docs and production case studies; domain-specific pitfalls inferred from app reviews and research |

**Overall confidence:** HIGH

### Gaps to Address

**Ingredient consolidation strategy**: Research identifies need for NLP/semantic matching but doesn't specify implementation. During Phase 3 planning, investigate:
- NLP libraries for ingredient parsing (.NET ML, spaCy via Python interop)
- Semantic similarity approaches (word embeddings, fuzzy matching)
- Unit conversion libraries (UnitsNet)
- Validation: test with real recipe data to measure accuracy

**Recipe scraping anti-bot mitigation**: Research notes anti-bot detection is sophisticated in 2026 but doesn't detail specific tools. During Phase 6 planning, investigate:
- Managed scraping services (ScrapingBee, BrightData) vs self-hosted
- LLM-based extraction (GPT-4, Claude) as fallback for brittle selectors
- Rate limiting strategies to avoid IP bans
- Validation: test with top 20 recipe sites (AllRecipes, Food Network, etc.)

**Conflict resolution for real-time collaboration**: Research identifies need for versioning but doesn't specify algorithm. During Phase 5 planning, investigate:
- Operational Transformation vs CRDTs for shopping list items
- Last-write-wins vs merge strategies for meal plan updates
- Version vector implementation in PostgreSQL
- Validation: simulate concurrent edits, test conflict scenarios

**Offline-first limitations**: Blazor Server requires active SignalR connection; true offline only possible with Blazor WebAssembly. Phase 7 may need to evaluate hybrid approach (WASM for shopping list page only) if user testing reveals offline shopping is critical. Monitor this during MVP validation.

## Sources

### Primary (HIGH confidence)
- Microsoft Learn: ASP.NET Core Blazor documentation, SignalR guidance, EF Core PostgreSQL integration
- NuGet Gallery: Package versions verified (Npgsql 8.0.10, MudBlazor 7.21.0, FluentValidation 11.11.0)
- schema.org: Recipe structured data specification for scraping strategy
- PostgreSQL docs: Multi-tenant database design with composite keys

### Secondary (MEDIUM confidence)
- Competitor analysis: Plan to Eat, BigOven Pro, Ollie, Mealime feature comparisons
- Recipe scraping research: Web scraping challenges in 2026, anti-bot detection patterns
- Blazor community: Performance best practices, circuit memory management patterns
- Mobile UX research: Touch target sizing, meal prep app filter patterns

### Tertiary (LOW confidence)
- Domain adoption barriers: "Why don't people use meal planning apps" (identifies friction points but small sample size)
- AI meal planning trends: 2026 market reports (directional but not validated with users)

---
*Research completed: 2026-01-22*
*Ready for roadmap: yes*
