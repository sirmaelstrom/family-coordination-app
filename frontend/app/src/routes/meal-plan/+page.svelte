<script lang="ts">
  // Meal-plan route. Where the plan is looking (week or day, and which date) lives in
  // this route's query string (`?view=day&date=YYYY-MM-DD`, see lib/meal-plan/lib/view.ts);
  // App reads it from $app/state and navigates with goto(resolve('/meal-plan') + search).
  // The /meal-plan SPA fallback (Program.cs) matches on path only, so a reload keeps the query.
  // Identity from #lib/session via ctx() (M8).
  import IslandRoute from '#lib/shared/IslandRoute.svelte';
  import App from '#lib/meal-plan/App.svelte';
  import { ctx } from '#lib/session.svelte.ts';
</script>

<IslandRoute>
  <App ctx={ctx()} />
</IslandRoute>
