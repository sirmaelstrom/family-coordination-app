---
name: dotnet-performance
description: "Optimize .NET/C# performance through measurement, not assumption. Use when investigating or tuning performance in a .NET/C# project — slow endpoints, high memory/GC pressure, allocation hotspots, async/await issues, EF Core query performance, thread-pool exhaustion, or benchmarking. Triggers: 'this is slow', 'optimize this', 'reduce allocations', 'why is memory high', 'N+1 query', 'benchmark this'. For .NET/C# projects only."
---

# .NET Performance

**Measure first, optimize second.** Never optimize on assumptions — profile, identify the actual bottleneck, benchmark the improvement. Readability and maintainability trump micro-optimizations. Prefer algorithmic improvements over language tricks.

**Focus Areas:**

- **Memory Allocation Optimization**
  - `Span<T>` and `Memory<T>` for buffer operations
  - `stackalloc` for small temporary buffers
  - `ArrayPool<T>` and object pooling
  - String handling (StringBuilder, interpolation, spans)
  - Reducing boxing/unboxing
  - Struct vs class trade-offs

- **Async/Await Patterns**
  - `ConfigureAwait(false)` in library code
  - `ValueTask<T>` for frequently-completed operations
  - `IAsyncEnumerable<T>` for streaming data
  - Avoiding async-over-sync (blocking vs truly async I/O)
  - Thread-pool tuning and async state-machine overhead
  - Cancellation token propagation

- **LINQ Optimization**
  - Deferred execution and materialization timing
  - Query compilation overhead
  - Alternatives to LINQ when needed (loops, iterators)
  - `AsParallel()` and PLINQ when appropriate
  - Avoiding closure allocations

- **EF Core Performance**
  - Query splitting for cartesian explosion
  - `AsNoTracking()` for read-only queries
  - Compiled queries (`EF.CompileQuery`)
  - Bulk operations (raw SQL, bulk-extension libraries)
  - Projection with `Select()` to fetch only needed columns
  - N+1 query detection and fixing
  - Index optimization

- **Caching Strategies**
  - `IMemoryCache` for single-server scenarios
  - `IDistributedCache` for multi-server
  - Response caching middleware
  - Output caching (.NET 7+)
  - Cache invalidation strategies
  - Cache-aside pattern

- **Garbage Collection Tuning**
  - GC generations and collection triggers
  - Server GC vs Workstation GC
  - Heap sizing and segment configuration
  - Minimizing Gen 2 collections
  - Analyzing GC pressure with `dotnet-counters` / perfmon

**Key Actions:**

1. **Profile before optimizing** — BenchmarkDotNet, dotnet-trace, dotnet-counters, PerfView
2. **Identify allocation hotspots** — Gen 0/1/2 allocation rates and object lifetimes
3. **Benchmark changes** — Measure before and after with realistic workloads
4. **Optimize database queries** — Query analysis, execution plans, indexing
5. **Apply appropriate caching** — Choose strategy by data volatility and access pattern
6. **Recommend async patterns** — Identify blocking I/O and suggest async alternatives
7. **Reduce allocations** — Spans, pooling, value types where allocation pressure is proven

**Outputs:**

- BenchmarkDotNet results comparing implementations
- Profiling reports (dotnet-trace, PerfView) with analysis
- Optimized code with before/after metrics
- Database query execution plans with recommendations
- Memory allocation analysis and reduction strategies
- Caching implementation with invalidation logic
- GC tuning recommendations with configuration

**Boundaries:**

**Will:**
- Profile existing code to identify bottlenecks
- Benchmark optimizations to prove improvements
- Recommend algorithmic changes over micro-optimizations
- Suggest appropriate data structures (Dictionary vs List, HashSet, etc.)
- Identify N+1 queries and propose batching/eager loading
- Use spans and pooling where allocation pressure is proven

**Will Not:**
- Optimize without measurement and profiling data
- Sacrifice readability for negligible performance gains
- Apply micro-optimizations that complicate code without evidence
- Recommend premature optimization during initial development
- Suggest unsafe code without significant proven benefit

**Will Always:**
- Require profiling data before suggesting optimizations
- Provide benchmark results to validate improvements
- Consider trade-offs (readability, maintainability, complexity)
- Recommend starting with simple solutions (better algorithm, caching, indexes)
- Highlight when optimization won't meaningfully impact user experience

**Profiling Tools:** BenchmarkDotNet, dotnet-trace, dotnet-counters, PerfView, JetBrains dotMemory / dotTrace, Visual Studio Profiler, Application Insights.
