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
- **More hits** on a body still reacting push it again (and restart `shot`); each earlier hit of
  the reaction makes the next count half again more (strength × (1 + 0.5 × hits)), so a character
  set on by several goes down. Probe: three strength-1 hits 0.4 and 0.3 s apart move it 0.9 m
  sideways, then 1.2 m in 0.4 s.
- **Hold**: combat keeps the character helpless while it reacts (`CombatSystem.Hold`: no swing,
  block or walking); its position follows the pelvis.
- **Ending**: standing, once balancing with the centre of mass under 0.35 m/s (from 0.5 s, at most
  4 s, or 5.5 m from the start): it faces the way its body turned and blends back into the
  animation over 0.35 s. Fallen, once it has been down 0.6 s and moves under 0.5 m/s (at most 2 s):
  knocked out for 2.5 s and handed to its ragdoll (`IRagdollHandOff`: the ragdoll waits while the
  body is held and starts from its last pose), then gets up as from any knockout.
- **Down hits** (the hit knocked out or killed): the body relaxes (`bodyRelax` 60, no balance) and
  falls as pushed, then goes to the ragdoll (once still, at most after 3 s).
- **Down without a hit** (bled out, a stealth knockout, knocked down while getting up): a character
  on its feet last tick and incapacitated now crumples the same way, with a light push in a random
  direction (**Verified** by probe: down 1 s, then the ragdoll). Characters already down when a game
  loads are left as they lie.
- **Off a ledge**: a body whose centre of mass falls faster than 5 m/s starts `highFall` (flails)
  and lands fallen (probe: pushed off a 4 m drop, flailing from 1.0 s, on the lower ground at 1.7 s).
- **Cap**: 24 bodies; a new one past it replaces the farthest from the camera focus, or plays the clip.
- Not for animals, carried or caged characters, ones getting up or crawling on a downed leg.

## Blade contact

`Combat/BladeContactSystem` (with the euphoria bodies, same toggle; after them in the slow-world
step). Combat's rolls still decide hit, block or miss; this makes a hit look like one.
- **Blade**: the weapon in the `hands` point (`Bip01 Prop2`, a child of `Bip01 R Hand`) runs along
  its mesh's +Y (**Observed** on `katana05_bare`, `hacker05_bare`, `hacker04_bare`, `polearm03`:
  grip at −2.4 to −4.6, tip at 10.2 to 12.3 units); traced from 1.5 to the mesh's highest Y.
- **Body**: 11 capsules over the defender's bones in the pose on show (a euphoria body's included):
  pelvis-spine1 1.7, spine1-neck 1.8, neck-head 1.1, upper arms 0.6, forearms 0.5, thighs 0.85,
  calves 0.6 units; each is a euphoria part (for the push).
- **Held off**: for swings within 90 m of the camera, a blade that would go into the body is turned
  at the shoulder (`Bip01 R UpperArm`, its children follow) until it rests on the surface, on the
  side it first touched (so it does not jump through as the swing goes on). Probe, two
  Greenlanders with katanas: the animated blade was inside the body on 156 of 1197 swinging ticks,
  up to 20 cm; held, at most 2 mm.
- **Hit stop and recovery**: a blow that lands freezes the swing (every bone) as soon as its blade
  is on the body (within 6 ticks of the blow) for 0.12 s; the fighter then eases (smoothstep) over
  0.35 s into the stance it swung from (its pose on the swing's first tick) instead of following
  through, holds it, and eases back into the animation over 0.2 s once the swing is over, half way
  to a combo's next blow, or when a new swing starts. The blade is held off the body in every one
  of these poses (the arm's turn applied to the whole arm). Kenshi's swings land late in their clips
  (`Cut left`: blow at 0.81 of the clip, swing over at 0.97), so the follow-through after the blow is
  what this replaces. Probe: no shown blade inside the body in any phase (20 hits).
- **Push**: a landed blow whose blade touched the body in the last 8 ticks pushes the euphoria body
  at the touch point, on the touched part, the way the blade was moving (24 of 29 hits in the probe).
- The overrides are published like a ragdoll's (`IRagdollSource`, after the euphoria bodies). Off
  hand on two-handed weapons, blocks and misses are not handled yet.

Deterministic across runs and thread counts (**Verified** by probe: two Greenlanders fighting
30 s, threads 2 and 4). Not saved: a reaction in progress is lost on load.
Cost (probe): about 0.4 ms per body per 1/60 s step on one thread; bodies step in parallel.

Combat and aggro count a body reacting on its feet as staggered (`IHitReactions.Reacting`), as
they do a stumble clip.

Gaps: carried and dropped bodies are the ragdoll's (`CarryMode`, and a dropped one falls from the
shoulder), not euphoria's; only a body already reacting falls off a drop (a walking character is not watched);
the bodies do not collide with each other or with other characters.
