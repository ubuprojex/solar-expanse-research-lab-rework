# Research Labs Rework

A two-part mod for Solar Expanse:

1. **A Teddit mod** (`mods/research_labs_mod/`) that reworks research labs:
   - The basic research lab now needs **double the workers (40)** and gives **double** research.
   - Adds a **second tier** of four advanced labs (200 workers, one per body), each consuming a
     resource per day and gated behind a matching technology:
     - **Advanced Electronics Lab** — consumes Electronics — unlocked by *Circuit Production in Space*
     - **Advanced Materials Lab** — consumes Exotic Alloys — unlocked by *Advanced Alloying Techniques*
     - **Advanced Fission Lab** — consumes Fissiles — unlocked by *Radioactive Isotope Isolation*
     - **Advanced Fusion Lab** — consumes Helium-3 — unlocked by *Helium-3 Extraction*
   - Labs use the game's `noBuildOnAsteroid` flag, so they can only be built on planets, moons,
     and dwarf planets — not spammed on asteroids.

   Gating is data-driven: each lab is `isLocked: true` in `facilities.yaml`, and `research.yaml`
   appends an `UnlockFacility` action to the matching vanilla tech (appended, so the tech's
   existing unlocks are preserved).
2. **A BepInEx plugin** (`plugins/ResearchLabsConsumption/`) that makes those labs actually pay their
   daily resource cost, stops their research when starved, shows the cost in the facility mouseover,
   and stamps `noBuildOnAsteroid` onto the new tier-2 labs (Teddit only applies that flag to existing
   facilities it patches, not to ones it creates).

## Why the plugin is needed

In Solar Expanse a facility's per-day behaviour is decided by its single `facilityItemClass`.
A `LabFacility` only runs research; only a `RefineryFacility` consumes resources — so a lab can
never natively consume anything. The labs therefore stay `LabFacility` (research works), keep a
`refinerInput` in their YAML, and the plugin reads that `refinerInput` each day and deducts it from
the body's stockpile. Without the plugin the labs simply research for free.

The plugin also gates research on supply: each day a lab runs **all-or-nothing**, the way a refinery
idles when starved. If the body can cover the lab's full daily input, it pays and researches; if it
can't, the lab consumes nothing and a Harmony patch on `LabFacility.GetBonusFromLab` zeroes that
lab's research bonus for the day — so a resource-less lab grants no research, exactly like turning it
off. Only the *enabled* count of each lab counts, so a manually disabled lab costs nothing.

The plugin also makes the in-world facility mouseover list the daily consumption. The build menu
shows it natively (via the `Refiner` ability on the YAML), but the running-facility tooltip is
class-gated and skips it for a lab, so the plugin appends the per-day rows itself (and marks the lab
"idle — no inputs" when it's starved).

Consumption is fully data-driven: the plugin charges **any** `LabFacility` that has a `refinerInput`,
so you can tune the cost entirely in the YAML.

## Installation

Requires [Teddit](https://github.com/ted505/solar-expanse-teddit) and BepInEx 5.4.

1. Copy `mods/research_labs_mod/` into `Solar Expanse/BepInEx/plugins/Teddit/mods/`.
2. Copy `plugins/ResearchLabsConsumption/` into `Solar Expanse/BepInEx/plugins/`.
3. Launch the game.

## Configuration

`BepInEx/plugins/ResearchLabsConsumption/ResearchLabsConsumption.cfg`:

| Setting | Default | Description |
| --- | --- | --- |
| `Enabled` | `true` | Master switch. When false, labs research but cost nothing. |
| `RateMultiplier` | `1` | Scales every lab's daily input rate. `2` = double cost, `0.5` = half. |
| `VerboseLogging` | `false` | Log each daily deduction and starvation event (noisy). |
| `DumpGameApi` | `false` | One-shot diagnostic: dumps facility/lab/refinery/research type members to the BepInEx log at startup, then does nothing. Used to discover game internals; leave off for normal play. |

The asteroid restriction has no config toggle. The base lab gets `noBuildOnAsteroid: true` from the
YAML (`mods/research_labs_mod/facilities.yaml`); the created tier-2 labs get it stamped by the plugin
at runtime (the plugin applies it to every `LabFacility`). To allow labs on asteroids again, you'd
remove the YAML flag and the `EnsureLabsNoBuildOnAsteroid` patch.

## Notes / limitations

- Consumption rides the daily life-support tick, so a staffed colony's labs are charged each day.
  A disabled lab (enabled count 0), which produces no research anyway, is not charged.
- Starvation is evaluated once per day. When a body can't cover a lab's full daily input that day,
  the lab idles: no resources are spent and it grants no research until supply returns. Research can
  therefore lag a daily tick behind a sudden shortage/recovery.
