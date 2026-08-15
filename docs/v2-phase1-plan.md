# v2 Phase 1 — Implementation Plan

**Goal:** replace the `Criticality × Urgency` + `MinWorkers`/`MaxWorkers` model with a
single **numeric importance** per demand, tag each demand `Resource` vs `Continuous`,
and restructure the planner into **rank → saturate → never-idle backfill**. No
consumption aggregator, no shifts, no new game-state reading — those are later phases.

The good news from reading the code: the planner is *already* discrete whole-pawn-
per-hour with diminishing-returns saturation (`FillPass`). Phase 1 is mostly a
subtraction (delete the coverage/bounds machinery) plus a rename and a small backfill
pass. Ties fall out for free (see step 3).

## What the player sees afterward

- Each demand row has one **Importance** number (integer, reusable across rows).
  Equal numbers → equal labor share.
- No more Min/Max worker buttons.
- Idle pawns are actively kept busy: any capable pawn with an open hour is put on the
  best work still worth doing, and only truly-uncoverable hours fall back to baseline.
- "Add standing demand" → "Add continuous demand"; rows are grouped conceptually as
  Resource (has a stock target) vs Continuous (research/cleaning/etc.).

---

## Step 1 — Model: `DemandConfig` + `LaborDemand` + `DemandKind`

**File:** `Source/Model/LaborDemand.cs`

Add the kind enum (transient — never scribed; derived at build time):

```csharp
public enum DemandKind { Resource, Continuous }
```

`LaborDemand`:
- Add `public required DemandKind Kind { get; init; }`.
- Change the weight formula to the unified scale:
  `public float Weight => Urgency * Config.Importance;`
  (Resource demands supply a 0..1 `Urgency`; continuous demands set `Urgency = 1f`, so
  one formula covers both — see step 2.)
- `StatusLine()`: the standing branch currently prints `Config.MinWorkers`. Replace with
  a continuous-appropriate line (e.g. importance / "continuous"). Keep the threshold
  branch unchanged.

`DemandConfig`:
- **Rename** `Criticality` → `Importance` (still a `float`, edited as an integer 0–10).
- **Delete** `MinWorkers` and `MaxWorkers` fields.
- Keep `Standing`, `Enabled`, `PinnedPawns` (Standing is the persisted source of truth
  for Kind; pins and enable/disable survive into later phases).
- `ExposeData` — migrate (see step 7).
- `Clone()` — drop the Min/Max lines, add `Importance`.

## Step 2 — Demand source: `DemandModel`

**File:** `Source/Model/DemandModel.cs`

- Enable/skip gate: `config.Criticality <= 0f` → `config.Importance <= 0f` (two places:
  the threshold loop and the standing loop).
- Threshold demands: set `Kind = DemandKind.Resource`. Weight/urgency logic unchanged.
- Standing → **continuous** demands (the second loop):
  - `Kind = DemandKind.Continuous`
  - `Urgency = 1f` (so `Weight == Importance`; ordering is pure importance)
  - `RemainingWork = PawnAvailability.HoursPerDay` — a non-zero sentinel so the demand
    stays *eligible* all day. Actual spread is limited by the fill saturation curve, not
    by a worker count. (Old code used `HoursPerDay * MinWorkers`; MinWorkers is gone.)
- `AddStandingDemand` / `RemoveStandingDemand`: keep behavior, drop the
  `MinWorkers < 1 → 1` line. Optionally rename to `AddContinuousDemand` (updates two call
  sites in `Schedule.cs`); renaming is clearer but not required.
- `Evaluate` still `SortByDescending(d => d.Weight)` — unchanged.

## Step 3 — Planner: delete coverage/bounds, add backfill

**File:** `Source/Plan/LaborPlanner.cs`

**Delete:**
- `CoveragePass(...)` entirely, and its call + the `if (colonyOptions.Flag(KeepCriticalManned))`
  guard in `Build`.
- In `Eligible(...)`, the `MaxWorkers` block (`if (demand.Config.MaxWorkers > 0 ...)`).
  `Eligible` loses its now-unused `workersThisHour` reason to exist for the cap, but the
  parameter is still used by callers for other demands — keep the signature, just remove
  the MaxWorkers check. (`workersThisHour` becomes unused inside `Eligible`; drop the
  param there and at its two call sites to keep it clean.)

**Keep unchanged:** `FillPass` — it *is* rank→saturate. It picks the max
`Value = Weight × score × saturation`, and `saturation = 1/(1 + rate·AssignedHours)`
falls as a demand is staffed. **Ties are automatic:** two demands with equal `Importance`
alternate, because assigning to one raises its `AssignedHours` and drops its value below
the tied one on the next pick → balanced split, no special code.

**Add** `BackfillPass(candidates, demands)` — run it **after** `FillPass`, **before**
`SmoothingPass`:

```
for each candidate:
  for each open, available hour (CanTake):
    pick the demand the pawn scores highest on (ScoreFor > 0), ignoring RemainingWork
      and Weight — a satisfied demand still beats idling;
    if found -> Take(hour, workType, locked:false)
```

This keeps a pawn on *useful demanded work* once the deficit-driven fill is exhausted.
Hours where the pawn can do no demanded work stay `null` and fall to baseline priorities
via the applier (the existing `NeverIdle` safety net — unchanged). *Active backfill into
non-demand work (e.g. stonecutting when nothing demands stone) needs generalized scoring
and is deferred to Phase 2b.* Note this limitation in the plan doc, not silently.

## Step 4 — Options catalogue

**File:** `Source/Core/LaborOption.cs`

- Remove `LaborOption.KeepCriticalManned` from `LaborOptions.All` (hides it from the tab,
  the settings screen, and per-pawn overrides). Leave the enum member as reserved (a
  comment: "reserved for Phase 5 single-station/watch coverage") so saved option sets
  with an orphaned value load harmlessly.
- `NeverIdle` stays as-is (still governs the baseline fallback for uncovered hours).

## Step 5 — UI

**File:** `Source/ManagerTabs/ManagerTab_Labor.Schedule.cs`

`DrawDemandRow`:
- Replace the Criticality slider with an **Importance** control. Simplest that reads as
  "a number you can reuse": an integer stepper. Either keep `DrawLabelledSlider` snapped
  to whole numbers 0–10, or a `-/value/+` button trio. Write back to `config.Importance`
  and `job.InvalidateApplication()` on change.
- **Delete** the `minRect`/`maxRect` buttons block and the `NextBound` helper.
- The bottom row is now just the importance control (can widen to full width).

`OpenStandingDemandMenu` / button text / `OpenDemandContextMenu`:
- Relabel "standing" → "continuous" (string keys in step 6). The `RemoveStanding`
  context item and `Standing` checks stay functional.

## Step 6 — Keyed strings

**File:** `Common/Languages/English/Keyed/WorkManager.xml`

- Add `WorkManager.Demands.Importance` (+ `.Tip`).
- Add continuous-demand strings (`AddContinuous`, `AddContinuous.Tip`, `RemoveContinuous`).
- `Demand.StatusStanding` → a continuous variant, or repurpose.
- Leave the now-unused `Criticality` / `MinWorkers` / `MaxWorkers` keys in place or remove
  them; unused keys are harmless.

## Step 7 — Save compatibility / migration

**File:** `Source/Model/LaborDemand.cs` (`DemandConfig.ExposeData`)

Demands persist inside `DemandModel._configs` (deep-scribed dict keyed by `WorkTypeDef`),
reached from `ManagerJob_Labor.ExposeData`. Migrate in place with a sentinel:

```csharp
Scribe_Values.Look(ref Importance, "importance", -1f);   // -1 = absent (old save)
float legacyCriticality = 1f;
Scribe_Values.Look(ref legacyCriticality, "criticality", 1f);
Scribe_Values.Look(ref Standing, "standing");
Scribe_Values.Look(ref Enabled, "enabled", true);
Scribe_Collections.Look(ref PinnedPawns, "pinnedPawns", LookMode.Reference);
// minWorkers/maxWorkers nodes in old saves are simply no longer read → ignored.

if (Scribe.mode == LoadSaveMode.PostLoadInit)
{
    if (Importance < 0f)                                  // migrate old criticality
        Importance = Mathf.Clamp(Mathf.Round(legacyCriticality), 0f, 10f);
    PinnedPawns ??= [];
    PinnedPawns.RemoveAll(p => p == null);
}
```

- Old criticality (0–3, default 1) maps directly to importance; players gain 0–10 headroom.
- Orphaned `minWorkers`/`maxWorkers`/`KeepCriticalManned` nodes are ignored on load — no
  errors. New saves stop writing them.
- No change needed to `ManagerJob_Labor.ExposeData` itself.

---

## Verification

1. **Builds** after each step (order above keeps it compiling).
2. **Load an old v1 save** (pre-Phase-1) — no red errors; each demand's importance equals
   its old criticality; pins preserved. (Use `mcp__rimworld-claude-dev-tools` smoke test;
   see [[rimworld-devtools-verify]].)
3. **Ties** — set two resource demands to the same importance, confirm labor splits roughly
   evenly in the schedule grid.
4. **Backfill** — a colony with all demands satisfied should still show scheduled hours
   (pawns on best demanded work) rather than an empty grid.
5. **No idle regression** — a pawn who can do no demanded work in an hour still reverts to
   baseline priorities (NeverIdle path).
6. **Unit tests** — `LaborPlanner.Build` is pure/static; add table tests for: equal-
   importance split, saturation falloff still moves labor off a covered demand, backfill
   fills open hours, pins still restrict.

## Risks & deferred decisions

- **Backfill scope.** Phase 1 backfills only into *demanded* work + baseline fallback.
  Generalized best-work backfill (non-demand work types) waits for Phase 2b scoring.
  Documented, not silent.
- **Importance scale (0–10, integer).** Chosen for clear ties; revisit if players want
  finer control. Migration clamps, so widening later is safe.
- **`KeepCriticalManned` removal** leaves single-station/watch coverage unavailable until
  Phase 5 — acceptable, since Phase 1 explicitly defers shifts.
- **`Eligible` signature churn** — dropping the `workersThisHour` param touches two call
  sites; trivial but do it in the same commit to avoid a half-cleaned signature.
