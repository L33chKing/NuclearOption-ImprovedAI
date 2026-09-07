# Improved AI

Vanilla AI drives its tanks bumper-to-bumper down the road into the same kill zone. Improved AI gives ground, air and naval AI a behavior changes: tanks spread out, halt to fire, and back out of losing fights nose-first. Planes  notch, laser, or spend an interceptor on what's about to hit them. Ships  form up and screen.

It's host-side: the AI is simulated by the host, so as a pure client on a server without it the mod does nothing.

---

## Features

**Missile self-defence** (skill-scaled)
- Pilots that can't beat an incoming missile with countermeasures decide between notching, holding a laser on it, or firing an interceptor — geometry, timing, turn rate and skill all factor in. Once an interceptor is inbound, they stop evading and get back to work.
- Skilled ground vehicles retask a gun onto missiles coming directly at them.

**Ground combat AI**
- Per-unit behaviour: push, halt-and-fire, fall back when outmatched (before the first shot, not after), pursue last contact. No squad scripts, just a few rules.
- Off-road maneuvering with building avoidance, water-aware routing, and spacing so lines fan out instead of stacking.
- Retreats reverse nose-first with guns on the enemy; outranged units turn and drive. Forward momentum is braked off before backing — no retreating *toward* the enemy.
- Player orders and mission hold-position are always respected, never overridden.

**Ground targeting**
- Faster acquisition (detectors scan sooner, elites re-pick targets sooner) and closest-first scoring among targets the weapon can actually hurt.
- Skilled crews fire all ready stations at the same target, salvo missiles across several targets without overkilling, and hold fire in ambush until the range is right.

**Skill, veterancy, traits**
- Unit skill sliders widened from 2 to 4 in the mission editor, effective skill up to 8 through faction multipliers and veterancy. Skill 4+ unlocks elite stat boosts (durability, guns, engines).
- Units earn veterancy from kills (damage-share credit) and level up; spectate any unit to see its rank and current orders.
- Random behavioural trait lottery plus mission-editor assignment (stored in the mission file, safe for players without the mod).

**Naval formations**
- Free warships inside 10 km auto-form a task force around the highest-cost ship: escorts take a front-preferred ring, small craft screen arcs toward the enemy, carriers hang back. Player orders detach a ship; it rejoins when stopped.

**Logistics**
- Units that run their main weapon dry road-march to the nearest supply, refill, and return (holders return to their exact spot). Unarmed AA and trucks flee threats and shelter behind engaged friendlies instead of driving to their deaths.

---

## Requirements

- Nuclear Option 0.34.1 (built and tested on this build)
- BepInEx 5.x
- BepInEx.ConfigurationManager 18.4.1 (the in-game config UI, F1)

## Installation

Via NOMM: install through the [Nuclear Option Mod Manager](https://github.com/Combat787/NOMM) and pick Improved AI from the list.

Manual:
1. Install BepInEx 5 and ConfigurationManager.
2. Grab the latest `ImprovedAI-vX.Y.Z.zip` from Releases.
3. Extract so the DLL lands at `BepInEx/plugins/ImprovedAI/ImprovedAI.dll`:

```
BepInEx/plugins/ImprovedAI/ImprovedAI.dll
```

Launch a mission and press F1 to configure.

## Building from source

Single C# file set (`src/*.cs`, one partial class) compiled straight with Roslyn against the game's own assemblies, no NuGet. Fix the paths at the top of the script if your install differs, then:

Windows:
```
powershell -ExecutionPolicy Bypass -File src/build.ps1
```

Linux:
```
bash src/build.sh
```

The script compiles outside the plugins folder (BepInEx would load a duplicate) and deploys the one canonical DLL.

## License

MIT
