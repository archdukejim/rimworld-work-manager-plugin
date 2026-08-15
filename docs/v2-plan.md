# Work Manager v2 — Design Plan

The Labor tab for Colony Manager Redux (CMR). v1 answers "who cuts the wood, and
when?" by reading CMR's stock thresholds and writing an hour-by-hour schedule.
v2 keeps that engine but changes what a *demand* is.

## The core reframe

v1's demands are flat and homogeneous: every row — wood, research, doctoring —
is a `LaborDemand` with the same shape (`Weight = Urgency × Criticality`, plus
`MinWorkers`/`MaxWorkers`). Resource rows get `Urgency` from a CMR
`Trigger_Threshold` deficit; everything else is faked as a "standing demand" with
no real signal.

v2: demands are **two typed things on one comparable weight scale**, so the
player can say "wood-when-critical outranks research" and the planner means it.

- **Resource / fabrication demands** — wood, steel, meals, medicine, components.
  Have a stock signal (target + a new *critical* threshold) and a measured/
  projected consumption rate. Weight is dynamic: low when healthy, spikes below
  critical.
- **Continuous demands** — research, cleaning, hauling. No stock signal; a
  **static weight** meaning "how much colony labor I want this to hold." This is
  where "threshold for research" lives — not a stock number, a weight on the same
  axis as the resource weights.

Because both live on one scale, the "compare wood vs research" behaviour is
automatic: research has static weight `W_r`; wood's weight is a function of how
far it is below critical. Wood healthy → weight < `W_r` → research wins. Wood
below critical → weight > `W_r` → cutters get pulled. No mode switch, no special
case.

## Explicitly out of scope: emergencies

Firefighting, doctoring, surgery, patient/rescue are **not** managed. The player
drafts pawns for those manually, which overrides work priorities anyway, so the
manager staying out of the way is correct. These work types are simply never
demanded and keep their vanilla/baseline priority. No emergency category, no
preemption loop.

## The one-sentence goal

The player says what's important (make clothes, do research, keep wood above X);
the mod finds the **best whole-pawn allocation** to achieve it, and **never leaves
a pawn idle**. Everything below serves that.

## Discrete assignment: rank → saturate → backfill

No fractions. No "40% of labor." Pawns are whole and assigned to one work type at
a time (per hour, since timetables and demands shift across the day). `MinWorkers`/
`MaxWorkers` sliders are gone; the same intent comes out of three mechanics:

### 1. Numeric importance (ties allowed)
Each demand carries a **number** the player sets — higher is staffed first. The
player can **reuse the same number** on several demands to say "these matter
equally," and equal numbers get **equal** whole-pawn allocation (round-robin
between them so neither starves). Resources modulate their effective number
dynamically — it climbs as stock approaches and crosses critical — while continuous
jobs hold the static number given. One shared numeric axis; ties are a feature, not
an accident.

### 2. Saturate to an integer pawn count
A demand only pulls as many *whole* pawns as it actually needs. For a resource
that's the number of pawns whose combined effective throughput closes the
projected deficit by the horizon:

```
pawnsNeeded = ceil( RemainingWork / perPawnThroughput )   // capped by diminishing returns
```

`RemainingWork` is the aggregator's projected net deficit (below). This replaces
`MaxWorkers`: one pawn who'll restock wood by tonight means the second-best cutter
is free for the next demand — computed, not hand-capped. **Reserve-a-pawn** (pin)
sits on top: a pinned pawn is assigned to its demand first, always.

### 3. Backfill — never idle
After important demands are staffed, every free pawn-hour is filled with the best
useful work that pawn *can still do* — cleaning, hauling, stonecutting, research —
even at low skill. A slow stonecutter in a clean room beats a pawn standing still.
Backfill only uses hours the timetable already allows and respects the mood floor,
so needs/recreation/rest are never trampled; it just refuses to schedule *nothing*.
This is v1's `NeverIdle` option promoted to a core guarantee.

### Coverage jobs → the shift system
Two jobs care about *coverage over time*, not pawn count: single-table research
(keep the one bench busy) and nursing (tend/feed patients around the clock). Both
are opt-in and handled by the shift system — see **Shifts, watches, and areas**
below — rather than by rank-and-saturate. This is the only surviving piece of the
old MinWorkers/rotation machinery.

## The consumption aggregator (new subsystem, v2 core)

Today the mod has **zero** direct bill/stockpile awareness — `RatePerDay` is just
exponential smoothing of observed stockpile change, which is backward-looking. A
queued 500-steel build doesn't register until it's already draining you.

`ConsumptionModel` walks:
- **Active bills** — `map`'s bill givers → `BillStack` → active bills → recipe
  ingredients × repeat/target count.
- **Construction** — blueprints and frames on the map → `ThingDef.costList`.

…and produces **per-ThingDef projected consumption/day**. Combined with produced/
day (from currently-assigned labor throughput), each resource demand gets a real
net-flow signal and an ETA to critical. This is what makes "work is a
resource-management view" literal — every resource row is a fabrication ledger:

```
Steel   842 / 1000  (crit 400)   +120/day made   −310/day queued   →  −190/day   ⚠ hits critical in 2.3d
```

The aggregator feeds two things: the **weight** (net deficit vs critical) and the
**saturation count** (`RemainingWork`).

**CMR checked — it doesn't help here.** CMR reasons purely about current stock vs
target and computes **no per-day consumption or drain rate** anywhere (confirmed in
`Trigger_Threshold` and `History`). Its closest feature —
`ManagerJob_Production`'s linked-producer *ingredient demand*
(`AggregateLinkedDemand` / `ComputeIngredientDemand`) — is a **stock buffer target,
not a rate**, spans only linked production jobs, and is `internal`. So we build the
walker ourselves. (We *could* difference CMR's `History` stock series for a cheap
*observed* drain to complement the *projected* one, but that's `internal` too and
would need reflection — the bill/blueprint scan is the primary signal.)

## Shifts, watches, and areas

Coverage-over-time jobs, all opt-in.

**Single-station rotation (e.g. research).** One bench serves one pawn at a time,
so more pawns don't help — the win is keeping it busy. The mod rotates pawns
through the single slot, picking whoever has the fewest hours so far so nobody
pulls a full 24-hour shift.

**Care watches (feed / clean / care for dependents).** Player choice, and richer.
A watch keeps a pawn awake off-hours to perform the **need-tending sub-jobs** that
keep dependents alive — *feed, clean, care* — across whichever of these categories
the player enables:

- **Patients** → Doctor (tend + feed downed colonists)
- **Babies / nurseries** → Childcare (feed and tend infants)
- **Prisoners** → Warden (feed and tend prisoners)

Each category is its own gated watch:

- **Optional 24-hour watch.** Setting a watch **edits the assigned pawn's sleep
  timetable** so someone is awake to cover those sub-jobs when a need appears.
- **Condition-gated ("when needed").** No patient / no baby / no prisoner → that
  watch doesn't fire and the pawn sleeps normally. No wasted vigils.
- **Light-duty, hourly check.** A watcher can still do other work as long as they
  **check every hour** for a tending need; the need preempts when it appears.
- **Shift-only area.** The watcher can be confined to an area (the infirmary,
  nursery, prison block) **only during the scheduled shift**, reverting off-shift.

A "nurse" is therefore not a new role — it's whatever capable pawn (Doctor /
Childcare / Warden aptitude) the player assigns to a watch. The sleep-timetable
edit and shift-only area are **new capability** — v1 only *reads* the timetable.
Model them as a time-boxed overlay: the shift owns the pawn's sleep + area for its
window, then hands them back cleanly (same capture/restore discipline as
`BaselineStore` uses for priorities).

## Pawn scoring from the full stat table (speed × chance)

v1 scores pawns mostly on **speed** stats (`MiningSpeed`, `PlantWorkSpeed`,
`ResearchSpeed`, …) plus skill and capacities. v2 scores from the **whole work
stat table** — both the *speed* columns and the *chance / quality* columns —
because in a resource model the chance stats change how much is made and wasted,
not just how fast:

| Work type | Speed stat | Chance / quality stat | Resource effect |
|-----------|-----------|----------------------|-----------------|
| Construct | ConstructionSpeed | **Construct success chance** | failures waste the full cost → *adds* consumption |
| Cook | CookSpeed | **Food poison chance** | poisoned meals = wasted ingredients + downtime |
| Grow / harvest | PlantWorkSpeed | **Plant harvest yield** | directly scales food/wood *produced* per work-hour |
| Hunt | HuntingSpeed (global) | **Hunting stealth** | miss/flee = wasted time, no meat |
| Handle / tame | AnimalGatherSpeed | **Tame animal chance** | failed tames waste the attempt |
| Doctor | MedicalTendSpeed | **Medical tend quality** | outcome quality (not stock, but real) |
| Warden | (global) | **Negotiation / recruit** | success-weighted throughput |
| Mine | MiningSpeed | — | speed only |
| Research | ResearchSpeed | — | speed only |

So the scorer's throughput term becomes **effective throughput = speed ×
success/yield**, normalised within a work type as today. Two payoffs:

- **The ledger gets more honest.** A builder with 83% construct success consumes
  ~20% more materials per structure; `ConsumptionModel` can attribute that waste,
  and the planner prefers the high-success builder when materials are tight.
- **Data-driven, not hardcoded.** `WorkTypeProfile` currently hardcodes a
  stat/capacity map per work-type defName with a weak fallback. v2 should make the
  speed-stat + chance-stat + capacity set a small def/table so modded work types
  and the exact columns above are declared, not baked into C#.

### Passion and traits — potential per hour, not raw skill
Fastest ≠ should-do-it. Passion should pull a pawn toward a job (they learn faster
and want to be there), but passion is **tempered by the traits that govern actual
speed**: a passionate pawn with **Sloth** (global work-speed penalty) may deliver
less per hour than a calm, quick worker, so their passion edge shouldn't
auto-assign them. The assignment score is therefore **max potential per unit
time** — passion × effective speed (traits included) × success/yield — not skill
in isolation. v1 already has `RespectPassions` and a `GrowthBias` (train-vs-
produce) slider; v2 sharpens it so trait-driven speed can *cancel* a passion rather
than the two being scored on separate axes.

## Three-band resource thresholds

v1 is two-band (resume-below 0.85 → target). v2 is three-band:

- below **critical** → escalated weight; may outrank continuous demands
- critical → target → normal proportional pull (today's behaviour)
- above target → satisfied; hysteresis holds until it falls back to resume-below

**Critical is an absolute count, and flagged experimental.** The player sets an
absolute number (not a fraction of target). It's powerful and blunt: a poorly
chosen critical can whipsaw the colony — everyone slams onto wood, other stocks
sag, then those cross *their* critical, and labor thrashes. The UI labels it
**experimental** with that warning. The bet is that the three-band **hysteresis**
we're already building damps the oscillation and settles levels over time when the
values are set sensibly. Target still comes from CMR; critical is ours.

**Where the player sets it — our tab always; inline on CMR's tabs is optional.**
Two layers, the same graceful-degradation discipline as the Enhanced Work Tab
integration:

- **Store (always).** The value lives on a `ManagerJobComp` injected into the
  relevant `ManagerDef`s' `jobComps` by **XML PatchOperation** — clean scribe via
  `PostExposeData`, no Harmony, no reflection for our own field.
- **Edit — baseline (always).** Critical is always editable on **our own Labor
  tab** (or demand row). This path never depends on Harmony and is the guaranteed
  UI.
- **Edit — inline on CMR's tabs (optional, Harmony).** *If Harmony is available and
  the patch binds*, a **postfix on `Trigger_Threshold.DrawTriggerConfig`** appends
  the critical slider under CMR's target slider, reading/writing the comp value.
  **If Harmony is absent or the method signature can't be matched, we simply don't
  draw there** — no error, the value is still set from our tab. Mirrors how
  `EnhancedWorkTabApplier` says so and falls back.

So Harmony is a **soft** dependency: the feature it buys (nicer placement) is
optional, and the mod runs fully without it. HarmonyLib is already loaded by
CMR/RimWorld anyway, so in practice it's present; we just never *require* it. Note
the soft-dependency in About/README on ship.

## Temporary priorities (manual surge)

Separate from the *automatic* escalation a critical threshold produces, the player
needs a **manual surge lever**: "wood NOW — it's deep winter, the heaters are about
to die, and the colony freezes tonight." A surge:

- **Overrides saturation.** Normal rank-and-saturate might put one cutter on wood
  because one cutter restocks by tomorrow. A surge says *tonight*, and floods as
  many capable pawns onto it as the player dials — past the computed ceiling.
- **Outranks everything** it's set above, temporarily — clothes and research wait.
- **Is time- or condition-boxed.** It expires after N hours/days, or clears when a
  target is hit (wood ≥ X), then labor snaps back to the normal plan. This is the
  intensity dial above "reserve one pawn" (pin): pin = dedicate one; surge = flood
  many, briefly.

Implementation-wise a surge is a transient, high-priority pseudo-demand injected
ahead of the ranked list with an expiry, evaluated each plan cycle and dropped when
spent. It reuses the same assignment engine — it just jumps the queue and lifts the
saturation cap.

## UI: the fabrication ledger

The demands section (`ManagerTab_Labor.Schedule.cs`) splits into two groups:

- **Resources** — ledger rows (stock / target / critical, made/day, consumed/day,
  net, ETA), a critical-threshold control, and one weight slider ("how hard to
  defend this").
- **Continuous** — research / cleaning / hauling, each a single weight slider
  ("how much labor this holds"). The old "Add standing demand" menu becomes "Add
  continuous demand."

No more Min/Max cycling buttons. Pinned-pawn and enable/disable stay.

## Code impact map

The engine seam is clean: `LaborPlanner.Build` is pure/static
(pawns + demands + options → plan). Most change is *upstream* (richer demands).

| Area | File(s) | Change |
|------|---------|--------|
| Demand model | `Model/LaborDemand.cs`, `DemandConfig` | add `DemandKind {Resource, Continuous}`, `CriticalThreshold`, `ConsumptionPerDay`, `SingleStation` flag, surge (`ExpiresTick`/`ClearAtCount`); drop `MinWorkers`/`MaxWorkers`; kind-aware weight/ordering |
| Consumption | **new** `Model/ConsumptionModel.cs` | bills + blueprints → per-ThingDef/day |
| Demand eval | `Model/DemandModel.cs` | classify sources, three-band resource logic, unify weights, wire consumption |
| Planner | `Plan/LaborPlanner.cs` | rank → integer saturation → never-idle backfill; single-slot rotation for flagged jobs; surge jumps the queue and lifts the cap |
| Classification | `Model/WorkTypeProfile.cs` | which work types are continuous vs resource-backed vs ignored (emergencies); make the speed-stat + **chance-stat** + capacity map data-driven |
| Scoring | `Model/PawnScorer.cs` | throughput = **speed × success/yield** using the chance columns; expose per-pawn effective throughput so the ledger can attribute waste |
| Apply | `Apply/VanillaScheduleApplier.cs` | leave emergency work types at baseline (don't demote) — small, optional |
| Shifts/areas | **new** `Plan/Shift*` + apply overlay | sleep-timetable + area overlay for a scheduled, condition-gated watch; capture/restore like `BaselineStore` |
| UI | `ManagerTabs/ManagerTab_Labor.Schedule.cs` | grouped demands + ledger rows; numeric importance field; watch/area controls; remove Min/Max buttons |
| Settings | `ManagerJobs/Settings/ManagerSettings_Labor.cs` | default critical fraction; drop Min/Max defaults |
| CMR integration | **new** `Comps/CriticalThresholdComp.cs` + XML patch | `ManagerJobComp` (injected into `jobComps`) *stores/scribes* the absolute critical + baseline edit on our tab — no Harmony; read back via public `Trigger_Threshold` |
| CMR inline (optional) | **new** `HarmonyPatches/` (guarded) | optional postfix on `Trigger_Threshold.DrawTriggerConfig` to draw critical inline; binds if Harmony present, silently skips otherwise |
| Dependency | `WorkManager.csproj`, `About.xml` | HarmonyLib as a **soft** ref (already loaded by CMR); patch guarded so absence just disables inline draw |
| Save compat | `ManagerJob_Labor.ExposeData` | migrate old `_demands` (Min/Max → numeric importance) on load |

## Phasing — epics

Tracked in the issue tracker as epics #1–#7, roadmap #8. Each epic has one target
goal and lands something real; the destructive refactor (#2) is never first.

1. **Visibility** — fabrication ledger (stock/target, made/day, used/day, net, ETA),
   read-only. Foundation; front-loads the biggest TPS risk (`ConsumptionModel`).
2. **Control model** — typed demands + numeric importance (ties allowed); remove
   Min/Max; planner rank → saturate → never-idle backfill. Behavior-preserving.
3. **Reaction** — absolute critical thresholds (experimental) + three-band
   escalation + integer saturation count. Depends on #1, #2.
4. **Right pawn** — potential-per-hour scoring (passion × trait-aware speed ×
   success/yield); low-success waste feeds the ledger. Pairs with #1.
5. **Coverage over time** — care watches (feed/clean/care over Doctor / Childcare /
   Warden, condition-gated) + single-station research rotation; sleep/area overlay.
   Depends on #2.
6. **Manual command** — pin (reserve a pawn) + temporary surge (queue-jumping,
   cap-lifting, expiry/target-hit). Depends on #2; stronger after #3.
7. **Integration & polish** — critical inline on CMR tabs via `ManagerJobComp` +
   optional guarded Harmony postfix; save-migration hardening; tuning. Depends on #3.

### Cross-cutting: performance & execution framework
Keep the per-cycle calculation off the hot path — cache/incrementalize the
bill+blueprint scan, and where possible **snapshot on the main thread, compute
off-thread, apply back on-thread** (RimWorld `Def`/`Thing` reads are not
thread-safe). Tackled first in #1. Must not tank TPS.

## Resolved (from discussion)

- **No fractions** — whole-pawn assignment only.
- **Rotation** — retired except **single-station 24/7 jobs** (single-table
  research, nursing): one slot, keep it manned, rotate by fewest-hours.
- **Scoring** — max potential *per hour*: passion × effective speed (traits
  included) × success/yield; a Sloth trait can cancel a passion.
- **Temporary priorities** — a manual, time/condition-boxed surge that jumps the
  queue and lifts the saturation cap; distinct from automatic critical escalation.
- **Continuous saturation** — non-resource jobs have no target cap; they're
  staffed by rank until pawns run out, then backfill soaks up the rest.
- **Emergencies** — out of scope; vanilla + manual drafting.
- **Importance is numeric with ties** — equal numbers = equal allocation; not a
  strict rank.
- **Diminishing returns — dropped.** One pawn per resource is how it lands
  naturally; pawn movement/behavior is out of scope, so no interference modeling.
- **Nursing = watches**, not a demand: opt-in, condition-gated, edits sleep +
  area for the shift window.
- **Consumption — build it ourselves.** CMR computes no drain rate; the
  bill/blueprint walker is required (CMR verified).
- **Critical value = absolute, experimental.** Player sets an absolute count;
  UI-flagged experimental; hysteresis expected to settle the oscillation.
- **Critical UI.** Always editable on our own tab (comp-stored, no Harmony). Inline
  on CMR's tabs is an **optional** Harmony postfix that draws only if it binds —
  else it silently doesn't, value still set from our tab (Enhanced-Work-Tab-style).
- **Harmony is a soft dependency** (already loaded by CMR; never required). Note it
  in About/README on ship.
- **Watches = care watches** (feed/clean/care) over Doctor / Childcare / Warden,
  each condition-gated. A "nurse" is any capable pawn assigned, not a new role.

## Open questions

- **Anti-thrash tuning for absolute critical.** With absolute criticals, two
  resources can ping-pong. Is three-band hysteresis alone enough, or do we want a
  per-cycle cap on how fast labor can swing onto a newly-critical resource?
- **Watch category granularity.** One watch covering all enabled categories
  (patients + babies + prisoners) for the assigned pawn, or separate watches per
  category (a night doctor *and* a night warden)? Leaning: per-category, since the
  best pawn and area differ.
