using System.Collections.Generic;
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
     * The locomotion half of the bridge, and the one the attack manager's own notes said was missing. Every body
     * gets an entrance, a resting clip, a walk, a run and a death, chosen from how fast it is actually moving, and
     * chosen for the whole crowd in one job rather than by a script on each entity.
     *
     * Only the bodies where something changed reach the main thread. A crowd standing around a target, or one all
     * running the same way, hands back an empty list, so a settled crowd costs the check and nothing else. That is
     * the same shape as the attack manager and for the same reason: what makes a horde affordable is not doing the
     * work quickly, it is not dispatching it per entity at all.
     *
     * Speed is read from how far each transform moved rather than from whatever is moving it, which is what lets
     * this drive flow field bodies, NavMeshAgent bodies and anything a game moves itself without knowing which.
     *
     * Two throttles, and they cap different things. Checks Per Second bounds how often the crowd is looked at, and
     * it is the one that skips the transform read, which is the part that scales. Clip Changes Per Check bounds
     * what any one check may start, because starting a clip writes a material property block and a crowd that all
     * begins walking on the same frame would otherwise do thousands of those at once. Neither is perceptible on an
     * animation: the shader runs the clip from a start time and a clock, so a throttle only delays the decision to
     * change, never the playback.
     *
     * An entrance and a death are held rather than chosen. Both are one-shots, and the difference between them is
     * only that one has an end: the entrance returns itself to the resting clip and hands control back, and the
     * death holds its last frame and never does. Anything already part way through a one-shot is left alone, the
     * attack clip included, so the two managers can drive the same body without cutting each other off.
     *
     * Ordered after the attack manager so that when both want the same body on the same frame, the attack has
     * already started and this sees it as busy rather than racing it.
     */
    /// <summary>
    /// Plays an entrance, idle, walk, run and death on a whole horde from one manager, chosen by how fast each body moves.
    /// </summary>
    [DefaultExecutionOrder(201)]
    [DisallowMultipleComponent]
    public class HordeAnimations : MonoBehaviour
    {

        private const int MINIMUM_CAPACITY = 64;

        /*
         * A death is held forever, and saying so as a time rather than as another flag keeps the hold in one
         * place. Nothing ever compares past it because the dead state is checked first.
         */
        private const float HELD_FOREVER = float.MaxValue;

        private static readonly ProfilerMarker SCAN_MARKER = new ProfilerMarker("MiHordeAnimation.Gait.Scan");
        private static readonly ProfilerMarker PLAY_MARKER = new ProfilerMarker("MiHordeAnimation.Gait.Play");

        private static readonly List<HordeAnimationBody> PENDING_REGISTER = new List<HordeAnimationBody>();
        private static readonly List<HordeAnimationBody> PENDING_UNREGISTER = new List<HordeAnimationBody>();

        private static HordeAnimations _instance;

        /// <summary>
        /// The manager in the loaded scene, or null when there is none.
        /// </summary>
        public static HordeAnimations Instance => _instance;

        [Header("Clips")]
        [Tooltip("Slice index of the entrance, played once when a body is enabled before it returns to Idle. Minus one for none.")]
        [SerializeField] private int spawnClip = -1;

        /*
         * Lists rather than single indices so a crowd is not a field of clones. A body picks one of each on the
         * way in and keeps it, so the choice costs nothing per frame and a body's idle and walk belong together
         * rather than being drawn separately every time it changes gait.
         *
         * One entry behaves exactly as a single clip did, which is what makes this free to ignore.
         */
        [Tooltip("Resting clips, looped. More than one and each body keeps its own, so a crowd does not idle in unison.")]
        [SerializeField] private int[] idleClips = { 0 };

        [Tooltip("Walk clips, looped.")]
        [SerializeField] private int[] walkClips = { 1 };

        [Tooltip("Run clips, looped. Point these at the walk if the character has only one.")]
        [SerializeField] private int[] runClips = { 2 };

        [Tooltip("Death clips, played once and held on the last frame. Leave empty for none.")]
        [SerializeField] private int[] deathClips = { };

        [Header("Gait")]
        /*
         * A speed rather than nothing, because a body being shoved about by the crowd it is standing in never
         * reads as exactly zero. At a tenth of a metre a second a settled crowd stands still instead of shuffling
         * in and out of its walk.
         */
        [Tooltip("Metres per second above which a body walks. Small rather than zero, so a body jostled by the crowd still counts as standing.")]
        [SerializeField, Min(0f)] private float walkAbove = .1f;

        [Tooltip("Metres per second above which a body runs instead of walking.")]
        [SerializeField, Min(0f)] private float runAbove = 3f;

        [Tooltip("How quickly measured speed follows what a body is actually doing. Lower is steadier and slower to change clip.")]
        [SerializeField, Min(.1f)] private float speedSmoothing = 8f;

        [Header("Throttling")]
        [Tooltip("How often the crowd is looked at. Zero is every frame. This is the one that skips the transform read, so it is the setting that saves the most.")]
        [SerializeField, Min(0f)] private float checksPerSecond = 20f;

        [Tooltip("Most clip changes one check may start. Zero is unlimited. Bodies over the budget keep their old clip and are served on the next checks, oldest queue position first.")]
        [SerializeField, Min(0)] private int clipChangesPerCheck = 256;

        [Header("Match Speed To Movement")]
        /*
         * Off by default because it needs two numbers about the clips that only whoever authored them knows, and
         * being wrong about them looks worse than not doing it at all.
         */
        [Tooltip("Scale clip playback by how fast a body is actually travelling, so feet do not slide. Needs the authored speeds below to be right.")]
        [SerializeField] private bool matchSpeedToMovement = false;

        [Tooltip("Metres per second the walk clip was authored at, meaning the speed at which it looks correct unscaled.")]
        [SerializeField, Min(0f)] private float walkAuthoredSpeed = 1.5f;

        [Tooltip("Metres per second the run clip was authored at.")]
        [SerializeField, Min(0f)] private float runAuthoredSpeed = 4f;

        [Tooltip("Playback is snapped to steps of this, so a body holding a steady pace writes once instead of every check. Smaller tracks the feet more closely and costs more.")]
        [SerializeField, Min(.01f)] private float clipSpeedStep = .05f;

        [Tooltip("Slowest the clip may be played, so a body creeping does not stop dead.")]
        [SerializeField, Min(.01f)] private float minimumClipSpeed = .5f;

        [Tooltip("Fastest the clip may be played, so a body flung by the crowd does not flicker.")]
        [SerializeField, Min(.01f)] private float maximumClipSpeed = 2f;

        private readonly List<HordeAnimationBody> _bodies = new List<HordeAnimationBody>();

        private TransformAccessArray _transforms;
        private NativeArray<float3> _positions;
        private NativeArray<float3> _previous;
        private NativeArray<float> _speed;
        private NativeArray<float> _holdUntil;
        private NativeArray<byte> _gait;
        private NativeArray<byte> _wanted;
        private NativeArray<byte> _variant;
        private NativeArray<float> _wantedClipSpeed;
        private NativeArray<float> _appliedClipSpeed;
        private NativeList<int> _due;

        private int _capacity;
        private int _cursor;
        private float _nextCheck;
        private float _lastCheck;

        /// <summary>
        /// How many bodies this manager is animating.
        /// </summary>
        public int BodyCount => _bodies.Count;

        /// <summary>
        /// How many bodies changed clip or clip speed on the last check.
        /// </summary>
        public int ChangedLastCheck { get; private set; }

        /// <summary>
        /// How many wanted to change on the last check, which is above ChangedLastCheck whenever the budget bit.
        /// </summary>
        public int WantedLastCheck { get; private set; }

        /// <summary>
        /// Queues a body to be animated. Safe from OnEnable.
        /// </summary>
        /// <param name="body">The body to animate.</param>
        public static void Register(HordeAnimationBody body)
        {
            /*
             * Dropped when there is no manager, rather than queued against one arriving. A scene can perfectly
             * well have the attack manager and not this one, and a queue nothing ever drains grows to hold every
             * enabled body in the game: not a leak, since disabling removes them again, but it turns both of these
             * into a linear scan of the whole crowd on every spawn and every despawn.
             *
             * Nothing is missed by it. A body enabled before this manager wakes is picked up by the sweep in Awake,
             * which is the only ordering this could have lost to.
             */
            if (!_instance) return;

            if (PENDING_UNREGISTER.Remove(body)) return;

            PENDING_REGISTER.Add(body);
        }

        /// <summary>
        /// Queues a body to stop being animated. Safe from OnDisable.
        /// </summary>
        /// <param name="body">The body to drop.</param>
        public static void Unregister(HordeAnimationBody body)
        {
            if (!_instance) return;

            if (PENDING_REGISTER.Remove(body)) return;

            PENDING_UNREGISTER.Add(body);
        }

        /*
         * Called by the game rather than worked out here, because nothing about a transform says that the thing
         * it belongs to has died. The body keeps its slot and its last frame until whatever owns it puts it away,
         * which is what lets a corpse lie there for a while before it is pooled.
         *
         * Played on the spot rather than queued, so nothing a game triggers explicitly is ever delayed by either
         * throttle. Only the gait goes through the check.
         */
        /// <summary>
        /// Plays the death clip on a body and holds its last frame, leaving it there until it is disabled.
        /// </summary>
        /// <param name="body">The body that died.</param>
        /// <returns>True when the body is registered and a death clip is configured.</returns>
        public bool PlayDeath(HordeAnimationBody body)
        {
            if (!body) return false;

            int index = body.GaitIndex;

            if (index < 0 || index >= _bodies.Count) return false;

            int clip = Pick(deathClips, _variant[index]);
            VATAnimator animator = body.Animator;

            if (clip < 0 || !animator) return false;

            _gait[index] = (byte)HordeGait.DEAD;
            _holdUntil[index] = HELD_FOREVER;

            animator.PlayOnce(clip, -1);
            return true;
        }

        /*
         * The other half of not interrupting a one-shot. Refusing to cut an attack off keeps this manager from
         * breaking the attack, and does nothing about the attack breaking this manager: a one-shot ends by
         * crossfading into whatever return clip it was given, which is a clip this manager did not choose and does
         * not know about, while its cached gait still names the one from before.
         *
         * That goes wrong quietly. A body that attacks while walking comes out of it on the attack's idle, the
         * scan still wants a walk, the cache still says walk, so nothing is queued and the body walks on its idle
         * clip for as long as its gait happens not to change. With clip variety on it is worse, since the return
         * clip is one fixed index and every body picked its own.
         *
         * Forgetting costs one byte written once, at the moment something else plays. The next check finds a value
         * no computed gait can equal, queues the body, waits out the one-shot and puts back the right clip.
         */
        /// <summary>
        /// Tells this manager it no longer knows what a body is playing, so it re-asserts the clip on the next check.
        /// Call it whenever something plays a clip on a body directly.
        /// </summary>
        /// <param name="body">The body whose clip was taken over.</param>
        public void ForgetClip(HordeAnimationBody body)
        {
            if (!body) return;

            int index = body.GaitIndex;

            if (index < 0 || index >= _bodies.Count) return;

            /*
             * A death is the one thing that is never re-asserted. It is meant to hold its last frame for good, and
             * putting a body back on its idle because something played over it would be the corpse standing up.
             */
            if (_gait[index] == (byte)HordeGait.DEAD) return;

            _gait[index] = (byte)HordeGait.UNKNOWN;
        }

        /// <summary>
        /// How fast this manager measured a body to be travelling, in metres per second on the ground plane.
        /// </summary>
        /// <param name="body">The body to ask about.</param>
        /// <returns>Its smoothed speed, or zero when it is not registered.</returns>
        public float SpeedOf(HordeAnimationBody body)
        {
            if (!body) return 0f;

            int index = body.GaitIndex;

            return index >= 0 && index < _bodies.Count ? _speed[index] : 0f;
        }

        /// <summary>
        /// What a body is currently playing.
        /// </summary>
        /// <param name="body">The body to ask about.</param>
        /// <returns>Its gait, or IDLE when it is not registered.</returns>
        public HordeGait GaitOf(HordeAnimationBody body)
        {
            if (!body) return HordeGait.IDLE;

            int index = body.GaitIndex;

            return index >= 0 && index < _bodies.Count ? (HordeGait)_gait[index] : HordeGait.IDLE;
        }

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

            AdoptExisting();
        }

        /*
         * Anything already enabled when this wakes, which is every body in a scene that was saved with its crowd
         * in it. Those ran their OnEnable before this existed and were turned away, so this is the other half of
         * refusing to queue against a manager that may never arrive.
         *
         * Once, at Awake, and never again: after this, registration is live and every body announces itself.
         */
        private void AdoptExisting()
        {
            HordeAnimationBody[] existing = FindObjectsByType<HordeAnimationBody>(FindObjectsSortMode.None);

            for (int i = 0; i < existing.Length; i++)
                if (existing[i].isActiveAndEnabled) PENDING_REGISTER.Add(existing[i]);
        }

        private void OnDestroy()
        {
            if (_instance == this) _instance = null;

            if (_transforms.isCreated) _transforms.Dispose();
            if (_due.IsCreated) _due.Dispose();
            if (_positions.IsCreated) _positions.Dispose();
            if (_previous.IsCreated) _previous.Dispose();
            if (_speed.IsCreated) _speed.Dispose();
            if (_holdUntil.IsCreated) _holdUntil.Dispose();
            if (_gait.IsCreated) _gait.Dispose();
            if (_wanted.IsCreated) _wanted.Dispose();
            if (_variant.IsCreated) _variant.Dispose();
            if (_wantedClipSpeed.IsCreated) _wantedClipSpeed.Dispose();
            if (_appliedClipSpeed.IsCreated) _appliedClipSpeed.Dispose();
        }

        private void LateUpdate()
        {
            /*
             * Every frame regardless of the check rate, because registering is what starts a body's entrance and
             * delaying that would have a pooled body visibly stand on its idle before snapping into its spawn.
             */
            DrainRoster();

            if (_bodies.Count == 0) return;

            float now = Time.time;

            if (checksPerSecond > 0f && now < _nextCheck) return;

            /*
             * Measured between checks rather than between frames. A check covering three frames that divided by
             * one would read as three times the speed and put a walking crowd into a sprint.
             */
            float elapsed = _lastCheck > 0f ? now - _lastCheck : Time.deltaTime;

            _lastCheck = now;
            _nextCheck = checksPerSecond > 0f ? now + 1f / checksPerSecond : 0f;

            _due.Clear();

            using (SCAN_MARKER.Auto())
            {
                JobHandle sample = new TransformSampleJob { Positions = _positions }.Schedule(_transforms);

                new HordeGaitScanJob
                {
                    Positions = _positions,
                    HoldUntil = _holdUntil,
                    AppliedClipSpeed = _appliedClipSpeed,
                    Previous = _previous,
                    Speed = _speed,
                    Gait = _gait,
                    Wanted = _wanted,
                    WantedClipSpeed = _wantedClipSpeed,
                    Due = _due,
                    Now = now,
                    Elapsed = elapsed,
                    WalkAbove = walkAbove,
                    RunAbove = math.max(runAbove, walkAbove),
                    Smoothing = speedSmoothing,
                    MatchSpeed = (byte)(matchSpeedToMovement ? 1 : 0),
                    WalkAuthoredSpeed = walkAuthoredSpeed,
                    RunAuthoredSpeed = runAuthoredSpeed,
                    ClipSpeedStep = clipSpeedStep,
                    MinimumClipSpeed = minimumClipSpeed,
                    MaximumClipSpeed = math.max(maximumClipSpeed, minimumClipSpeed),
                    Count = _bodies.Count
                }
                .Schedule(sample)
                .Complete();
            }

            using (PLAY_MARKER.Auto())
                PlayDue();
        }

        /*
         * The only loop here that touches managed objects, and it runs over what the scan handed back rather than
         * over the roster. A body already on the right clip at the right speed never reaches it.
         *
         * Started from a rotating offset rather than from the beginning. The queue is built in roster order, so
         * taking the first few every time would serve the same low numbered bodies for ever and leave the tail of
         * a large crowd never updating at all. Advancing by whatever was served means every body is reached
         * within queue length over budget checks, which is the guarantee any priority order would have to beat.
         */
        private void PlayDue()
        {
            WantedLastCheck = _due.Length;
            ChangedLastCheck = 0;

            if (_due.Length == 0) return;

            int budget = clipChangesPerCheck > 0 ? math.min(clipChangesPerCheck, _due.Length) : _due.Length;

            for (int i = 0; i < budget; i++)
            {
                int index = _due[(_cursor + i) % _due.Length];
                HordeAnimationBody body = _bodies[index];
                VATAnimator animator = body ? body.Animator : null;

                if (!animator) continue;

                /*
                 * Part way through something that should not be cut off, an attack most of the time. Nothing is
                 * committed, so this body comes back through the scan and gets its clip the moment the one-shot
                 * is done with it.
                 */
                if (animator.IsPlayingOnce) continue;

                Apply(animator, index);

                ChangedLastCheck++;
            }

            _cursor = (_cursor + budget) % _due.Length;
        }

        /*
         * Speed before clip, so that a body doing both in one check pays for the clip change and gets the new
         * speed folded into the same material write rather than triggering a second one.
         */
        private void Apply(VATAnimator animator, int index)
        {
            if (matchSpeedToMovement)
            {
                animator.Speed = _wantedClipSpeed[index];
                _appliedClipSpeed[index] = _wantedClipSpeed[index];
            }

            byte wanted = _wanted[index];

            if (_gait[index] == wanted) return;

            int clip = ClipFor((HordeGait)wanted, _variant[index]);

            if (clip < 0) return;

            animator.Play(clip);
            _gait[index] = wanted;
        }

        private int ClipFor(HordeGait gait, byte variant) => gait switch
        {
            HordeGait.RUNNING => Pick(runClips, variant),
            HordeGait.WALKING => Pick(walkClips, variant),
            _ => Pick(idleClips, variant)
        };

        /*
         * The same variant number across every list, so a body's idle, walk and run belong to one another rather
         * than being drawn separately. Wrapped per list, so the lists do not have to be the same length.
         */
        private static int Pick(int[] clips, byte variant) =>
            clips == null || clips.Length == 0 ? -1 : clips[variant % clips.Length];

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
            if (!body || body.GaitIndex >= 0) return;

            EnsureCapacity(_bodies.Count + 1);

            int slot = _bodies.Count;

            /*
             * Seeded from where the body actually is. A pooled body coming back into a slot somebody else left
             * would otherwise measure the distance between the two of them as one step of travel, which at any
             * spawn distance at all reads as several hundred metres a second, and it would come up running.
             */
            _previous[slot] = body.transform.position;
            _speed[slot] = 0f;
            _holdUntil[slot] = 0f;
            _gait[slot] = (byte)HordeGait.IDLE;
            _wanted[slot] = (byte)HordeGait.IDLE;
            _wantedClipSpeed[slot] = 1f;
            _appliedClipSpeed[slot] = 1f;
            _variant[slot] = (byte)UnityEngine.Random.Range(0, byte.MaxValue);

            body.GaitIndex = slot;
            _bodies.Add(body);
            _transforms.Add(body.transform);

            BeginSpawn(body, slot);
        }

        /*
         * Started here rather than asked for, so a pooled body plays its entrance every time it is enabled without
         * anything having to remember to call for one. That is the same moment the body joins the crowd, which is
         * the only moment a spawn can mean anything.
         */
        private void BeginSpawn(HordeAnimationBody body, int slot)
        {
            VATAnimator animator = body.Animator;

            if (!animator) return;

            /*
             * Reset first, because a pooled body comes back holding whatever playback speed it died at, and an
             * entrance played at half speed is not the one that was authored. The setter does nothing when it is
             * already one, so this is free for anything that was not scaled.
             */
            animator.Speed = 1f;

            int idle = Pick(idleClips, _variant[slot]);

            if (spawnClip < 0)
            {
                if (idle >= 0) animator.Play(idle);
                return;
            }

            _gait[slot] = (byte)HordeGait.SPAWNING;
            _holdUntil[slot] = Time.time + EntranceSeconds(animator);

            animator.PlayOnce(spawnClip, idle);
        }

        /*
         * How long the entrance will take at the speed it is going to run at, so the hold ends when the clip does.
         * A clip set that does not know its own lengths answers zero, which holds for no time at all and leaves
         * the gait free immediately: the animator still returns itself to idle, so the worst case is a body that
         * can change clip during its entrance rather than one stuck in it.
         */
        private float EntranceSeconds(VATAnimator animator)
        {
            if (!animator.ClipSet) return 0f;

            float length = animator.ClipSet.LengthAt(spawnClip);
            float speed = animator.CurrentSpeed * animator.GetClipSpeed(spawnClip);

            return speed > 0f ? length / speed : length;
        }

        private void Remove(HordeAnimationBody body)
        {
            int index = body.GaitIndex;

            if (index < 0 || index >= _bodies.Count || _bodies[index] != body) return;

            int last = _bodies.Count - 1;

            /*
             * Every per body row moves with the body swapped into this slot. All of them hold state from earlier
             * checks, and leaving any behind hands that body a stranger's: somebody else's measured speed, or
             * somebody else's death, which is the one that shows.
             */
            _bodies[index] = _bodies[last];
            _bodies[index].GaitIndex = index;
            _previous[index] = _previous[last];
            _speed[index] = _speed[last];
            _holdUntil[index] = _holdUntil[last];
            _gait[index] = _gait[last];
            _wanted[index] = _wanted[last];
            _variant[index] = _variant[last];
            _wantedClipSpeed[index] = _wantedClipSpeed[last];
            _appliedClipSpeed[index] = _appliedClipSpeed[last];
            _bodies.RemoveAt(last);
            _transforms.RemoveAtSwapBack(index);

            body.GaitIndex = -1;
        }

        private void EnsureCapacity(int count)
        {
            if (count <= _capacity) return;

            int capacity = math.max(_capacity * 2, math.max(count, MINIMUM_CAPACITY));

            /*
             * Positions are rewritten in full by the sample job before anything reads them, so they are replaced.
             * Everything else is carried, because dropping it would reset the whole crowd's measured speed and its
             * gait on the check the roster happened to grow, which looks like every body stumbling at once.
             */
            if (_positions.IsCreated) _positions.Dispose();

            _positions = new NativeArray<float3>(capacity, Allocator.Persistent);

            Carry(ref _previous, capacity);
            Carry(ref _speed, capacity);
            Carry(ref _holdUntil, capacity);
            Carry(ref _gait, capacity);
            Carry(ref _wanted, capacity);
            Carry(ref _variant, capacity);
            Carry(ref _wantedClipSpeed, capacity);
            Carry(ref _appliedClipSpeed, capacity);

            _capacity = capacity;
        }

        private void Carry<T>(ref NativeArray<T> array, int capacity) where T : unmanaged
        {
            NativeArray<T> grown = new NativeArray<T>(capacity, Allocator.Persistent);

            if (array.IsCreated)
            {
                NativeArray<T>.Copy(array, grown, array.Length);
                array.Dispose();
            }

            array = grown;
        }

    }
}
