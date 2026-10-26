# Euphoria hit reactions

Meitou-only (setting "Physical hit reactions", on by default): a hit character becomes a physical
body for a few seconds that is pushed by the blow, steps to keep its balance, and falls when it
cannot. The bodies are [Delirium Physics](https://github.com/PVCPC/delirium-physics) (GPL-3.0-only,
a reimplementation of NaturalMotion's euphoria as GTA IV runs it), vendored in `native/delirium`.

## Library and data

- `native/delirium/build.sh` builds `lib/linux-x64/libdelirium_physics.so` and
  `lib/win-x64/delirium_physics.dll` (mingw; imports only system DLLs). The Simulation project copies
  both beside the program. Meitou's additions to the SDK: `nm_prepare_from_game`, `nm_init_art`
  (`.art` byte order; GTA IV PC files are little-endian, read big-endian the body never moves),
  `nm_build_fit_mass`.
- The characters come from the player's own GTA IV (never shipped): `EuphoriaData.Shared` imports
  them once into `%LOCALAPPDATA%\Meitou\euphoria` (`fred.art`, `wilma.art`, `build-male.txt`,
  `build-female.txt`) on a worker thread. GTA IV is found by `UserConfig.Gta4Folder`, then
  `MEITOU_GTA4`, then `Grand Theft Auto IV[/GTAIV]` beside Kenshi in its Steam library. Without it,
  or until the import finishes, hits play their stumble clips. The log says `euphoria  bodies ready`
  or `euphoria  off: <why>`.

## Fitting to Kenshi's skeleton

`EuphoriaData.Fit`, per skeleton (male body from `fred`, female from `wilma`): the 21 Delirium parts
drive 20 Kenshi bones (`PartBones`; Kenshi has three spine bones to Delirium's four: `Bip01 Spine2`
follows the chest part). Limbs and the head are fitted to Kenshi's bind-pose joints; the trunk
(spine, chest, neck) keeps the source's proportions, turned and scaled onto Kenshi's pelvis, hips
and head: fitted to Kenshi's spine bones the parts came out 190 kg (**Verified** by probe on
`male_skeleton.skeleton`). Total mass is then set to 80 kg (man) and 65 kg (woman). Each bone rides
its part with the offset of the bind pose; the root bone follows the pelvis part.
Space: Kenshi (x, y, z) units = Delirium (x, −z, y) × 0.1 m, about each body's start point.

## A reaction

`Euphoria/EuphoriaSystem` runs serially after the animation and before the ragdolls.
- **Start** (`CombatSystem.Reactions`, every landed melee hit and bolt): the body starts in the
  pose on show and the character's velocity, with `bodyBalance`, in a scene of its own: a 14 m
  square of ground sampled every 0.5 m plus the building collision there.
- **Push**: an impulse along the hit on the part struck (the health part's bone), and `shot`
  (melee for blows). Strength = damage / (stumble threshold + 15); impulse = (15 + 40 × strength,
  strength ≤ 4) N s × 0.8 (cut) to 1.2 (blunt), × 0.6 for bolts. From strength 1.5 the steps it may
  take are limited (`maxSteps` 4, 2 from 2.5, 1 from 3.5). Measured on Kenshi's man and woman
  (probe): 1 staggers about a metre, 2 nearly two, 3 knocks over from the front, back or side.
- **Hold**: combat keeps the character helpless while it reacts (`CombatSystem.Hold`: no swing,
  block or walking); its position follows the pelvis.
- **Ending**: standing, once balancing with the centre of mass under 0.35 m/s (from 0.5 s, at most
  4 s, or 5.5 m from the start): it faces the way its body turned and blends back into the
  animation over 0.35 s. Fallen, once it has been down 0.6 s and moves under 0.5 m/s (at most 2 s):
  knocked out for 2.5 s and handed to its ragdoll (`IRagdollHandOff`: the ragdoll waits while the
  body is held and starts from its last pose), then gets up as from any knockout.
- **Down hits** (the hit knocked out or killed): the body relaxes (`bodyRelax` 60, no balance) and
  falls as pushed, then goes to the ragdoll (once still, at most after 3 s).
- **Cap**: 24 bodies; a new one past it replaces the farthest from the camera focus, or plays the clip.
- Not for animals, carried or caged characters, ones getting up or crawling on a downed leg.

Deterministic across runs and thread counts (**Verified** by probe: two Greenlanders fighting
30 s, threads 2 and 4). Not saved: a reaction in progress is lost on load.
Cost (probe): about 0.4 ms per body per 1/60 s step on one thread; bodies step in parallel.

Gaps: no reaction for knockouts and deaths from bleeding, falls, or being dropped; the bodies do
not collide with each other or with other characters; target scoring does not count a reacting
character as staggered (it looks for a stumble clip).
