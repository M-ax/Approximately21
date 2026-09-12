# Interaction foundation

`InteractionContext` contains the local `World` and full placed-block `Entity` (`Block`), suitable for `BlackjackTableRegistry.TryResolve`. It is never serialized. `InteractionButton.GetVisualState` supplies text/enabled state by context; shared definitions hold no per-table state. Rotation is in radians in the table's X/Z plane. Disabled controls block dispatch, and selecting an empty/disabled nearer surface never falls through to another table.

Labels use atlas-free uppercase 3x5 glyph meshes through EPC_Renderer/CRPRendererData, supporting digits, decimal punctuation, multiline text, and a fallback question mark. Text is bounded to 128 characters per control. Each placed control retains its mesh until text changes; enabled changes switch shared materials without regenerating glyphs. Definitions must be registered before configuration. ResetRendering releases instance caches, renderer children, meshes, materials, and fallback textures; Dispose is virtual for future subscription cleanup.

## Reusable seats (step 4)

`BlackjackSeatInteraction.RegisterFive(bounds, RegisterButton, viewProvider)` creates and registers all five groups before renderer configuration. `BlackjackTable` now supplies its felt bounds to `BlackjackInteraction`, which calls this factory and supplies cached live views through `BlackjackTablePresentation`. Each group exposes `SeatIndex`, `ActionRequested(context, action)`, and `WagerAdjustmentRequested(context, direction)`. The owner resolves local contexts through the runtime registry and handles all selection changes and command submission; the definitions neither access Steam nor mutate snapshots.

The calibrated profile uses material group 1 in `lib\BlackjackTableBundle\Built\BlackjackTableMesh.bin`: at model scale 0.1, X spans ±0.518180, Z spans −0.301947 to +0.316233. The curved player edge is **−Z**; +Z points inward toward the straight dealer edge. Layout uses an inward offset polyline and equal arc-length samples with equal end margins. `Create(width, depth)` returns physical offsets relative to bounds center; `CreateNormalized()` uses tabletop width as a uniform unit, retaining correct rotations. Do not independently scale rotated X/Z coordinates.

Managed tests check profile vertices against the exported felt mesh, all panel corners against its triangles, convex boundary containment, separating axes for every panel pair, and child-control separation. Every hand has its own label to avoid losing split-hand summaries to the 128-character glyph limit. Wagers use decimal arithmetic, start at 10, adjust by one, and only include an existing stake in available funds while betting. Availability consumes validated snapshots and readiness/pending flags; hidden engine constraints remain host-authoritative.

Live readability, physical player-facing orientation, and rendered/hit-tested alignment of these small panels still need in-game acceptance. Step 5 enables the panels, but no two-peer Unity/Steam session was available for live validation.

## Live acceptance still required

Managed geometry/ownership tests and an interop build are not proof of Unity rendering behavior. No compatible running game session was used in this step.

- Place two tables on one ship. Verify their renderer Parent chains reach distinct SCPrefab + NetcoreEntity blackjack blocks, and click contexts resolve to those blocks (including Entity.Version), not the ship or renderer.
- Verify EPC conversion retains zero-area marker meshes for label entities, and changing CRPRendererData updates cached glyph meshes/materials in the game renderer.
- Check rotated, translated, and scaled ship/table hierarchies. LocalTransform plus PostTransformMatrix carries the exact relative basis; confirm the game's transform-update timing keeps hit regions and rendered labels aligned.
- Verify the rendering camera's forward (+Z) axis is the viewing direction. Reverse-ray fallback was deliberately removed to prevent hits behind the camera.
- Overlay two tables in the view. A click on a disabled control or blank area of the nearer surface must not activate the farther table.
- Lose/recreate renderers, replace the world/core, and unload the plugin. Confirm markers are rebound, pointers hidden, and no stale glyph/material references or duplicate prefab children remain.