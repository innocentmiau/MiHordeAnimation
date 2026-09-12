using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace MiHordeAnimation
{
    /*
     * Works out how fast every body is actually travelling, which of idle, walk and run that should be, and how
     * fast its clip should run to match, then hands back only the ones where something changed. A crowd at rest,
     * or a crowd all running the same way, returns an empty list and costs the main thread nothing at all.
     *
     * Speed is measured from how far the transform moved rather than asked of whatever is moving it. That keeps
     * this working for a flow field body, a NavMeshAgent body and anything a game moves itself, and it is the
     * honest number in every case: what the animation should match is the distance actually covered, not the speed
     * something intended.
     *
     * The elapsed time is handed in rather than taken from the frame, because this does not run every frame. A
     * check that covers three frames and divides by one would read as three times the speed and put a walking
     * crowd into a sprint.
     *
     * What it decides is written to Wanted and not to Gait. Gait is what is actually playing, and only the main
     * thread knows whether it managed to start the clip: a body part way through an attack is left alone, and so
     * is one past the frame's budget. Leaving Gait untouched is what brings both of them back here on the next
     * check rather than stranding them on the wrong clip.
     */
    /// <summary>
    /// Picks idle, walk or run and a clip speed for every body from how far it moved, and collects what changed.
    /// </summary>
    [BurstCompile]
    public struct HordeGaitScanJob : IJob
    {

        /*
         * Below this a change in clip speed is not worth a material write. The quantisation step already stops the
         * ordinary wobble, and this only catches the float comparison at the edge of a step.
         */
        private const float SPEED_EPSILON = .0001f;

        [ReadOnly] public NativeArray<float3> Positions;
        [ReadOnly] public NativeArray<float> HoldUntil;
        [ReadOnly] public NativeArray<float> AppliedClipSpeed;

        public NativeArray<float3> Previous;
        public NativeArray<float> Speed;
        public NativeArray<byte> Gait;
        public NativeArray<byte> Wanted;
        public NativeArray<float> WantedClipSpeed;

        [WriteOnly] public NativeList<int> Due;

        public float Now;
        public float Elapsed;
        public float WalkAbove;
        public float RunAbove;
        public float Smoothing;

        public byte MatchSpeed;
        public float WalkAuthoredSpeed;
        public float RunAuthoredSpeed;
        public float ClipSpeedStep;
        public float MinimumClipSpeed;
        public float MaximumClipSpeed;

        public int Count;

        public void Execute()
        {
            /*
             * Framerate independent, so the same smoothing means the same thing however often this runs.
             */
            float blend = Elapsed <= 0f ? 1f : 1f - math.exp(-Smoothing * Elapsed);

            for (int i = 0; i < Count; i++)
            {
                float3 position = Positions[i];

                /*
                 * Measured and stored even for a body whose gait is being held, so one coming out of its spawn
                 * does not count everything it covered during the entrance as a single step of travel and come
                 * out of it sprinting.
                 */
                float moved = math.distance(position.xz, Previous[i].xz);
                float instant = Elapsed > 0f ? moved / Elapsed : 0f;

                Previous[i] = position;
                Speed[i] = math.lerp(Speed[i], instant, blend);

                byte current = Gait[i];

                if (current == (byte)HordeGait.DEAD) continue;

                /*
                 * An entrance is held for as long as the clip lasts rather than until something notices it
                 * finished. The animator returns itself to idle at the end, so coming out of the hold is only
                 * this job being allowed to have an opinion again, and the body is already on the right clip.
                 */
                if (current == (byte)HordeGait.SPAWNING)
                {
                    if (Now < HoldUntil[i]) continue;

                    current = (byte)HordeGait.IDLE;
                    Gait[i] = current;
                }

                float speed = Speed[i];
                byte wanted = (byte)(speed >= RunAbove
                    ? HordeGait.RUNNING
                    : speed >= WalkAbove ? HordeGait.WALKING : HordeGait.IDLE);

                float clipSpeed = ClipSpeedFor(wanted, speed);

                WantedClipSpeed[i] = clipSpeed;

                bool gaitChanged = wanted != current;
                bool speedChanged = MatchSpeed != 0 && math.abs(clipSpeed - AppliedClipSpeed[i]) > SPEED_EPSILON;

                if (!gaitChanged && !speedChanged) continue;

                Wanted[i] = wanted;
                Due.Add(i);
            }
        }

        /*
         * How fast the clip has to run for the feet to keep up with the ground. A walk authored for one and a half
         * metres a second, played by a body doing three, is the whole of what foot sliding is.
         *
         * Quantised, because this is applied by writing a material property and a body's speed wobbles constantly.
         * Snapping to steps means a body holding a steady pace writes once and then never again, and the step is
         * the dial between how closely the feet track and how often that write happens.
         *
         * Idle is left alone. Nothing about standing still has a speed to match, and scaling an idle by a speed
         * that is nearly zero would stop it dead.
         */
        private float ClipSpeedFor(byte gait, float speed)
        {
            if (MatchSpeed == 0 || gait == (byte)HordeGait.IDLE) return 1f;

            float authored = gait == (byte)HordeGait.RUNNING ? RunAuthoredSpeed : WalkAuthoredSpeed;

            if (authored <= 0f) return 1f;

            float wanted = math.clamp(speed / authored, MinimumClipSpeed, MaximumClipSpeed);

            return ClipSpeedStep > 0f ? math.round(wanted / ClipSpeedStep) * ClipSpeedStep : wanted;
        }

    }
}
