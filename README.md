# Work Tab for Colony Manager Redux

A Labor tab for [Colony Manager Redux](https://github.com/ilyvion/colony-manager-redux), which it
requires — this mod does nothing on its own.

**[Available on the Steam Workshop »](https://steamcommunity.com/sharedfiles/filedetails/?id=3782418159)**

The other manager tabs answer "how much wood should we have?". This one answers "who should be
cutting it, and when?" — it reads the stock targets already configured on those tabs, watches how
fast each stockpile is actually refilling, and writes an hour-by-hour work schedule for every
colonist.

## How it works

**Demand.** Every managed job on the map that has a stock threshold becomes a demand row: current
count, target, and the measured change per day. A demand's pull is how far below target it is —
plus, if it's satisfied but draining fast enough to fall back through the band within a day, a
little pull in advance.

**Hysteresis.** A resource is worked until it reaches its target, then left alone until it falls
back to a configurable fraction of target (85% by default). Between those two points the previous
decision stands, so labor doesn't flip between wood and steel every cycle. When the wood pile is
full, the pawns cutting it are free for whatever is still short.

**Who.** Pawns are scored per work type on what the game already tracks: the work-speed stats that
actually govern that work (`MiningSpeed`, `PlantWorkSpeed`, `ConstructionSpeed`, `ResearchSpeed`,
…), skill level, and the body capacities the work leans on. Scores are normalised within a work
type, so "best miner" and "best researcher" are comparable and the demand weight decides between
them.

- **Train vs. produce** slides between assigning whoever produces most right now and whoever learns
  fastest with the most room to grow. Passion feeds both ends, but dominates the training end.
- **Injuries** are read as capacity levels against the work's needs. A pawn whose Moving is
  impaired stops being scheduled for hauling — their slow trips cost the colony more than the
  hauling gains — while manipulation-light work is unaffected.

**When.** Each pawn's schedulable hours come from their own timetable. A hard cap limits scheduled
hours per day; past it, and in any hour the plan leaves empty, the pawn falls back to their own work
priorities rather than standing idle. Runs shorter than the minimum block get extended or dropped so
nobody spends the day walking between jobs.

**Well-being.** When a pawn's mood is under the mood floor, hours come back off their schedule,
scaled by how far under they are.

**Rotation.** A demand can require a minimum number of workers every hour. Those slots are filled
first, always by whoever has the fewest hours so far, so research or doctoring stays manned around
the clock without one colonist working a 24-hour shift. Add a standing demand for work that has no
stockpile target of its own.

Every option is set for the colony and can be overridden for a single pawn: select a colonist in
the list and any option you touch becomes theirs alone. Demands can likewise be pinned to specific
colonists.

## Applying the schedule

By default the plan is applied by rewriting vanilla work priorities at the top of each hour: the
scheduled work goes to priority 1 and everything else the pawn is willing to do drops behind it.
Work types the player switched off stay off.

If [Enhanced Work Tab](https://steamcommunity.com/sharedfiles/filedetails/?id=3715873875) is
loaded, the whole day's schedule is written into its hour-aware priorities instead, so the schedule
is visible in the work tab. That integration is optional and bound by reflection; if the method
signature it needs can't be identified, it says so on the tab and falls back to the vanilla path.

Each colonist's priorities are captured before the manager first touches them. That baseline is what
they fall back to, and what's restored if you stop managing a pawn or delete the job.

## Building

Requires the .NET SDK. Colony Manager Redux and ilyvion's Laboratory are referenced from the Steam
workshop install; override the paths if yours are elsewhere:

```bash
dotnet build Source/WorkManager.csproj -p:WorkshopPath="D:\Steam\steamapps\workshop\content\294100"
```

Output goes to `1.6/Assemblies/`.

## Requirements

- RimWorld 1.6
- Colony Manager Redux (and its own dependency, ilyvion's Laboratory)
- Enhanced Work Tab — optional
