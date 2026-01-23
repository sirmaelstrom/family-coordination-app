# Stack Research

**Domain:** Family Meal Planning and Coordination Web Application
**Researched:** 2026-01-22
**Confidence:** HIGH

## Recommended Stack

### Core Technologies

| Technology | Version | Purpose | Why Recommended |
|------------|---------|---------|-----------------|
| .NET | 8.0 | Application framework | LTS support through November 2026, mature ecosystem, excellent performance |
| Blazor Server | 8.0 | UI framework | Native real-time capabilities via SignalR, component-based architecture, server-side rendering reduces client payload |
| ASP.NET Core | 8.0 | Web framework | Built-in SignalR support, minimal APIs for future REST endpoints, production-ready |
| PostgreSQL | 16+ | Primary database | Advanced data types (arrays, JSON), open-source, excellent .NET support via Npgsql |
| Entity Framework Core | 8.0 | ORM | Type-safe queries, migrations, PostgreSQL-specific features (arrays, JSON columns) |
| Npgsql.EntityFrameworkCore.PostgreSQL | 8.0.10 | PostgreSQL provider | Official EF Core provider for PostgreSQL with full .NET 8 compatibility |
| SignalR | 8.0 (built-in) | Real-time communication | Native Blazor Server integration, WebSocket support with fallback, automatic reconnection |
| Docker | 24+ | Containerization | Consistent deployment, easy scaling, works well with .NET 8 multi-stage builds |

### Supporting Libraries

| Library | Version | Purpose | When to Use |
|---------|---------|---------|-------------|
| **Web Scraping & Parsing** | | | |
| HtmlAgilityPack | 1.12.4 | HTML parsing | Recipe URL import - most popular (83M downloads), handles malformed HTML |
| AngleSharp | 1.4.0 | HTML/CSS parsing | Advanced CSS selectors for recipe extraction, modern async API, W3C compliant |
| **Validation** | | | |
| FluentValidation | 11.11.0 | Business logic validation | Complex validation rules for recipes, meal plans, shopping lists |
| Blazored.FluentValidation | 2.2.0 | Blazor forms integration | Client-side validation in EditForms, real-time feedback |
| **Logging & Monitoring** | | | |
| Serilog.AspNetCore | 10.0.0 | Structured logging | Production diagnostics, structured log data for analysis |
| Serilog.Sinks.PostgreSQL | 2.4.0 | Database logging sink | Persist logs to PostgreSQL for centralized monitoring |
| **UI Component Framework** | | | |
| MudBlazor | 7.21.0 | Material Design components | Mobile-first responsive design, 100% C#, comprehensive component library |
| Blazorise | 1.8.0 | Multi-framework UI components | Alternative: flexible theming, Bootstrap/Material support |
| **State Management** | | | |
| Fluxor | 6.2.0 | State management | Complex app state, undo/redo for meal planning, centralized state |
| **Testing** | | | |
| bUnit | 1.33.0 | Blazor component testing | Unit test Blazor components in isolation |
| Testcontainers | 4.8.0 | Integration testing | Spin up PostgreSQL containers for integration tests |

### Development Tools

| Tool | Purpose | Notes |
|------|---------|-------|
| Docker Compose | Multi-container orchestration | PostgreSQL + app in local dev, production deployment |
| EF Core Tools | Database migrations | `dotnet ef` CLI for schema management |
| Rider / VS 2022 | IDE | Full Blazor debugging, hot reload support |
| pgAdmin | PostgreSQL management | Database administration and query optimization |

## Installation

### Core Packages
```bash
# Core framework (implicit with .NET 8 SDK)
dotnet new blazorserver -n FamilyCoordinationApp

# Database
dotnet add package Npgsql.EntityFrameworkCore.PostgreSQL --version 8.0.10
dotnet add package Microsoft.EntityFrameworkCore.Design --version 8.0.11

# Web scraping
dotnet add package HtmlAgilityPack --version 1.12.4
dotnet add package AngleSharp --version 1.4.0

# Validation
dotnet add package FluentValidation --version 11.11.0
dotnet add package Blazored.FluentValidation --version 2.2.0
```

### Supporting Libraries
```bash
# UI components
dotnet add package MudBlazor --version 7.21.0

# Logging
dotnet add package Serilog.AspNetCore --version 10.0.0
dotnet add package Serilog.Sinks.PostgreSQL --version 2.4.0

# State management (optional, for complex scenarios)
dotnet add package Fluxor.Blazor.Web --version 6.2.0
```

### Dev Dependencies
```bash
# Testing
dotnet add package bUnit --version 1.33.0
dotnet add package Testcontainers.PostgreSql --version 4.8.0
dotnet add package xUnit --version 2.9.2
dotnet add package FluentAssertions --version 6.12.2
```

## Alternatives Considered

| Recommended | Alternative | When to Use Alternative |
|-------------|-------------|-------------------------|
| Blazor Server | Blazor WebAssembly | If true offline-first is critical (not recommended for this app due to real-time requirements) |
| Blazor Server | MAUI Blazor Hybrid | If native mobile app features needed (camera, GPS, push notifications) |
| HtmlAgilityPack | Puppeteer Sharp | If JavaScript rendering required (rare for recipe sites with schema.org markup) |
| MudBlazor | Radzen Blazor | If data-grid heavy admin interfaces are primary use case |
| MudBlazor | Blazorise | If multi-framework theme support (Bootstrap/Tailwind) is required |
| PostgreSQL | SQL Server | If Windows-only deployment or existing SQL Server infrastructure |
| Fluxor | Custom service | For simple apps, a singleton notification service is sufficient |

## What NOT to Use

| Avoid | Why | Use Instead |
|-------|-----|-------------|
| Blazor Server with PWA offline | Blazor Server requires constant SignalR connection; offline PWA only works with WebAssembly | Accept online-only for Blazor Server or switch to hybrid architecture |
| IronWebScraper | Commercial license required, overkill for recipe scraping | HtmlAgilityPack (free, proven) + schema.org JSON-LD parsing |
| Selenium for recipe scraping | Heavy browser automation, slow, resource-intensive | HtmlAgilityPack for static HTML, Puppeteer Sharp only if dynamic JS rendering required |
| NScrape | Unmaintained (last update 2015), .NET Framework only | HtmlAgilityPack or AngleSharp |
| JavaScript interop for state | Performance overhead, breaks server prerendering, adds complexity | Native C# state management (scoped services, Fluxor for complex cases) |
| Repository pattern over EF Core | Over-abstraction hides LINQ power, adds unnecessary layers | Use DbContext directly; add repositories only for testing isolation or multi-store scenarios |
| Blazor WebAssembly for real-time | Requires separate SignalR hub setup, larger initial payload, more complex deployment | Blazor Server (SignalR built-in, simpler architecture) |

## Stack Patterns by Variant

### Recipe Import Strategy
**If most recipe sites use schema.org/Recipe markup (common in 2026):**
- Use AngleSharp to parse HTML
- Extract JSON-LD `<script type="application/ld+json">` tags
- Deserialize to Recipe schema objects
- Fallback to CSS selectors for non-compliant sites
- Because: Most major recipe sites (AllRecipes, Food Network, etc.) use schema.org for SEO

**If dealing with JavaScript-rendered recipe sites:**
- Use Puppeteer Sharp for browser automation
- Headless Chrome renders JavaScript
- Extract rendered HTML then parse with AngleSharp
- Because: Some modern recipe sites use React/Vue SPAs

### State Management Pattern
**For simple app (1-2 editors, basic collaboration):**
- Use scoped DI services for component state
- SignalR hub broadcasts change notifications
- Components call StateHasChanged() on updates
- Because: Blazor Server already runs over SignalR; no need for separate hub

**For complex app (many editors, conflict resolution, undo/redo):**
- Use Fluxor for centralized state
- Implement action/reducer pattern
- Version tracking for collaborative editing
- Because: Fluxor provides time-travel debugging, predictable state updates

### Mobile Optimization Pattern
**For grocery store use (mobile-first critical):**
- MudBlazor for responsive Material Design components
- Large touch targets (minimum 44x44px)
- Minimal JavaScript, server-rendered
- Fast SignalR reconnection on network changes
- Because: Users need reliable access in stores with spotty WiFi

**For primarily desktop use:**
- Radzen Blazor for data-grid-heavy interfaces
- Complex table filtering/sorting
- Multi-panel layouts
- Because: Desktop affords more complex UI patterns

## Version Compatibility

| Package A | Compatible With | Notes |
|-----------|-----------------|-------|
| Npgsql.EntityFrameworkCore.PostgreSQL 8.0.10 | EF Core 8.0.0+ | Do not use with EF Core 9+ (use Npgsql 9.x instead) |
| MudBlazor 7.x | .NET 8 | Fully compatible; v7 targets .NET 8 |
| Blazored.FluentValidation 2.2.0 | FluentValidation 11.x | Requires FluentValidation >= 11.0 |
| Serilog.AspNetCore 10.0.0 | .NET 8 | Supports .NET 8.0, .NET Standard 2.0 |
| bUnit 1.33.0 | .NET 8 Blazor | Compatible with Blazor Server .NET 8 |
| Testcontainers.PostgreSql 4.8.0 | Docker 20+ | Requires Docker Desktop or Docker daemon |
| AngleSharp 1.4.0 | .NET 8 | Targets .NET 8.0, .NET Standard 2.0 |

## Recipe Scraping Implementation Strategy

### Schema.org JSON-LD Approach (Recommended)
```csharp
// 1. Fetch HTML with HttpClient
// 2. Parse with AngleSharp
// 3. Extract <script type="application/ld+json"> tags
// 4. Deserialize JSON to schema.org Recipe object
// 5. Map to domain Recipe entity
```

**Libraries:**
- `AngleSharp` for HTML parsing
- `System.Text.Json` for JSON-LD deserialization

**Advantages:**
- Fast (no browser automation)
- Reliable (standard schema.org format)
- Low resource usage
- Works for 80%+ of recipe sites in 2026

### Fallback CSS Selector Approach
For sites without schema.org markup:
- Use HtmlAgilityPack with CSS selector mapping
- Maintain site-specific selector configurations
- Regex patterns for ingredient parsing

**When to use:**
- Schema.org extraction fails
- Small/personal recipe blogs
- Non-standard recipe formats

### Browser Automation (Last Resort)
Only if JavaScript rendering is absolutely required:
- Puppeteer Sharp (Chromium automation)
- Playwright (multi-browser support)

**Warning:** Heavy resource usage, slower, requires Chromium installation

## PostgreSQL-Specific Patterns

### Array Columns for Tags
```csharp
// Recipe tags stored as PostgreSQL array
public string[] Tags { get; set; }  // Maps to text[] in PostgreSQL
```

### JSON Columns for Flexible Data
```csharp
// Store recipe metadata as JSON column
public RecipeMetadata Metadata { get; set; }  // Maps to jsonb in PostgreSQL
```

**Use Npgsql's native support:**
- Arrays: `HasPostgresArrayConversion()` in EF Core
- JSON: `ToJson()` fluent API (EF Core 8)

## Blazor Server Real-Time Patterns

### Pattern 1: Shared Notification Service (Simple)
**For:** Server-originated events within Blazor app
```csharp
// Singleton service that components subscribe to
// Background task pushes events into service
// Components call StateHasChanged() on notification
```
**Why:** Blazor Server already uses SignalR; no separate hub needed

### Pattern 2: SignalR Hub + Redis (Complex)
**For:** Multi-instance deployments, version conflict resolution
```csharp
// SignalR hub receives updates
// Redis stores current version + state
// Version check before applying changes
// Broadcast to all collaborators on success
```
**Why:** Enables horizontal scaling, conflict detection

### Pattern 3: Automatic Reconnection
```csharp
// Configure in Program.cs
builder.Services.AddServerSideBlazor()
    .AddHubOptions(options =>
    {
        options.ClientTimeoutInterval = TimeSpan.FromSeconds(60);
        options.HandshakeTimeout = TimeSpan.FromSeconds(30);
    })
    .AddCircuitOptions(options =>
    {
        options.DetailedErrors = isDevelopment;
    });
```

**Enable WebSocket SkipNegotiation:**
- WebSockets preferred for lowest latency
- Automatic reconnect for transient network issues
- Remove SkipNegotiation if WebSockets unavailable (allows SSE/Long Polling fallback)

## Docker Deployment Configuration

### Multi-Stage Dockerfile (.NET 8)
```dockerfile
# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["FamilyCoordinationApp.csproj", "./"]
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /app/publish

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 80
ENTRYPOINT ["dotnet", "FamilyCoordinationApp.dll"]
```

### Docker Compose (PostgreSQL + App)
```yaml
services:
  postgres:
    image: postgres:16-alpine
    environment:
      POSTGRES_DB: family_coordination
      POSTGRES_USER: appuser
      POSTGRES_PASSWORD: ${DB_PASSWORD}
    volumes:
      - postgres_data:/var/lib/postgresql/data
    ports:
      - "5432:5432"

  web:
    build: .
    depends_on:
      - postgres
    environment:
      ConnectionStrings__DefaultConnection: "Host=postgres;Database=family_coordination;Username=appuser;Password=${DB_PASSWORD}"
    ports:
      - "8080:80"

volumes:
  postgres_data:
```

## Performance Considerations

### Circuit Management
- Monitor active circuits: `aspnetcore.components.circuit.active`
- Set appropriate timeout intervals (default 60s client timeout)
- Implement circuit disposal for disconnected users

### Rendering Optimization
- Use `@key` directive for list items (meal plan days)
- Implement `ShouldRender()` to prevent unnecessary re-renders
- Minimize parameter processing (`aspnetcore.components.update_parameters.duration`)
- Track render batch size (`aspnetcore.components.render_diff.size`)

### State Management
- Avoid storing large objects in circuit state (memory leak risk)
- Use scoped services, not local component variables
- Implement proper disposal (IDisposable/IAsyncDisposable)

### Shopping List Aggregation
- Database-side aggregation using PostgreSQL array functions
- Group by ingredient base (normalize "2 cups flour" + "1 cup flour" = "3 cups flour")
- Use semantic similarity (embedding-based) for advanced consolidation

## Known Limitations

### Blazor Server Offline Support
**Limitation:** Blazor Server requires constant SignalR connection; true offline mode not possible
**Impact:** Shopping list unavailable if user loses connection in store
**Mitigation:**
- Aggressive reconnection attempts (`.WithAutomaticReconnect()`)
- UI feedback showing connection status
- Consider Blazor WebAssembly for shopping list view only (hybrid approach)
- Accept online-only requirement (most stores have WiFi in 2026)

### State Loss on Circuit Disconnect
**Limitation:** UI state lives in server memory; disconnects lose unsaved work
**Impact:** Users lose form progress after network interruptions
**Mitigation:**
- Auto-save drafts to database every N seconds
- Persist critical state to browser localStorage via JS interop
- Show "connection lost" warning before circuit disposal
- Short circuit timeout (30-60s) to preserve memory

### Horizontal Scaling Complexity
**Limitation:** Circuits are sticky to servers; load balancing requires session affinity
**Impact:** Multi-instance deployments need sticky sessions or Redis backplane
**Mitigation:**
- Use Azure SignalR Service or Redis backplane for distributed state
- Configure sticky sessions at load balancer
- Or accept single-instance deployment (sufficient for family app scale)

## Sources

**Framework & Core Technologies:**
- [What's new in ASP.NET Core in .NET 8 - Microsoft Learn](https://learn.microsoft.com/en-us/aspnet/core/release-notes/aspnetcore-8.0?view=aspnetcore-9.0) — HIGH confidence
- [ASP.NET Core Blazor performance best practices - Microsoft Learn](https://learn.microsoft.com/en-us/aspnet/core/blazor/performance/?view=aspnetcore-8.0) — HIGH confidence
- [.NET 8's Best Blazor is not Blazor as we know it - ServiceStack](https://servicestack.net/posts/net8-best-blazor) — MEDIUM confidence
- [Getting Started with Blazor's New Render Modes in .NET 8 - Telerik](https://www.telerik.com/blogs/getting-started-blazor-new-render-modes-net-8) — MEDIUM confidence

**Database:**
- [Npgsql Entity Framework Core Provider - Official Docs](https://www.npgsql.org/efcore/) — HIGH confidence
- [NuGet Gallery - Npgsql.EntityFrameworkCore.PostgreSQL](https://www.nuget.org/packages/npgsql.entityframeworkcore.postgresql) — HIGH confidence
- [10 Essential Best Practices for Using Entity Framework Core in .NET 8 - Medium](https://medium.com/@solomongetachew112/10-essential-best-practices-for-using-entity-framework-core-in-net-8-3274d6143992) — MEDIUM confidence

**Web Scraping:**
- [Web Scraping in C#: Complete Guide 2026 - ZenRows](https://www.zenrows.com/blog/web-scraping-c-sharp) — MEDIUM confidence
- [Top 7 C# Web Scraping Libraries in 2026 - Bright Data](https://brightdata.com/blog/web-data/c-sharp-web-scraping-libraries) — MEDIUM confidence
- [NuGet Gallery - HtmlAgilityPack](https://www.nuget.org/packages/HtmlAgilityPack/) — HIGH confidence
- [NuGet Gallery - AngleSharp](https://www.nuget.org/packages/AngleSharp/) — HIGH confidence
- [Recipe - Schema.org Type](https://schema.org/Recipe) — HIGH confidence
- [GitHub - micahcochran/scrape-schema-recipe](https://github.com/micahcochran/scrape-schema-recipe) — MEDIUM confidence

**UI Components:**
- [Blazorise - Blazor Component Library](https://blazorise.com/) — HIGH confidence
- [MudBlazor - Blazor Component Library](https://mudblazor.com/) — HIGH confidence
- [Free Blazor Components | 100+ UI controls by Radzen](https://blazor.radzen.com/) — HIGH confidence
- [MudBlazor vs. Radzen - Component Library Comparison](https://gimburg.online/mudblazor-vs-radzen-choosing-the-right-component-library-for-your-blazor-project/) — MEDIUM confidence
- [10 Blazor component libraries to speed up your development](https://jonhilton.net/blazor-component-libraries/) — MEDIUM confidence

**Real-Time Collaboration:**
- [Real-Time Blazor Apps: Integrating SignalR and Blazorise Notifications](https://blazorise.com/blog/real-time-blazor-apps-signalr-and-blazorise-notifications) — MEDIUM confidence
- [Use ASP.NET Core SignalR with Blazor - Microsoft Learn](https://learn.microsoft.com/en-us/aspnet/core/blazor/tutorials/signalr-blazor?view=aspnetcore-10.0) — HIGH confidence
- [Real-Time Collaborative Editing in Blazor Diagram with SignalR and Redis - Syncfusion](https://www.syncfusion.com/blogs/post/collaborative-editing-in-blazor-diagram) — MEDIUM confidence

**Validation:**
- [FluentValidation - Blazor Documentation](https://docs.fluentvalidation.net/en/latest/blazor.html) — HIGH confidence
- [GitHub - Blazored/FluentValidation](https://github.com/Blazored/FluentValidation) — HIGH confidence
- [GitHub - loresoft/Blazilla](https://github.com/loresoft/Blazilla) — MEDIUM confidence

**Logging:**
- [NuGet Gallery - Serilog.AspNetCore](https://www.nuget.org/packages/Serilog.AspNetCore) — HIGH confidence
- [GitHub - serilog/serilog-aspnetcore](https://github.com/serilog/serilog-aspnetcore) — HIGH confidence
- [Structured Logging with Serilog in ASP.NET Core - Code with Mukesh](https://codewithmukesh.com/blog/structured-logging-with-serilog-in-aspnet-core/) — MEDIUM confidence

**Docker Deployment:**
- [Containerising a Blazor Server App - Chris Sainty](https://chrissainty.com/containerising-blazor-applications-with-docker-containerising-a-blazor-server-app/) — MEDIUM confidence
- [How I use Docker to deploy my Blazor apps - Jon Hilton](https://jonhilton.net/blazor-docker-hosting/) — MEDIUM confidence

**Anti-Patterns & Pitfalls:**
- [10 Architecture Mistakes Developers Make in Blazor Projects - Medium](https://medium.com/dotnet-new/10-architecture-mistakes-developers-make-in-blazor-projects-and-how-to-fix-them-e99466006e0d) — MEDIUM confidence
- [Building Resilient Blazor Server Apps in .NET 10 - Medium](https://medium.com/@brian.moraboza/building-resilient-blazor-server-apps-in-net-10-5fb4838cbc6d) — MEDIUM confidence
- [Common Mistakes in Blazor Development and How to Solve Them - Medium](https://medium.com/@yusufeminirki/common-mistakes-in-blazor-development-and-how-to-solve-them-55ded7e5d338) — MEDIUM confidence

**PWA & Offline:**
- [Offline-First Strategy with Blazor PWAs - Medium](https://medium.com/@dgallivan23/offline-first-strategy-with-blazor-pwas-a-complete-guide-a6e27e564d0c) — MEDIUM confidence
- [ASP.NET Core Blazor Progressive Web Application (PWA) - Microsoft Learn](https://learn.microsoft.com/en-us/aspnet/core/blazor/progressive-web-app/?view=aspnetcore-10.0) — HIGH confidence

**Meal Planning Domain:**
- [Top Meal Planning Apps with Grocery Lists in the U.S. (2026) - Fitia](https://fitia.app/learn/article/7-meal-planning-apps-smart-grocery-lists-us/) — MEDIUM confidence
- [AI Grocery List App Development - Matellio](https://www.matellio.com/blog/ai-grocery-list-app-development/) — MEDIUM confidence

---
*Stack research for: Family Meal Planning and Coordination Web Application*
*Researched: 2026-01-22*
*Confidence: HIGH (verified with official documentation and NuGet package versions)*
