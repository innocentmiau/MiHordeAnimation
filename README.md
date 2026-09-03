# MiHordeAnimation

Drives [MiVertexAnimation](https://github.com/innocentmiau/MiVertexAnimation) clips from a [MiHordeTraffic](https://github.com/innocentmiau/MiHordeTraffic) crowd, from one manager rather than a script on every entity.

Both packages already avoid per entity per frame CPU, and joining them by hand tends to give it straight back: a `MonoBehaviour` on every body measuring its own distance to a target and calling `Play` is thousands of `Update` dispatches and thousands of native transform reads a frame, which is the cost both packages exist to remove. This does the same job from one place, with the loop that runs over every body in a Burst job.

## What it costs

Per frame, for the whole crowd:

- one `IJobParallelForTransform` reading positions, which is the only place transforms are touched
- one Burst `IJob` scanning those positions against the target and each body's cooldown
- one managed loop over **what the scan handed back**, not over the roster

A crowd standing around a target on cooldown produces an empty list, and the managed half of that frame is zero. Nothing on a registered body is dispatched again for as long as it lives.

## Installing

Add both dependencies first, then this package. In `Packages/manifest.json`:

```json
"com.andreleandrodev.mihordetraffic": "https://github.com/innocentmiau/MiHordeTraffic.git",
"com.andreleandrodev.mivertexanimation": "https://github.com/innocentmiau/MiVertexAnimation.git",
"com.andreleandrodev.mihordeanimation": "https://github.com/innocentmiau/MiHordeAnimation.git"
```

## Setting it up

1. Put `HordeAnimationBody` on the entity prefab. It finds the `VATAnimator` in its children the first time it is enabled, and has no `Update` of its own.
2. Put `HordeAttackAnimator` on a manager object in the scene.
3. Set the clip. Either type the name as it appears in the clip set, or leave the name empty and give the slice index instead.
4. Set `range` and `cooldown`.

Leave `target` empty and the crowd attacks whatever the `HordeFlowFieldDriver` is pointing at, which is the same thing it is walking towards.

## Clips by name or by index

Both work, and the name wins when there is one.

A name survives a rebake that reorders the slices and reads as what it is at the call site, which is why it is the default. An index is what the shader actually addresses, so it is the one to reach for when a clip set has two clips of the same name, or when the baked names are whatever the source files happened to be called.

Either way it is resolved to a slice index **once, when a body registers**, and never again. Resolving a name means walking the clip set comparing strings, and doing that for every body every time it swings is the kind of cost that is invisible at one entity and is the whole system at five thousand.

Resolution happens at registration rather than in `Start`, because a horde spawns for as long as it runs and a body that arrived later would never have been checked. Anything that does not resolve is reported once, naming the object, the field that failed, and the clips actually baked on that set.

## Cooldown

One timestamp per body covers both questions. Before it, the body is either mid clip or resting, and the difference does not matter to anything here. It is claimed inside the scan job, so a body cannot be handed out twice in one frame and the main thread's share is a `PlayOnce` call and nothing else.

Keep the cooldown at or above the clip's own length. A shorter one restarts the clip before it has finished and the return to idle never plays.

## Ordering

`HordeAttackAnimator` runs at `[DefaultExecutionOrder(200)]`, and reads transforms from its `LateUpdate`. The mover joins its own transform job in a `LateUpdate` at -100 and separation at 0, so 200 is the first point in the frame where no other job is writing those transforms.

## What it does not do yet

Locomotion. Idle, walk and run belong to a gait selector reading how fast each body is actually going, which needs MiHordeTraffic to expose the speed and heading arrays its movement job already fills in. The attack half needs none of that, which is why it exists first.

## Licence

MIT. See `LICENSE.md`.
