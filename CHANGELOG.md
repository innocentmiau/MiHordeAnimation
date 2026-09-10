# Changelog

All notable changes to this package are documented here.

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