using System.Collections.Generic;
using MiHordeTraffic.Pathing.FlowField;
using MiHordeTraffic.Separation;
using MiVertexAnimation;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Jobs;

namespace MiHordeAnimation
{
    /*
     * A first pass at the bridge between the horde and the vertex animation package, deliberately doing one thing:
     * bodies that get within range of the target play a named clip and return to another, and then sit out a
     * cooldown. Everything the eventual manager needs is already shaped the way it will need to be, so measuring
     * this says something about the real thing rather than about a throwaway.
     *
     * The whole per frame cost is one transform job, one scan job and a play call for each body that came back due.
     * Nothing on an entity is dispatched, nothing is looked up by object, and no distance is measured on the main
     * thread. At rest, with a crowd standing around a target on cooldown, the scan returns an empty list and the
     * main thread's share of this is zero.
     *
     * Run last on purpose. The mover joins its transform job in a LateUpdate at minus one hundred and separation at
     * zero, so reading positions from a transform job here at two hundred is the first point in the frame where no
     * other job is writing those same transforms.
     *
     * What it does not do yet is locomotion. Idle, walk and run belong to a gait selector reading how fast each
     * body is actually going, which needs the mover to expose its speed and heading arrays. This half needs none of
     * that, so it can be measured first and the accessors added once there is a number to justify them.
     */
    /// <summary>
    /// Plays one clip on every horde body that comes within range of a target, once per cooldown, from one manager.
    /// </summary>
    [DefaultExecutionOrder(200)]
    [DisallowMultipleComponent]
    public class HordeAttackAnimator : MonoBehaviour
    {

        private const int MINIMUM_CAPACITY = 64;

        private static readonly ProfilerMarker SCAN_MARKER = new ProfilerMarker("MiHordeAnimation.Attack.Scan");
        private static readonly ProfilerMarker PLAY_MARKER = new ProfilerMarker("MiHordeAnimation.Attack.Play");

        private static readonly List<HordeAnimationBody> PENDING_REGISTER = new List<HordeAnimationBody>();
        private static readonly List<HordeAnimationBody> PENDING_UNREGISTER = new List<HordeAnimationBody>();

        private static HordeAttackAnimator _instance;

        /// <summary>
        /// The manager in the loaded scene, or null when there is none.
        /// </summary>
        public static HordeAttackAnimator Instance => _instance;

        [Header("Target")]
        [Tooltip("What the crowd attacks. Left empty it follows whatever the flow field driver is pointing at.")]
        [SerializeField] private Transform target;

        [Tooltip("Metres from the target at which a body plays the clip, measured on the ground plane.")]
        [SerializeField, Min(0f)] private float range = 2f;

        /*
         * Named or numbered, and the name wins when there is one. A name survives a rebake that reorders the
         * slices and reads as what it is at the call site, which is why it is the default. A number is what the
         * shader actually addresses, so it is the one to reach for when a clip set has two clips of the same name
         * or when the names in it are whatever the source files happened to be called.
         *
         * Both are resolved to a slice index once, when a body registers, rather than on every attack. Resolving a
         * name is a walk of the clip set comparing strings, and doing that for every body every time it swings is
         * the kind of cost that hides: it is invisible at one entity and is the whole system at five thousand.
         */
        [Header("Clip")]
        [Tooltip("Name of the clip to play once, as it is named in the animator's clip set. Leave empty to use the index below instead.")]
        [SerializeField] private string clipName = "Attack";

        [Tooltip("Slice index of the clip to play once. Only used when the name above is empty.")]
        [SerializeField] private int clipIndex = -1;

        [Tooltip("Name of the clip to fall back to when it finishes. Leave both this and the index below empty to hold the last frame.")]
        [SerializeField] private string returnClipName = "Idle";

        [Tooltip("Slice index of the clip to fall back to. Only used when the name above is empty. Minus one holds the last frame.")]
        [SerializeField] private int returnClipIndex = -1;

        [Tooltip("Seconds before a body may play it again. Keep this at or above the clip's own length, or the next one starts before the last has finished.")]
        [SerializeField, Min(0f)] private float cooldown = 1.5f;

        private readonly List<HordeAnimationBody> _bodies = new List<HordeAnimationBody>();

        private TransformAccessArray _transforms;
        private NativeArray<float3> _positions;
        private NativeArray<float> _nextAllowed;
        private NativeList<int> _due;

        /*
         * Managed rather than native because nothing but the main thread reads them, and because minus one has to
         * mean two different things: a clip that could not be resolved and will never play, and a return clip that
         * was deliberately left out so the animator holds its last frame.
         */
        private int[] _clip = new int[0];
        private int[] _returnClip = new int[0];

        private int _capacity;
        private bool _warnedAboutClip;

        /// <summary>
        /// How many bodies this manager is watching.
        /// </summary>
        public int BodyCount => _bodies.Count;

        /// <summary>
        /// How many bodies were started on the clip last frame, which is what to watch when tuning the cooldown.
        /// </summary>
        public int PlayedLastFrame { get; private set; }

        /// <summary>
        /// Queues a body to be animated. Safe from OnEnable.
        /// </summary>
        /// <param name="body">The body to watch.</param>
        public static void Register(HordeAnimationBody body)
        {
            if (PENDING_UNREGISTER.Remove(body)) return;

            PENDING_REGISTER.Add(body);
        }

        /// <summary>
        /// Queues a body to stop being animated. Safe from OnDisable.
        /// </summary>
        /// <param name="body">The body to drop.</param>
        public static void Unregister(HordeAnimationBody body)
        {
            if (PENDING_REGISTER.Remove(body)) return;

            PENDING_UNREGISTER.Add(body);
        }

        /// <summary>
        /// Points the crowd at something else from now on.
        /// </summary>
        /// <param name="value">The transform to measure range against, or null to follow the flow field driver.</param>
        public void SetTarget(Transform value) => target = value;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            _instance = null;
            PENDING_REGISTER.Clear();
            PENDING_UNREGISTER.Clear();
        }

        private void Awake()
        {
            if (_instance && _instance != this)
            {
                Destroy(this);
                return;
            }

            _instance = this;
            _transforms = new TransformAccessArray(MINIMUM_CAPACITY);
            _due = new NativeList<int>(MINIMUM_CAPACITY, Allocator.Persistent);

            EnsureCapacity(MINIMUM_CAPACITY);
        }

        private void OnDestroy()
        {
            if (_transforms.isCreated) _transforms.Dispose();
            if (_positions.IsCreated) _positions.Dispose();
            if (_nextAllowed.IsCreated) _nextAllowed.Dispose();
            if (_due.IsCreated) _due.Dispose();

            _bodies.Clear();

            if (_instance == this) _instance = null;
        }

        private void LateUpdate()
        {
            DrainRoster();

            Transform destination = ResolveTarget();

            PlayedLastFrame = 0;

            if (!destination || _bodies.Count == 0) return;

            _due.Clear();

            /*
             * Joined on the spot rather than left in flight, because there is nothing left in the frame to overlap
             * it with at this execution order. Both halves together are a transform read and a compare per body,
             * so the wait is short, and leaving it until the next frame would put the whole crowd's attacks a
             * frame behind the positions they were decided from for no gain worth having.
             */
            using (SCAN_MARKER.Auto())
            {
                JobHandle sample = new TransformSampleJob { Positions = _positions }.Schedule(_transforms);

                new HordeAttackScanJob
                {
                    Positions = _positions,
                    NextAllowed = _nextAllowed,
                    Due = _due,
                    Target = destination.position,
                    RangeSquared = range * range,
                    Now = Time.time,
                    Cooldown = cooldown,
                    Count = _bodies.Count
                }
                .Schedule(sample)
                .Complete();
            }

            using (PLAY_MARKER.Auto())
                PlayDue();

            PlayedLastFrame = _due.Length;
        }

        private Transform ResolveTarget()
        {
            if (target) return target;

            return HordeFlowFieldDriver.Instance ? HordeFlowFieldDriver.Instance.Target : null;
        }

        /*
         * The only loop here that touches managed objects, and it runs over what the scan handed back rather than
         * over the roster. A body already mid clip, resting, or out of range never reaches it.
         */
        private void PlayDue()
        {
            for (int i = 0; i < _due.Length; i++)
            {
                int slot = _due[i];
                int clip = _clip[slot];

                /*
                 * Skipped silently, because whatever is wrong with this body was said once when it registered.
                 * Saying it again on every cooldown for every body in range is thousands of formatted strings a
                 * second, which buries the one line that explains it.
                 */
                if (clip < 0) continue;

                VATAnimator animator = _bodies[slot].Animator;

                if (!animator) continue;

                animator.PlayOnce(clip, _returnClip[slot]);
            }
        }

        /// <summary>
        /// Resolves the clip names or indices against every registered body again, after changing them at runtime.
        /// </summary>
        public void RefreshClips()
        {
            _warnedAboutClip = false;

            for (int i = 0; i < _bodies.Count; i++)
                ResolveClips(_bodies[i], i);
        }

        /*
         * Done once per body as it registers rather than once in Start, because a horde spawns for as long as it
         * runs and a body that arrived after Start would never have been looked at. It is also the earliest moment
         * the answer can be worked out at all: the clip set belongs to the animator, so there is nothing to check
         * against until there is a body carrying one.
         */
        /// <summary>
        /// Works out which slice this body's animator should play, and says so when the answer is nothing.
        /// </summary>
        private void ResolveClips(HordeAnimationBody body, int slot)
        {
            _clip[slot] = -1;
            _returnClip[slot] = -1;

            VATAnimator animator = body ? body.Animator : null;

            if (!animator)
            {
                WarnAboutClip(body, "carries no VATAnimator, so there is nothing to play a clip on");
                return;
            }

            VATClipSet set = animator.ClipSet;

            if (!set)
            {
                WarnAboutClip(animator, "has no clip set assigned, so a clip can be neither named nor numbered on it");
                return;
            }

            int clip = ResolveOne(set, clipName, clipIndex);

            if (clip < 0)
            {
                WarnAboutClip(animator, $"has no clip matching name '{clipName}' or index {clipIndex}");
                return;
            }

            /*
             * Nothing asked for is a valid answer and means hold the last frame, which is what PlayOnce does with
             * minus one. Only a return clip that was asked for and not found is worth saying anything about.
             */
            bool wantsReturn = !string.IsNullOrEmpty(returnClipName) || returnClipIndex >= 0;
            int returnTo = -1;

            if (wantsReturn)
            {
                returnTo = ResolveOne(set, returnClipName, returnClipIndex);

                if (returnTo < 0)
                {
                    WarnAboutClip(animator, $"has no return clip matching name '{returnClipName}' or index {returnClipIndex}");
                    return;
                }
            }

            _clip[slot] = clip;
            _returnClip[slot] = returnTo;
        }

        /// <summary>
        /// The slice a name or an index points at, with the name winning whenever there is one.
        /// </summary>
        private static int ResolveOne(VATClipSet set, string name, int index)
        {
            if (!string.IsNullOrEmpty(name)) return set.IndexOf(name);

            return index >= 0 && index < set.Count ? index : -1;
        }

        /*
         * Once for the whole crowd. Whatever is wrong is wrong on the prefab, so it is wrong for every body that
         * spawns from it, and a line per body is thousands of them for one mistake.
         */
        private void WarnAboutClip(Object context, string problem)
        {
            if (_warnedAboutClip) return;

            _warnedAboutClip = true;

            VATAnimator animator = context as VATAnimator;
            VATClipSet set = animator ? animator.ClipSet : null;
            string available = set && set.Count > 0 ? string.Join(", ", set.Names()) : "none";

            Debug.LogWarning(
                $"[MiHordeAnimation] {(context ? context.name : "A horde body")} {problem}, so it will not play anything. " +
                $"Clips baked on it: {available}.",
                context);
        }

        /// <summary>
        /// Applies everything that registered or unregistered since the last frame. Only ever called with no job in flight.
        /// </summary>
        private void DrainRoster()
        {
            for (int i = 0; i < PENDING_REGISTER.Count; i++)
                Add(PENDING_REGISTER[i]);

            PENDING_REGISTER.Clear();

            for (int i = 0; i < PENDING_UNREGISTER.Count; i++)
                Remove(PENDING_UNREGISTER[i]);

            PENDING_UNREGISTER.Clear();
        }

        private void Add(HordeAnimationBody body)
        {
            if (!body || body.AnimationIndex >= 0) return;

            EnsureCapacity(_bodies.Count + 1);

            int slot = _bodies.Count;

            /*
             * Cleared so a pooled body coming back does not inherit the cooldown of whoever held the slot before
             * it, which would have a freshly spawned entity stand in range doing nothing for a second and a half.
             */
            _nextAllowed[slot] = 0f;

            body.AnimationIndex = slot;
            _bodies.Add(body);
            _transforms.Add(body.transform);

            ResolveClips(body, slot);
        }

        private void Remove(HordeAnimationBody body)
        {
            int index = body.AnimationIndex;
            if (index < 0 || index >= _bodies.Count || _bodies[index] != body) return;

            int last = _bodies.Count - 1;

            /*
             * The cooldown moves with the body swapped into this slot. It is the only state here that survives
             * between frames, so leaving it behind would hand that body a stranger's timer and let it attack again
             * immediately, or stop it attacking for as long as the departed body had left.
             */
            _bodies[index] = _bodies[last];
            _bodies[index].AnimationIndex = index;
            _nextAllowed[index] = _nextAllowed[last];
            _clip[index] = _clip[last];
            _returnClip[index] = _returnClip[last];
            _bodies.RemoveAt(last);
            _transforms.RemoveAtSwapBack(index);

            body.AnimationIndex = -1;
        }

        private void EnsureCapacity(int count)
        {
            if (count <= _capacity) return;

            int capacity = math.max(_capacity * 2, math.max(count, MINIMUM_CAPACITY));

            /*
             * Positions are rewritten in full by the sample job before anything reads them, so they are replaced.
             * The cooldowns are carried, because dropping them would let the whole crowd attack again on the frame
             * the roster happened to grow.
             */
            if (_positions.IsCreated) _positions.Dispose();

            _positions = new NativeArray<float3>(capacity, Allocator.Persistent);

            NativeArray<float> grown = new NativeArray<float>(capacity, Allocator.Persistent);

            if (_nextAllowed.IsCreated)
            {
                NativeArray<float>.Copy(_nextAllowed, grown, _capacity);
                _nextAllowed.Dispose();
            }

            _nextAllowed = grown;

            System.Array.Resize(ref _clip, capacity);
            System.Array.Resize(ref _returnClip, capacity);

            _capacity = capacity;
        }

    }
}
