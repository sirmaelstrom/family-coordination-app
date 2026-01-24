# Phase 6: Recipe Import - Research

**Researched:** 2026-01-24
**Domain:** Web scraping, HTML parsing, JSON-LD extraction, HTTP resilience
**Confidence:** HIGH for core libraries and patterns, MEDIUM for anti-bot mitigation strategies

## Summary

Recipe import from URLs requires three layers: (1) HTTP client with anti-bot mitigation for fetching HTML, (2) HTML parsing to extract JSON-LD structured data, and (3) graceful degradation when structured data is missing. The standard .NET stack is **AngleSharp 1.4.0** for HTML parsing and **Polly 8.6.5** for HTTP resilience. Most major recipe sites (AllRecipes, Food Network, NYT Cooking) use schema.org JSON-LD markup in `<script type="application/ld+json">` tags, making extraction straightforward with CSS selectors.

The primary technical challenges are: (1) anti-bot detection (Cloudflare, fingerprinting, rate limiting), (2) SSRF vulnerability prevention (localhost/private IP validation), and (3) handling incomplete or missing schema.org data. Anti-bot mitigation requires realistic User-Agent headers, request timing variations, and potentially residential proxies for aggressive bot detection—but for MVP with family usage patterns (low volume, manual triggers), basic User-Agent rotation and Polly retry policies should suffice.

**Primary recommendation:** Use HttpClient with Polly retry policies + AngleSharp for HTML parsing + System.Text.Json for JSON-LD deserialization. Implement JSON-LD-first extraction with HTML fallback for critical fields (name, ingredients, instructions). Validate URLs against SSRF attacks before fetching. For MVP, skip full JSON-LD processor (json-ld.net) and deserialize directly to POCOs since we only need Recipe type.

## Standard Stack

### Core

| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| AngleSharp | 1.4.0 | HTML parsing with CSS selectors | W3C-compliant DOM API, 70M+ downloads, modern HTML5 support, superior to HtmlAgilityPack for CSS selectors |
| Polly | 8.6.5 | HTTP retry, timeout, circuit breaker | Industry standard for .NET resilience, native async/await, resilience pipelines (v8), IHttpClientFactory integration |
| System.Text.Json | Built-in (.NET 8+) | JSON-LD deserialization | High performance, UTF-8 native, built-in, sufficient for simple schema.org Recipe deserialization |

### Supporting

| Library | Version | Purpose | When to Use |
|---------|---------|---------|-------------|
| json-ld.net | Latest (check NuGet) | Full JSON-LD processing (expand, compact, normalize) | Only if need full linked data features—overkill for simple Recipe extraction |
| RichardSzalay.MockHttp | Latest | HttpClient mocking for tests | Integration testing recipe scraping without hitting live sites |

### Alternatives Considered

| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| AngleSharp | HtmlAgilityPack | HAP has 100M+ downloads vs AngleSharp's 70M, lower memory footprint, but requires XPath instead of CSS selectors and less accurate HTML5 parsing |
| System.Text.Json | json-ld.net processor | Full JSON-LD processor handles @context expansion, but adds complexity and dependency for simple Recipe deserialization—only needed if consuming arbitrary linked data |
| Polly | Manual retry logic | Custom retry loses exponential backoff with jitter, circuit breaker patterns, timeout coordination—reinventing battle-tested library |

**Installation:**

```bash
# Core dependencies
dotnet add package AngleSharp --version 1.4.0
dotnet add package Polly --version 8.6.5

# Testing (optional)
dotnet add package RichardSzalay.MockHttp --version <latest>
```

**Note:** Project currently uses .NET 10.0 (net10.0 target framework). Verify package compatibility, though both AngleSharp 1.4.0 and Polly 8.6.5 target .NET Standard 2.0/.NET 6.0+ and are compatible with .NET 10.

## Architecture Patterns

### Recommended Project Structure

```
Services/
├── RecipeImportService.cs      # Orchestrates URL → Recipe entity
├── RecipeScraperService.cs     # Fetches HTML, extracts JSON-LD
├── RecipeParserService.cs      # Parses schema.org Recipe to entity
└── UrlValidator.cs             # SSRF validation (localhost/private IP blocking)

Models/
└── SchemaOrg/
    ├── RecipeSchema.cs         # POCO for schema.org Recipe type
    └── (other schema types as needed)
```

### Pattern 1: HttpClient with Polly Resilience

**What:** Configure IHttpClientFactory with Polly policies for retry with exponential backoff, timeout, and circuit breaker.

**When to use:** All external HTTP requests (recipe scraping, any web fetching).

**Example:**

```csharp
// Source: https://medium.com/asp-dotnet/polly-in-net-effective-retry-and-timeout-policies-for-httpclient-0d4712cc5d15
// Verified: https://learn.microsoft.com/en-us/dotnet/architecture/microservices/implement-resilient-applications/implement-http-call-retries-exponential-backoff-polly

// In Program.cs (service registration)
builder.Services.AddHttpClient("RecipeScraper")
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
    })
    .AddStandardResilienceHandler(options =>
    {
        // Retry with exponential backoff + jitter
        options.Retry.MaxRetryAttempts = 3;
        options.Retry.BackoffType = Polly.DelayBackoffType.Exponential;
        options.Retry.UseJitter = true;

        // Timeout per attempt
        options.AttemptTimeout = TimeSpan.FromSeconds(30);

        // Total timeout across all retries
        options.TotalRequestTimeout = TimeSpan.FromSeconds(60);

        // Circuit breaker (open after 3 consecutive failures, break for 30s)
        options.CircuitBreaker.FailureRatio = 0.5;
        options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(10);
        options.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(30);
    });

// Usage in service
public class RecipeScraperService
{
    private readonly IHttpClientFactory _httpClientFactory;

    public RecipeScraperService(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public async Task<string> FetchHtmlAsync(string url)
    {
        var client = _httpClientFactory.CreateClient("RecipeScraper");

        // Set realistic User-Agent (critical for anti-bot)
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36"
        );

        var response = await client.GetAsync(url);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }
}
```

### Pattern 2: JSON-LD Extraction with AngleSharp

**What:** Parse HTML with AngleSharp, query for `<script type="application/ld+json">` tags, deserialize with System.Text.Json.

**When to use:** Extracting structured data from recipe websites.

**Example:**

```csharp
// Source: https://www.zenrows.com/blog/anglesharp (verified 2026)
// Source: https://code-maze.com/csharp-parsing-html-with-anglesharp/

using AngleSharp;
using AngleSharp.Html.Parser;
using System.Text.Json;

public async Task<RecipeSchema?> ExtractRecipeJsonLdAsync(string html)
{
    var parser = new HtmlParser();
    var document = await parser.ParseDocumentAsync(html);

    // Query for JSON-LD script tags
    var jsonLdScripts = document.QuerySelectorAll("script[type='application/ld+json']");

    foreach (var script in jsonLdScripts)
    {
        var jsonContent = script.TextContent;

        try
        {
            // JSON-LD can be single object or array
            if (jsonContent.TrimStart().StartsWith("["))
            {
                var items = JsonSerializer.Deserialize<RecipeSchema[]>(jsonContent);
                var recipe = items?.FirstOrDefault(i => i.Type == "Recipe");
                if (recipe != null) return recipe;
            }
            else
            {
                var item = JsonSerializer.Deserialize<RecipeSchema>(jsonContent);
                if (item?.Type == "Recipe") return item;
            }
        }
        catch (JsonException)
        {
            // Invalid JSON, try next script tag
            continue;
        }
    }

    return null;
}
```

### Pattern 3: SSRF Protection with URL Validation

**What:** Validate URLs before fetching to prevent Server-Side Request Forgery attacks (localhost, private IPs, alternative representations).

**When to use:** All user-provided URLs before HTTP requests.

**Example:**

```csharp
// Source: https://cheatsheetseries.owasp.org/cheatsheets/Server_Side_Request_Forgery_Prevention_Cheat_Sheet.html
// Source: https://medium.com/@jeroenverhaeghe/why-you-should-check-your-ssrf-validation-right-now-fdad82d6e5f5

public class UrlValidator
{
    private static readonly HashSet<string> AllowedSchemes = new() { "http", "https" };

    public bool IsUrlSafe(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        // Only allow http/https
        if (!AllowedSchemes.Contains(uri.Scheme.ToLowerInvariant()))
            return false;

        // Resolve DNS to check IP
        try
        {
            var addresses = Dns.GetHostAddresses(uri.Host);
            foreach (var addr in addresses)
            {
                if (IsPrivateOrLocalAddress(addr))
                    return false;
            }
        }
        catch (SocketException)
        {
            return false; // DNS resolution failed
        }

        return true;
    }

    private bool IsPrivateOrLocalAddress(IPAddress address)
    {
        // Check loopback (127.0.0.0/8, ::1)
        if (IPAddress.IsLoopback(address))
            return true;

        // Check 0.0.0.0 (can refer to localhost on some systems)
        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
            return true;

        // Check private ranges (10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16)
        byte[] bytes = address.GetAddressBytes();
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            if (bytes[0] == 10) return true;
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
            if (bytes[0] == 192 && bytes[1] == 168) return true;
            if (bytes[0] == 169 && bytes[1] == 254) return true; // Link-local
        }

        return false;
    }
}
```

### Pattern 4: Graceful Degradation with Fallback Parsing

**What:** If JSON-LD extraction fails or returns incomplete data, fallback to HTML parsing for critical fields.

**When to use:** Ensuring import works for sites with missing/incomplete schema.org markup.

**Example:**

```csharp
// Source: https://github.com/hhursev/recipe-scrapers (Python reference, shows pattern)
// Pattern: JSON-LD first, HTML fallback for missing fields

public async Task<RecipeSchema> ExtractRecipeWithFallbackAsync(string html)
{
    var recipe = await ExtractRecipeJsonLdAsync(html) ?? new RecipeSchema();

    var parser = new HtmlParser();
    var document = await parser.ParseDocumentAsync(html);

    // Fallback for missing name
    if (string.IsNullOrWhiteSpace(recipe.Name))
    {
        var titleElement = document.QuerySelector("h1.recipe-title, h1[itemprop='name'], h1");
        recipe.Name = titleElement?.TextContent.Trim();
    }

    // Fallback for missing ingredients (common selectors)
    if (recipe.RecipeIngredient == null || !recipe.RecipeIngredient.Any())
    {
        var ingredientElements = document.QuerySelectorAll(
            ".ingredient, [itemprop='recipeIngredient'], .recipe-ingredients li"
        );
        recipe.RecipeIngredient = ingredientElements
            .Select(e => e.TextContent.Trim())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToArray();
    }

    // Fallback for missing instructions
    if (recipe.RecipeInstructions == null || string.IsNullOrWhiteSpace(recipe.RecipeInstructions.ToString()))
    {
        var instructionElements = document.QuerySelectorAll(
            ".instruction, [itemprop='recipeInstructions'], .recipe-steps li"
        );
        var instructions = string.Join("\n\n",
            instructionElements.Select(e => e.TextContent.Trim())
        );
        recipe.RecipeInstructions = instructions;
    }

    return recipe;
}
```

### Anti-Patterns to Avoid

- **Blocking HTTP calls in UI thread:** Always use async/await for HTTP requests—synchronous calls will freeze Blazor Server SignalR connection
- **Storing HttpClient instance as field:** Use IHttpClientFactory—creating new HttpClient per request exhausts sockets and prevents proper connection pooling
- **Hardcoded User-Agent string:** Rotate User-Agent headers from a pool—single static UA is easily fingerprinted and blocked
- **No timeout on HTTP requests:** Always set timeouts—recipe sites may be slow or unresponsive, causing indefinite hangs
- **Deserializing untrusted JSON without validation:** Validate JSON structure before deserialization—malicious sites could return payloads that exploit deserializer vulnerabilities
- **Ignoring redirect responses:** Validate redirect URLs with same SSRF checks—redirects can bypass initial URL validation to hit internal resources

## Don't Hand-Roll

Problems that look simple but have existing solutions:

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| HTTP retry with backoff | Manual retry loop with Thread.Sleep | Polly retry policies | Polly handles exponential backoff with jitter (prevents thundering herd), circuit breaker (stops retrying dead services), timeout coordination (per-attempt vs total), cancellation token propagation—custom implementation misses edge cases like concurrent retry storms |
| HTML parsing with regex | Regex patterns for `<tag>content</tag>` | AngleSharp CSS selectors | HTML is not regular language—regex fails on nested tags, self-closing tags, attributes, comments, CDATA sections; AngleSharp handles malformed HTML, character encoding, entity decoding |
| User-Agent rotation | Random string generation | List of real browser User-Agent strings | Anti-bot systems fingerprint User-Agent format—fake UAs are detected by missing browser-specific headers (Sec-CH-UA, Accept-Language patterns); use real strings from useragentstring.com or whatismybrowser.com |
| URL validation for SSRF | Simple localhost string check | DNS resolution + IP range validation | Attackers bypass "localhost" filters with 127.1, 0x7f.0.0.1, 2130706433 (decimal), DNS rebinding, alternative encodings—only DNS resolution + IP range check catches all variants |
| JSON-LD parsing | Regex to extract JSON from script tags | AngleSharp to query script tags + System.Text.Json | JSON-LD in HTML can have escaped quotes, nested script tags, HTML comments, minification—regex breaks on edge cases; DOM query + JSON parser is robust |

**Key insight:** Web scraping has adversarial nature (sites actively block scrapers) and edge cases (malformed HTML, encoding issues, redirects). Libraries like AngleSharp and Polly encode years of production battle-testing against these edge cases—custom solutions miss subtle failure modes until they break in production.

## Common Pitfalls

### Pitfall 1: Cloudflare/Bot Detection Blocking Requests

**What goes wrong:** Legitimate recipe import requests return 403 Forbidden or Cloudflare challenge pages instead of HTML.

**Why it happens:** Recipe sites use Cloudflare or similar anti-bot services that fingerprint requests via TLS fingerprint, User-Agent header, missing browser headers (Accept, Accept-Language, Sec-CH-UA), request timing patterns, IP reputation. Default HttpClient has .NET TLS fingerprint and no User-Agent—instantly flagged.

**How to avoid:**
1. Set realistic User-Agent header (recent Chrome/Firefox on Windows/Mac)
2. Add common browser headers: Accept, Accept-Language, Accept-Encoding, Referer
3. Vary request timing (don't hammer sites)—Polly retry with exponential backoff helps naturally
4. For aggressive sites: may need residential proxies or headless browser (Playwright, Selenium)—defer to future enhancement

**Warning signs:**
- 403 Forbidden responses
- HTML contains Cloudflare challenge page (`<title>Just a moment...</title>`)
- Successful curl but failed HttpClient (TLS fingerprint difference)

**For MVP:** Most major recipe sites (AllRecipes, Food Network) allow scraping with proper User-Agent. If blocked, show error with "manual paste" fallback. Don't over-engineer proxy rotation for 20% of sites.

### Pitfall 2: JSON-LD Arrays vs Single Objects

**What goes wrong:** Deserialization fails because JSON-LD can be single object `{...}` or array `[{...}]` in same `<script>` tag.

**Why it happens:** Schema.org allows embedding multiple structured data items (Recipe, BreadcrumbList, Organization) as array, but single Recipe sites often emit single object. Deserializing to `RecipeSchema` when JSON is array throws exception.

**How to avoid:**
1. Check first character of JSON content after trim: `[` means array, `{` means object
2. Deserialize accordingly: `RecipeSchema[]` or `RecipeSchema`
3. Filter for `@type: "Recipe"` since arrays may contain other types

**Warning signs:**
- JsonException: "The JSON value could not be converted to RecipeSchema"
- Sites with multiple JSON-LD script tags (one per type)

**Code pattern shown in Pattern 2 above.**

### Pitfall 3: SSRF Vulnerability from User-Provided URLs

**What goes wrong:** Attacker provides URL like `http://localhost:5000/admin` or `http://192.168.1.1/router-config`, server-side HttpClient fetches internal resources, exposes them to attacker.

**Why it happens:** Recipe import accepts arbitrary URLs from users. Without validation, attacker can target internal network resources (localhost, private IPs, cloud metadata endpoints like 169.254.169.254), bypassing firewalls since request originates from server.

**How to avoid:**
1. Validate URL scheme is http/https only (block file://, ftp://, gopher://)
2. Resolve DNS to IP addresses
3. Check all resolved IPs against:
   - Loopback: 127.0.0.0/8, ::1
   - Private ranges: 10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16
   - Link-local: 169.254.0.0/16 (AWS metadata!)
   - Special: 0.0.0.0
4. Reject on match
5. **Critical:** Re-validate after redirects—redirect can bypass initial check

**Warning signs:**
- Security audit flags URL handling
- Penetration test attempts internal network access
- Logs show requests to 127.0.0.1, 192.168.x.x from recipe import

**Code pattern shown in Pattern 3 above.**

### Pitfall 4: Incomplete or Missing Schema.org Fields

**What goes wrong:** Recipe JSON-LD has `name` and `image` (Google required fields) but missing `recipeIngredient` or `recipeInstructions`, import creates useless recipe stub.

**Why it happens:** Schema.org only requires `name` and `image` for SEO rich results. Many sites omit optional fields or use non-standard formats (HowToStep array vs plain text for instructions). Deserializer succeeds but properties are null.

**How to avoid:**
1. Define validation rules for imported recipes (must have: name, ingredients array with ≥1 item, instructions)
2. If JSON-LD missing critical fields, fallback to HTML parsing (see Pattern 4)
3. Show clear error if extraction fails: "Could not extract recipe from URL. Please paste ingredients and instructions manually."
4. Log failed URLs for debugging/improving parsers

**Warning signs:**
- Imported recipes with titles but no ingredients/instructions
- User reports "import worked but recipe is empty"
- Specific sites consistently fail validation

### Pitfall 5: Encoding and Special Character Issues

**What goes wrong:** Recipe content has garbled text: "½ cup" becomes "Â½ cup", "jalapeño" becomes "jalapeÃ±o".

**Why it happens:** HTML declares charset in `<meta charset="UTF-8">` but HttpClient reads as wrong encoding (Windows-1252, ISO-8859-1). AngleSharp detects from BOM or meta tag, but if HTML is pre-decoded incorrectly, AngleSharp receives corrupted input.

**How to avoid:**
1. Let AngleSharp handle encoding detection—pass raw bytes or let it parse from stream
2. If using HttpClient.GetStringAsync(), check response Content-Type header for charset
3. For UTF-8, use `response.Content.ReadAsStringAsync()` (default)
4. Test with recipes containing Unicode fractions (½ ⅓ ¼), accented characters (jalapeño, café)

**Warning signs:**
- Â, Ã characters in imported text (UTF-8 interpreted as Windows-1252)
- User reports "text looks weird" on imported recipes
- Fractions, accents corrupted

## Code Examples

Verified patterns from official sources:

### Extracting Recipe Name from JSON-LD

```csharp
// Source: https://schema.org/Recipe (official)
// Source: https://developers.google.com/search/docs/appearance/structured-data/recipe

public class RecipeSchema
{
    [JsonPropertyName("@context")]
    public string? Context { get; set; } // Usually "https://schema.org"

    [JsonPropertyName("@type")]
    public string Type { get; set; } = "Recipe";

    [JsonPropertyName("name")]
    public string? Name { get; set; } // Required

    [JsonPropertyName("image")]
    public object? Image { get; set; } // Can be string URL or ImageObject

    [JsonPropertyName("recipeIngredient")]
    public string[]? RecipeIngredient { get; set; }

    [JsonPropertyName("recipeInstructions")]
    public object? RecipeInstructions { get; set; } // Can be string or HowToStep[]

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("prepTime")]
    public string? PrepTime { get; set; } // ISO 8601 duration: "PT15M"

    [JsonPropertyName("cookTime")]
    public string? CookTime { get; set; } // ISO 8601 duration: "PT30M"

    [JsonPropertyName("recipeYield")]
    public object? RecipeYield { get; set; } // Can be string "4 servings" or number

    [JsonPropertyName("recipeCategory")]
    public string? RecipeCategory { get; set; } // "dinner", "appetizer", etc.

    [JsonPropertyName("recipeCuisine")]
    public string? RecipeCuisine { get; set; } // "Italian", "Thai", etc.
}
```

### User-Agent Rotation

```csharp
// Source: https://www.zenrows.com/blog/c-sharp-httpclient-user-agent
// Source: https://hasdata.com/blog/user-agents-for-web-scraping

public class UserAgentRotator
{
    private static readonly string[] UserAgents = new[]
    {
        // Chrome on Windows
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36",
        // Firefox on Windows
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:123.0) Gecko/20100101 Firefox/123.0",
        // Chrome on Mac
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36",
        // Safari on Mac
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.2 Safari/605.1.15",
    };

    private readonly Random _random = new();

    public string GetRandomUserAgent()
    {
        return UserAgents[_random.Next(UserAgents.Length)];
    }
}
```

### Complete Import Flow

```csharp
// Combined pattern showing full flow

public class RecipeImportService
{
    private readonly RecipeScraperService _scraper;
    private readonly RecipeParserService _parser;
    private readonly UrlValidator _urlValidator;

    public async Task<Result<Recipe>> ImportFromUrlAsync(string url)
    {
        // Step 1: Validate URL (SSRF protection)
        if (!_urlValidator.IsUrlSafe(url))
            return Result<Recipe>.Failure("URL is not allowed (security restriction)");

        // Step 2: Fetch HTML (with Polly retry/timeout)
        string html;
        try
        {
            html = await _scraper.FetchHtmlAsync(url);
        }
        catch (HttpRequestException ex)
        {
            return Result<Recipe>.Failure($"Failed to fetch URL: {ex.Message}");
        }

        // Step 3: Extract recipe data (JSON-LD first, HTML fallback)
        var recipeSchema = await _scraper.ExtractRecipeWithFallbackAsync(html);

        // Step 4: Validate extracted data
        if (string.IsNullOrWhiteSpace(recipeSchema.Name))
            return Result<Recipe>.Failure("Could not extract recipe name from URL");

        if (recipeSchema.RecipeIngredient == null || !recipeSchema.RecipeIngredient.Any())
            return Result<Recipe>.Failure("Could not extract ingredients from URL");

        if (recipeSchema.RecipeInstructions == null)
            return Result<Recipe>.Failure("Could not extract instructions from URL");

        // Step 5: Parse to domain entity
        var recipe = _parser.ParseSchemaToEntity(recipeSchema, url);

        return Result<Recipe>.Success(recipe);
    }
}
```

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| HtmlAgilityPack with XPath | AngleSharp with CSS selectors | 2016+ | Modern HTML5 parsing, CSS selectors more familiar to web devs, better handling of malformed HTML |
| Polly v7 Policy Wrap | Polly v8 Resilience Pipelines | 2023 (v8 release) | Cleaner API, AddStandardResilienceHandler() combines retry+timeout+circuit breaker, native async/await |
| Newtonsoft.Json | System.Text.Json | 2019 (.NET Core 3.0) | Higher performance, lower allocations, UTF-8 native—Newtonsoft still used for complex scenarios but System.Text.Json sufficient for schema.org |
| json-ld.net for all JSON-LD | Direct deserialization to POCOs | Ongoing | Full JSON-LD processor overkill for simple Recipe extraction—saves dependency and complexity when only consuming one schema.org type |

**Deprecated/outdated:**
- **WebClient class:** Replaced by HttpClient in .NET Framework 4.5+ (2012)—WebClient is synchronous, blocks threads
- **puppeteer-extra-plugin-stealth:** No longer maintained for 2025+ Cloudflare bypass—use Nodriver, SeleniumBase UC Mode, or Camoufox instead (though these are Python; .NET equivalent is Playwright if needed)

## Open Questions

Things that couldn't be fully resolved:

1. **Anti-bot detection severity for target recipe sites**
   - What we know: AllRecipes, Food Network use schema.org JSON-LD (verified by WebSearch). Cloudflare detection has become more sophisticated with per-customer ML models (2025-2026 trend).
   - What's unclear: Do target sites (AllRecipes, Food Network, NYT Cooking) actively block HttpClient requests with proper User-Agent, or do they allow scraping? Testing required.
   - Recommendation: Start with basic User-Agent + Polly retry. If blocked, log failure + show manual import UI. Defer headless browser (Playwright) to future enhancement—avoid over-engineering for edge cases in MVP.

2. **Handling recipe instructions as HowToStep[] vs plain text**
   - What we know: Schema.org `recipeInstructions` can be string or array of HowToStep objects (structured with text, image, video per step).
   - What's unclear: How prevalent is HowToStep format vs plain text string? Does it justify extra parsing logic?
   - Recommendation: Support both—deserialize as JsonElement, check if string or array, extract text accordingly. HowToStep parsing adds value (step-by-step display) but fallback to plain text if structure unexpected.

3. **Rate limiting for recipe imports**
   - What we know: Family app has low usage (4-6 users max), manual import triggers, unlikely to trigger rate limits.
   - What's unclear: Should we implement per-user or global rate limiting to prevent accidental abuse (e.g., bulk import script)?
   - Recommendation: MVP: no rate limiting (trust family usage). Monitor logs for unusual patterns. Add rate limiting if abuse detected.

4. **JSON-LD context expansion necessity**
   - What we know: json-ld.net supports context expansion, compaction, normalization. Most recipe sites use standard schema.org context.
   - What's unclear: Are there sites using custom @context that require expansion for Recipe properties to be recognized?
   - Recommendation: Skip full JSON-LD processing in MVP—directly deserialize with assumption of standard schema.org context. If custom contexts appear, log error and fallback to manual import. Can add json-ld.net later if needed.

## Sources

### Primary (HIGH confidence)

- **Schema.org Recipe specification:** [https://schema.org/Recipe](https://schema.org/Recipe) - Official property definitions
- **Google Recipe Structured Data:** [https://developers.google.com/search/docs/appearance/structured-data/recipe](https://developers.google.com/search/docs/appearance/structured-data/recipe) - Required/recommended properties, formatting rules
- **AngleSharp GitHub:** [https://github.com/AngleSharp/AngleSharp](https://github.com/AngleSharp/AngleSharp) - Current version (1.4.0), installation, usage patterns
- **Polly NuGet:** [https://www.nuget.org/packages/polly/](https://www.nuget.org/packages/polly/) - Latest version (8.6.5)
- **AngleSharp NuGet:** [https://www.nuget.org/packages/AngleSharp](https://www.nuget.org/packages/AngleSharp) - Version verification
- **Microsoft Learn - Polly Retry:** [https://learn.microsoft.com/en-us/dotnet/architecture/microservices/implement-resilient-applications/implement-http-call-retries-exponential-backoff-polly](https://learn.microsoft.com/en-us/dotnet/architecture/microservices/implement-resilient-applications/implement-http-call-retries-exponential-backoff-polly) - Official resilience patterns
- **OWASP SSRF Prevention:** [https://cheatsheetseries.owasp.org/cheatsheets/Server_Side_Request_Forgery_Prevention_Cheat_Sheet.html](https://cheatsheetseries.owasp.org/cheatsheets/Server_Side_Request_Forgery_Prevention_Cheat_Sheet.html) - URL validation best practices
- **json-ld.net GitHub:** [https://github.com/linked-data-dotnet/json-ld.net](https://github.com/linked-data-dotnet/json-ld.net) - .NET JSON-LD processor

### Secondary (MEDIUM confidence)

- **ScrapingBee C# HTML Parser Comparison:** [https://www.scrapingbee.com/blog/csharp-html-parser/](https://www.scrapingbee.com/blog/csharp-html-parser/) - AngleSharp vs HtmlAgilityPack tradeoffs (2026)
- **5 Best C# HTML Parsers in 2026:** [https://www.scrapingdog.com/blog/csharp-html-parser/](https://www.scrapingdog.com/blog/csharp-html-parser/) - Download counts, performance comparison
- **Polly in .NET (Medium):** [https://medium.com/asp-dotnet/polly-in-net-effective-retry-and-timeout-policies-for-httpclient-0d4712cc5d15](https://medium.com/asp-dotnet/polly-in-net-effective-retry-and-timeout-policies-for-httpclient-0d4712cc5d15) - Retry/timeout patterns with code examples
- **C# Polly Retry (ZenRows):** [https://www.zenrows.com/blog/c-sharp-polly-retry](https://www.zenrows.com/blog/c-sharp-polly-retry) - User-Agent rotation with Polly
- **Code Maze - AngleSharp Parsing:** [https://code-maze.com/csharp-parsing-html-with-anglesharp/](https://code-maze.com/csharp-parsing-html-with-anglesharp/) - AngleSharp tutorial with CSS selectors
- **ZenRows AngleSharp Tutorial:** [https://www.zenrows.com/blog/anglesharp](https://www.zenrows.com/blog/anglesharp) - QuerySelector usage (2026)
- **Code Maze - HttpClient Mocking:** [https://code-maze.com/csharp-mock-httpclient-with-unit-tests/](https://code-maze.com/csharp-mock-httpclient-with-unit-tests/) - Testing patterns with MockHttp
- **Preventing SSRF in .NET (Medium):** [https://medium.com/@jeroenverhaeghe/why-you-should-check-your-ssrf-validation-right-now-fdad82d6e5f5](https://medium.com/@jeroenverhaeghe/why-you-should-check-your-ssrf-validation-right-now-fdad82d6e5f5) - .NET-specific SSRF validation code
- **recipe-scrapers Python Library:** [https://github.com/hhursev/recipe-scrapers](https://github.com/hhursev/recipe-scrapers) - Reference for fallback parsing patterns (606 supported sites, JSON-LD + HTML fallback approach)

### Tertiary (LOW confidence - requires validation)

- **Cloudflare Bypass Methods 2026:** [https://scrapfly.io/blog/posts/how-to-bypass-cloudflare-anti-scraping](https://scrapfly.io/blog/posts/how-to-bypass-cloudflare-anti-scraping), [https://www.zenrows.com/blog/bypass-cloudflare](https://www.zenrows.com/blog/bypass-cloudflare) - Anti-bot mitigation tactics (may be outdated quickly, vendor-specific)
- **WebSearch findings on major recipe sites using JSON-LD:** Not directly verified with site inspection—assumes standard practice based on SEO incentives
- **User-Agent string lists:** [https://www.zenrows.com/blog/c-sharp-httpclient-user-agent](https://www.zenrows.com/blog/c-sharp-httpclient-user-agent), [https://hasdata.com/blog/user-agents-for-web-scraping](https://hasdata.com/blog/user-agents-for-web-scraping) - Need periodic updates as browser versions change

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH - AngleSharp and Polly are well-established, version-verified, actively maintained
- Architecture: HIGH - Patterns verified from official docs (Microsoft Learn, OWASP, schema.org)
- Pitfalls: MEDIUM - Based on WebSearch + community experience, not exhaustive testing with target sites
- Anti-bot mitigation: MEDIUM - Cloudflare detection evolves rapidly, strategies may need updates
- SSRF protection: HIGH - OWASP guidance is authoritative and stable

**Research date:** 2026-01-24
**Valid until:** 2026-02-24 (30 days - AngleSharp/Polly are stable libraries, schema.org changes infrequently)

**Note:** Anti-bot detection strategies evolve rapidly (3-6 month shelf life). Re-verify Cloudflare bypass techniques if scraping failures occur. User-Agent strings should be updated quarterly to match current browser versions.
