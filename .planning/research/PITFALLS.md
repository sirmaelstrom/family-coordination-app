# Pitfalls Research

**Domain:** Family Meal Planning and Coordination Apps
**Researched:** 2026-01-22
**Confidence:** HIGH (Blazor/SignalR), MEDIUM (Domain-specific), MEDIUM (Recipe scraping)

## Critical Pitfalls

### Pitfall 1: SignalR Circuit Memory Leaks

**What goes wrong:**
Circuit state stays in memory indefinitely, causing "Blazor Server eats all RAM" syndrome. Each user session creates a circuit that holds state in server memory. When circuits don't get disposed, memory grows with every user and never shrinks. Small per-user objects (lists, view models) across thousands of circuits slowly consume more RAM.

**Why it happens:**
When ComponentActivator is a singleton, transient disposable services created by it are tracked in the root scope of the DI container, preventing garbage collection until root scope is disposed. Blazor keeps disconnected circuits in memory for 3 minutes (default) to enable reconnect, delaying collection. Developers forget to implement IDisposable or IAsyncDisposable for components that subscribe to events or hold resources.

**How to avoid:**
- Implement IDisposable/IAsyncDisposable for all components subscribing to events or holding state
- Use scoped services instead of singleton ComponentActivator
- Configure circuit retention periods based on expected reconnect patterns
- Monitor memory usage and circuit count in production
- Avoid storing large objects in component state (use database/cache)
- Unsubscribe from events in Dispose methods

**Warning signs:**
- Memory usage steadily increases over time without declining
- Server becomes unresponsive after many users have connected
- Out of memory exceptions after app has been running for hours/days
- Memory dumps show thousands of component instances still in memory

**Phase to address:**
Phase 1 (Foundation) - proper disposal patterns must be established from the start. Phase 2 (Testing/Production Deployment) - memory profiling and monitoring before production.

---

### Pitfall 2: Recipe Scraping Reliability Collapse

**What goes wrong:**
Recipe import feature works in development but silently fails or produces garbage data in production. Website structure changes break selectors without warning. Anti-bot detection blocks requests. Dynamic content loaded via AJAX doesn't appear in initial page source. Users lose confidence in the feature and resort to manual entry exclusively.

**Why it happens:**
Modern websites (2026) have sophisticated anti-bot detection tracking browsing patterns, mouse movements, and typing behavior. Sites load content dynamically through AJAX calls, not serving complete HTML. Websites change structure frequently without notice, breaking brittle CSS selectors. Silent failures occur - system isn't blocked, it just scrapes nothing useful.

**How to avoid:**
- Use managed scraping infrastructure with rotation (not simple HttpClient)
- Implement AI-native extraction (LLM-based) as fallback to brittle selectors
- Design for failure: graceful degradation to manual entry with partial data preserved
- Monitor scraping success rates per domain in production
- Queue retries with exponential backoff for transient failures
- Cache successful parse patterns per domain, detect when they break
- Let users submit corrections/examples for failed sites

**Warning signs:**
- Scraping success rate declining over weeks/months
- Increasing user reports of "didn't work" for specific sites
- Empty or partial recipe data with no error message to user
- Timeout exceptions increasing in logs
- Specific popular recipe sites consistently failing

**Phase to address:**
Phase 1 (Core Features) - must ship with resilient scraping or it won't be adopted. Phase 3 (Monitoring) - scraping health dashboard and alerts. Consider Phase 2 fallback to LLM-based extraction if brittle selectors fail frequently.

---

### Pitfall 3: Mobile Network Latency Breaking Real-Time Collaboration

**What goes wrong:**
Blazor Server is network latency-dependent. Noticeable lag starts around 100ms, and at 200ms the app breaks due to SignalR data requests coming out of sync. At the grocery store with spotty WiFi or cellular, every interaction feels sluggish. Users tap items to check off, nothing happens, they tap again, then both taps register when connection improves. Race conditions cause conflicting updates when multiple users edit shopping list simultaneously.

**Why it happens:**
Blazor Server renders on server and sends DOM updates via SignalR. Every user interaction requires round-trip to server. Mobile networks have variable latency (50-300ms typical, worse in stores). SignalR WebSocket connections drop when device sleeps or switches networks. Circuits timeout if reconnection takes too long (default 3 minutes).

**How to avoid:**
- Implement optimistic UI updates (reflect user action immediately, reconcile with server async)
- Use version-based optimistic concurrency for collaborative editing (track userVersion/serverVersion)
- Extend circuit timeout for mobile scenarios (default too aggressive)
- Implement proper reconnection UI (not just spinning modal)
- Design "offline-tolerant" patterns: queue mutations locally, sync when connection restored
- Test on throttled network (Chrome DevTools network throttling: Slow 3G)
- Consider Blazor WebAssembly hybrid for shopping list page only (offline-capable)

**Warning signs:**
- User complaints about "slow" or "laggy" on mobile but fine on desktop
- Increased SignalR reconnection events in logs during peak usage
- Users reporting duplicate check-offs or missed taps
- Timeouts and circuit disposal during shopping trips
- Multiple users editing same list report conflicts/lost changes

**Phase to address:**
Phase 1 (Core Features) - must address before shipping real-time shopping list. Phase 2 (Testing) - mobile network simulation testing required. Phase 4 (Optimization) - may need hybrid WASM approach if latency unacceptable.

---

### Pitfall 4: Missing Composite Foreign Keys Breaking Multi-Tenant Isolation

**What goes wrong:**
Single-tenant MVP ships with HouseholdId column on every table but no database-level enforcement. When expanding to multi-tenant, application-level filtering (WHERE household_id = X) is the only protection. Inexperienced developer forgets filter in one query and accidentally exposes Household A's data to Household B. Catastrophic security breach with wide blast radius.

**Why it happens:**
Developers add HouseholdId to schema for "future-proofing" but don't enforce at database level. Foreign keys reference only the entity ID, not (household_id, id). Application relies on WHERE clause filters in every query. Manual filtering without middleware/helpers makes mistakes easy. Security vulnerability isn't detected until production breach.

**How to avoid:**
- Use composite primary keys: PRIMARY KEY (household_id, id) on all tenant-scoped tables
- Use composite foreign keys: FOREIGN KEY (household_id, recipe_id) REFERENCES recipes (household_id, id)
- Database-level enforcement prevents cross-tenant data leaks regardless of application bugs
- Denormalize: include household_id on every table even if "technically" not normalized
- Implement query middleware/filters that automatically add household_id clause
- Use Row-Level Security (PostgreSQL RLS) as additional safeguard

**Warning signs:**
- Foreign keys only reference entity ID, not including household_id
- No database constraints preventing cross-tenant references
- Application code manually adds WHERE household_id everywhere
- No middleware/filter layer enforcing tenant isolation
- Tests don't verify cross-tenant data isolation

**Phase to address:**
Phase 1 (Foundation) - must be in initial schema design before any data exists. Retrofitting composite keys after data is created is painful migration. Phase 2 (Security Testing) - verify cross-tenant isolation with tests.

---

### Pitfall 5: Feature Creep Killing MVP Momentum

**What goes wrong:**
Project starts with core workflow (recipes → meal plan → shopping list) but accumulates "good ideas" during development. Calendar integration, pantry tracking, recipe ratings, meal templates, nutritional analysis - each seems reasonable. Timeline stretches from 6 weeks to 6 months. Family never sees working version, loses interest. App ships with too many half-baked features instead of polished core.

**Why it happens:**
Fear that "nobody will use it without this feature." Good ideas added at the wrong time. No clear definition of "done" for MVP. Scope creep happens when roadmap has undefined boundaries. Each unplanned feature increases development cost, testing effort, and delivery time. Developer excitement about new capabilities overrides shipping discipline.

**How to avoid:**
- Define MVP scope as "must-haves for release goal" - write it down, defend it
- Defer "nice-to-haves" to Phase 2 explicitly in PROJECT.md
- Implement MoSCoW prioritization: Must have, Should have, Could have, Won't have
- Ship MVP first, add advanced features after real users validate product
- Set hard deadline (even artificial) to force scope discipline
- Track feature additions: any new requirement must justify delaying ship date
- Remember: most apps fail because they start with development, not validation

**Warning signs:**
- Roadmap growing instead of shrinking as development progresses
- "Just one more feature" conversations happening weekly
- Original 6-week estimate now 3-4 months with no ship date
- Features partially implemented, none fully polished
- Team solving edge cases instead of core functionality
- No user has seen working version yet

**Phase to address:**
Phase 0 (Planning) - define hard scope boundaries in PROJECT.md before starting. During execution - ruthless prioritization, defer anything not blocking family adoption.

---

### Pitfall 6: Unhandled Exceptions Crashing Circuits Without Recovery

**What goes wrong:**
Unhandled exception in Blazor Server component crashes the SignalR circuit. User sees "Disconnected" modal or frozen UI. Refreshing page loses all unsaved work. If this happens while editing shopping list at grocery store, user loses trust in app reliability. Multiple crashes lead to abandonment.

**Why it happens:**
Blazor Server's single-threaded circuit model means unhandled exceptions kill the entire session. Developers don't wrap risky operations in try-catch. No global error boundary to catch component exceptions. Recipe scraping failures, database timeouts, SignalR disconnects throw exceptions that bubble up. User sees generic error, not actionable message.

**How to avoid:**
- Implement global error boundary with ErrorBoundary component
- Wrap all async operations in try-catch with user-friendly messages
- Use circuit handlers to intercept exceptions before circuit dies
- Implement retry logic for transient failures (database, network)
- Preserve component state in external store (not just in-memory) for recovery
- Log exceptions with context (user, action, component) for debugging
- Test exception scenarios: network failure, database timeout, scraping failure

**Warning signs:**
- Users reporting "page froze" or "had to refresh"
- Increasing circuit disposal events in logs
- Exception logs without user-visible error messages
- Lost shopping list edits reported by users
- No graceful degradation when external services fail

**Phase to address:**
Phase 1 (Foundation) - error boundaries and exception handling from start. Phase 2 (Resilience) - circuit handlers and state persistence. Phase 3 (Production Hardening) - monitoring and alerting.

---

### Pitfall 7: Mobile UX Friction Causing Abandonment at Grocery Store

**What goes wrong:**
App works fine on desktop but fails mobile use case. Touch targets too small (< 44px), users mis-tap items. Initial load takes 5+ seconds on 3G, users give up. Horizontal scrolling hijacks vertical scroll (Kroger anti-pattern). Long lists without virtualization cause performance issues. Users revert to text messages instead.

**Why it happens:**
84% of mobile apps struggle with properly sized/spaced touch interfaces. Desktop-first design ported to mobile. Blazor Server initial load includes full app download. No performance testing on slow networks or low-end devices. Recipe lists, shopping lists render all items (no virtualization). Developers test on high-end devices with fast WiFi.

**How to avoid:**
- Touch targets minimum 44-48px (WHO: 9mm on each side)
- Implement virtualization for long lists (Blazor Virtualize component)
- Test on throttled network: Chrome DevTools "Slow 3G" setting
- Optimize initial load: code splitting, lazy loading, prerendering
- Avoid horizontal scroll components that hijack vertical scroll
- Test on actual low-end Android device, not just emulator
- Monitor Time to Interactive (TTI) - pages > 3s see 32% bounce rate, 5s = 90%

**Warning signs:**
- Users complaining about "slow" on mobile but not desktop
- Reports of accidentally tapping wrong items
- Bounce rate high on mobile vs desktop
- Users not using app at grocery store (defeats core use case)
- Performance metrics showing slow TTI on 3G

**Phase to address:**
Phase 1 (Core Features) - mobile-first design from start, not retrofitted. Phase 2 (Testing) - device/network testing before production. Phase 3 (Optimization) - performance tuning based on real usage.

---

## Technical Debt Patterns

Shortcuts that seem reasonable but create long-term problems.

| Shortcut | Immediate Benefit | Long-term Cost | When Acceptable |
|----------|-------------------|----------------|-----------------|
| Skip composite foreign keys, rely on app-level filtering | Faster initial schema design | Security vulnerability, no database-level tenant isolation, catastrophic breach risk | Never - always use composite FKs for multi-tenant |
| Use simple HttpClient for recipe scraping | Easy implementation, works in development | Brittle, breaks with anti-bot detection, silent failures in production | Only for prototype/demo, not MVP |
| Store all state in circuit memory | Simple state management | Memory leaks, lost data on disconnect, no recovery | Acceptable for transient UI state only, not user data |
| Skip error boundaries | Faster component development | Unhandled exceptions crash entire circuit, poor UX | Never - implement from start |
| Defer mobile testing to "later" | Ship desktop version faster | Mobile UX friction discovered too late, expensive redesign | Never - mobile is primary use case (grocery store) |
| Manual WHERE household_id in every query | No infrastructure setup | Easy to forget, no enforcement, security risk | Only if using Row-Level Security as backup |

## Integration Gotchas

Common mistakes when connecting to external services.

| Integration | Common Mistake | Correct Approach |
|-------------|----------------|------------------|
| Recipe URL scraping | Using brittle CSS selectors only | AI-native extraction as fallback, monitor success rates, graceful degradation to manual |
| Google OAuth | Storing tokens in circuit state | Use ASP.NET Core authentication middleware, persist in secure cookies/database |
| PostgreSQL | Not using connection pooling | Configure Npgsql connection pooling, monitor connection count |
| SignalR backplane (future multi-instance) | No shared state for circuits | Use Redis backplane when scaling beyond single instance |
| External recipe APIs (if added later) | Synchronous calls blocking circuit | Async/await all external calls, implement timeouts and retries |

## Performance Traps

Patterns that work at small scale but fail as usage grows.

| Trap | Symptoms | Prevention | When It Breaks |
|------|----------|------------|----------------|
| Rendering full shopping list (100+ items) without virtualization | Slow render on mobile, high memory | Use Blazor Virtualize component for lists > 20 items | 50+ items on low-end mobile device |
| Keeping all circuits in memory indefinitely | Memory usage grows linearly with users | Implement proper disposal, configure circuit retention | 100+ concurrent users |
| N+1 queries loading recipes with ingredients | Slow meal plan page load | Use EF Core Include() for eager loading | Meal plan with 10+ recipes |
| Sending full recipe data via SignalR on every shopping list update | Message size limit (32KB), slow updates | Send only changed items (delta updates) | Shopping list with 50+ items from complex recipes |
| No database indexes on household_id or foreign keys | Slow queries as data grows | Add indexes on all foreign keys and filter columns | 1000+ recipes, 100+ meal plans |

## Security Mistakes

Domain-specific security issues beyond general web security.

| Mistake | Risk | Prevention |
|---------|------|------------|
| No composite foreign keys for tenant isolation | Cross-tenant data leak (Household A sees Household B's data) | Use composite FKs: (household_id, id), database-level enforcement |
| Storing Google OAuth tokens in circuit state | Token exposure on circuit leak, lost on disconnect | Use ASP.NET Core auth middleware, secure cookie storage |
| No rate limiting on recipe scraping | IP ban from recipe sites, DoS vulnerability | Implement per-user rate limits, exponential backoff |
| No input sanitization on recipe URL | XSS if malicious site injects scripts | Sanitize all scraped content before storing/rendering |
| Email whitelist only in application code | Bypass via direct database manipulation | Enforce in database constraints or use ASP.NET Identity roles |

## UX Pitfalls

Common user experience mistakes in this domain.

| Pitfall | User Impact | Better Approach |
|---------|-------------|-----------------|
| Silent scraping failures | User waits, sees nothing, assumes broken, abandons feature | Show progress, provide feedback, offer manual entry on failure |
| No offline indication for mobile | User taps items, nothing happens, confusion | Clear "reconnecting" UI, queue actions, sync when online |
| Auto-aggregating ingredients without transparency | User doesn't understand why quantities changed, distrust | Show aggregation (3 recipes need onions → 5 total), allow override |
| Forcing category assignment on recipe import | Friction during recipe add, users skip or abandon | Auto-categorize via ML/rules, allow quick edit later |
| No search/filter on large recipe list | Scrolling through 100+ recipes to find one, frustration | Implement search, tags, filters before 20+ recipes |
| Requiring all recipe details upfront | User just wants quick capture, too much work | Support minimal recipe (name + URL), flesh out later |

## "Looks Done But Isn't" Checklist

Things that appear complete but are missing critical pieces.

- [ ] **Real-time shopping list:** Often missing optimistic UI updates - verify taps respond immediately, not waiting for server
- [ ] **Recipe scraping:** Often missing failure handling - verify graceful degradation to manual entry with partial data preserved
- [ ] **Mobile responsiveness:** Often missing touch target sizes - verify 44-48px minimum on actual mobile device
- [ ] **SignalR reconnection:** Often missing reconnect UI - verify user sees status and can recover without refresh
- [ ] **Multi-tenant design:** Often missing composite foreign keys - verify database enforces household_id isolation
- [ ] **Error handling:** Often missing error boundaries - verify unhandled exceptions don't crash circuit
- [ ] **Memory management:** Often missing component disposal - verify IDisposable implemented for event subscriptions
- [ ] **List performance:** Often missing virtualization - verify smooth scroll on 100+ item list on mobile
- [ ] **Network resilience:** Often missing timeout/retry - verify app handles slow 3G without hanging
- [ ] **Cross-tenant security:** Often missing database-level enforcement - verify queries can't access other household's data

## Recovery Strategies

When pitfalls occur despite prevention, how to recover.

| Pitfall | Recovery Cost | Recovery Steps |
|---------|---------------|----------------|
| Memory leaks from missing disposal | LOW | Add IDisposable/IAsyncDisposable to components, implement Dispose methods, restart app |
| Recipe scraping broken for popular site | MEDIUM | Add site-specific parser, update selectors, cache working patterns, notify users of fix |
| Cross-tenant data leak from missing WHERE clause | HIGH | Audit all queries, add composite foreign keys, migrate data, notify affected users, security review |
| Mobile UX too slow | MEDIUM | Implement virtualization, optimize bundle size, add loading states, consider WASM hybrid |
| Missing error boundaries causing crashes | LOW | Add ErrorBoundary components, wrap risky operations in try-catch, deploy update |
| SignalR circuit timeout at grocery store | LOW | Increase circuit retention config, improve reconnect UI, add optimistic updates |

## Pitfall-to-Phase Mapping

How roadmap phases should address these pitfalls.

| Pitfall | Prevention Phase | Verification |
|---------|------------------|--------------|
| Circuit memory leaks | Phase 1 (Foundation) | Memory profiling during dev, load testing before production |
| Recipe scraping reliability | Phase 1 (Core Features) | Monitor success rates per domain, user feedback |
| Mobile network latency | Phase 1 (Core Features) | Test on throttled network (Slow 3G), measure TTI |
| Missing composite FKs | Phase 1 (Foundation) | Database schema review, cross-tenant access tests |
| Feature creep | Phase 0 (Planning) | Scope document in PROJECT.md, ruthless prioritization |
| Unhandled exceptions | Phase 1 (Foundation) | Exception monitoring, circuit survival tests |
| Mobile UX friction | Phase 1 (Core Features) | Device testing, touch target audit, TTI < 3s |
| No virtualization | Phase 1 (Core Features) | Performance testing with 100+ item lists |
| Silent scraping failures | Phase 1 (Core Features) | Error message UX review, manual entry fallback |
| Missing reconnect UI | Phase 1 (Core Features) | Network disconnect simulation, user testing |

## Sources

**Blazor Server / SignalR:**
- [ASP.NET Core Blazor SignalR guidance | Microsoft Learn](https://learn.microsoft.com/en-us/aspnet/core/blazor/fundamentals/signalr?view=aspnetcore-10.0)
- [Common Pitfalls to Avoid When Using SignalR in Blazor](https://www.thetechplatform.com/post/common-pitfalls-to-avoid-when-using-signalr-in-blazor)
- [How to Deploy Blazor Server in Production & Fix SignalR Reconnection Issues](https://www.codestudy.net/blog/how-to-deploy-blazor-server-in-production-and-overcome-signalr-re-connection-problems/)
- [Blazor Server Memory Management: Stop Circuit Leaks](https://amarozka.dev/blazor-server-memory-management-circuit-leaks/)
- [Manage memory in deployed ASP.NET Core server-side Blazor apps | Microsoft Learn](https://learn.microsoft.com/en-us/aspnet/core/blazor/host-and-deploy/server/memory-management?view=aspnetcore-10.0)
- [Real-Time Collaborative Editing in Blazor Diagram with SignalR and Redis | Syncfusion Blogs](https://www.syncfusion.com/blogs/post/collaborative-editing-in-blazor-diagram)

**Recipe Scraping:**
- [State of Web Scraping 2026: Trends, Challenges & What's Next](https://www.browserless.io/blog/state-of-web-scraping-2026)
- [9 Web Scraping Challenges and How to Solve Them | Octoparse](https://www.octoparse.com/blog/9-web-scraping-challenges)
- [Top Web Scraping Challenges in 2025 | ScrapingBee](https://www.scrapingbee.com/blog/web-scraping-challenges/)
- [6 Web Scraping Challenges & Practical Solutions in 2026](https://research.aimultiple.com/web-scraping-challenges/)

**Mobile Performance:**
- [Improve the load time performance of Blazor WebAssembly apps on low-end mobile devices · Issue #42284 · dotnet/aspnetcore](https://github.com/dotnet/aspnetcore/issues/42284)
- [ASP.NET Core Blazor performance best practices | Microsoft Learn](https://learn.microsoft.com/en-us/aspnet/core/blazor/performance/?view=aspnetcore-8.0)
- [Top 17 UX Mistakes in Mobile App Design & How to Dodge Them](https://www.cigen.io/insights/top-17-ux-mistakes-in-mobile-app-design-and-how-to-dodge-them)
- [How To Create A Winning Grocery App Design? Best Practices To Know](https://www.magenative.com/blog/grocery-app-design/)

**Multi-Tenant Database:**
- [Designing Your Postgres Database for Multi-tenancy | Crunchy Data Blog](https://www.crunchydata.com/blog/designing-your-postgres-database-for-multi-tenancy)
- [Why Your Multi-Tenant Database Design is Probably Wrong (And How to Fix It Before It's Too Late) | by Harishsingh | Medium](https://medium.com/@harishsingh8529/why-your-multi-tenant-database-design-is-probably-wrong-and-how-to-fix-it-before-its-too-late-c543b777106a)

**MVP Scope / Feature Creep:**
- [How to Prevent & Manage Scope Creep in MVP: A Complete Guide](https://imaginovation.net/blog/prevent-scope-creep-mvp-development/)
- [Feature Creep: Why 'Just One More Feature' Is Killing Your SaaS](https://wearepresta.com/why-just-one-more-feature-is-killing-your-product-roadmap/)
- [Common Mistakes to Avoid in MVP Development](https://www.tresastronautas.com/en/blog/common-mistakes-in-mvp-development-essential-tips-for-success)

**Meal Planning Apps:**
- [Why don't more people use meal planning apps?](https://ohapotato.app/potato-files/why-dont-more-people-use-meal-planning-apps)
- [Barriers to and Facilitators for Using Nutrition Apps: Systematic Review and Conceptual Framework - PMC](https://pmc.ncbi.nlm.nih.gov/articles/PMC8409150/)
- [Mobile Apps to Support Healthy Family Food Provision: Systematic Assessment of Popular, Commercially Available Apps - PMC](https://pmc.ncbi.nlm.nih.gov/articles/PMC6320405/)

---
*Pitfalls research for: Family Meal Planning and Coordination Apps*
*Researched: 2026-01-22*
