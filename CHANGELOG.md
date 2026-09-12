# Changelog

All notable changes to this package are documented here.

## [0.3.0]

### Requires

MiHordeTraffic **0.7.1** and MiVertexAnimation **1.5.1**, which adds the `IsPlayingOnce` this needs to avoid cutting off an attack.

### Added

- **`HordeAnimations`**, the locomotion half the attack manager's own notes said was missing. One manager gives a whole crowd an entrance, a resting clip, a walk, a run and a death, chosen from how fast each body is actually moving.

  **Speed is measured from how far each transform moved**, not asked of whatever is moving it, so the same manager drives flow field bodies, NavMeshAgent bodies and anything a game moves itself. It is smoothed, because a single frame's delta flickers a body between walk and run several times a second at a steady pace.

  **Only bodies whose clip should change reach the main thread.** A crowd standing still, or one all running together, hands back an empty list, so a settled crowd costs the scan and nothing else.

  **The entrance plays itself.** A body plays its spawn clip whenever it is enabled and returns to idle at the end, so a pooled entity gets one every time it comes back without anything having to ask.

  **`PlayDeath`** holds the last frame and never leaves it, so a corpse can lie there until whatever owns it puts it away.

  **Nothing part way through a one-shot is interrupted**, the attack clip included, so both managers can drive the same body without cutting each other off.

- **Two throttles, capping different things.** Neither is perceptible: the shader runs a clip from a start time and a clock, so a throttle delays the decision to change clip and never the playback itself.

  **Checks Per Second**, 20 by default and 0 meaning every frame, bounds how often the crowd is looked at. It is the one that skips the transform read, which is the part that scales with body count. Speed is measured between checks rather than between frames, since a check covering three frames that divided by one would read as three times the speed and put a walking crowd into a sprint.

  **Clip Changes Per Check**, 256 by default and 0 meaning unlimited, bounds what any one check may start. Starting a clip writes a material property block, and a crowd that all begins walking on the same frame would otherwise do thousands at once. Bodies over the budget keep their old clip and are served on later checks from a rotating offset, so every body is reached within queue length over budget checks rather than the tail of a large crowd never updating at all. Below the budget it never engages, so a small crowd is exactly as it was.

  Nothing triggered explicitly goes through either. `PlayDeath` and the attack manager play on the spot.

- **Match Speed To Movement**, off by default. Scales clip playback by how fast a body is actually travelling, which is what stops a walk authored for one and a half metres a second from sliding when the body does three. Playback is snapped to steps, so a body holding a steady pace writes once and then never again, and the step is the dial between how closely the feet track and how often that write happens. Needs the authored speed of the walk and run clips, which is why it is off until someone says what they are.

- **Clip variety.** Idle, walk, run and death are lists rather than single indices. A body picks one variant on the way in and keeps it across all four, so its idle and walk belong to each other rather than being drawn separately, and the choice costs nothing per frame. One entry behaves exactly as a single clip did.

- **`HordeGait`**, the state a body is in, and **`HordeGaitScanJob`**, the Burst scan that picks it.

- **`HordeAnimationBody.GaitIndex`**, a second roster slot. The two managers are filled and emptied independently, so a scene can have either, both or neither.

## [0.2.0]

### Requires

MiHordeTraffic **0.6.0** and MiVertexAnimation **1.5.0**. The manifest had been pinning 0.2.0 and 1.4.1 since the first release, which were already behind.

### Fixed

- **Attack range was measured from the target's centre**, so a crowd pressed against the wall of a large target stood half its width out of range and never swung. It is measured from the nearest part of the target now, taking the shape from `HordeTarget` where there is one, and from the driver directly when this is chasing the same target the crowd is. A target with no shape is a point, exactly as before.

## [0.1.0]

First release.

### Added

- `HordeAnimationBody`, a registration component with no `Update` of its own. Finds the `VATAnimator` in its children on first enable and holds the slot the manager hands it.
- `HordeAttackAnimator`, which plays one clip on every body that comes within range of a target, once per cooldown, from a single manager. Reads transforms in one job, scans them in Burst, and touches only the bodies the scan hands back.
- `HordeAttackScanJob`, the Burst scan. Skips anything still on cooldown, tests range on the ground plane, and claims the cooldown as it collects a body, so nothing can be handed out twice in one frame.
- Clips addressed by name or by slice index, resolved once when a body registers rather than on every attack, with a warning naming the object, the field that failed and the clips actually baked on the set.
- The target defaults to whatever the `HordeFlowFieldDriver` is pointing at, so the crowd attacks what it is walking towards without wiring it twice.