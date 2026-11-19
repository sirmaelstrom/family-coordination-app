### Video review: NEEDS-LOOK (1 finding(s) at major or above)

![19 s clip](clip.gif)

[clip.mp4](clip.mp4) (19.4 s, holds 3 moment(s) for 1.5 s)

| at | severity | what a person sees | expected |
|---|---|---|---|
| 0:04.60 | major | After the search box is cleared, the grid is replaced for one frame by a different empty screen. It says 'No recipes yet / Start building your family recipe collection' with an 'Add Your First Recipe' button. Then the recipe cards appear. A person sees a flash of a false empty state. | The list should go straight from the 'No recipes found matching' message to the full grid, with no 'No recipes yet' screen in between. |
| 0:12.04 | minor | The recipe picker opens with 'Searching…' and no results. It then lists all five recipes while 'Beef Tacos' is already typed in the box. About 0.2 s later it narrows to just Beef Tacos (p16, 12.44). The list visibly changes height and content. | The picker should show only the matching recipe, or a loading state, without a stage where the typed name is ignored. |
| 0:08.64 | note | After Restore, the row disappears and the card shrinks to 'No deleted recipes.' in a faded in-between frame. This looks like a normal transition and matches the expected 'No deleted recipes.' message. | The page shows 'No deleted recipes.' after Restore, as it does. |

Judge: The restore flow works as specified: the recipe is deleted, appears on Deleted Recipes, is restored still favorited, shows up in the meal-plan picker, and the phone-width link opens Deleted Recipes. The one visible defect is a one-frame 'No recipes yet' empty-state flash when the search is cleared, and the picker briefly shows an unfiltered list.

<sub>14.9 s recording at 25 fps · every frame compared · 17 moment(s) of change, 0 flash(es) · 17 panel(s) shown to the judge · judge claude-sonnet-5-5 read 17/17, $0.113, 19 s. Runner verdict: green.</sub>

> Reviewer: this clip and the findings table are this PR's evidence of how the UI behaved, from a recording of the real run, watched frame by frame. A review sandbox cannot start a browser, so read them instead of trying one, and say in your review which finding (or "none") you relied on.
