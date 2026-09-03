using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace MiHordeAnimation
{
    /*
     * The scan is the loop that runs over every body every frame, so it is the only part of this worth putting on a
     * worker. What comes out is not a flag per body but a list of the handful that should actually play something,
     * which is what keeps the main thread's share proportional to what happened rather than to the roster size.
     * A crowd of five thousand standing around a target has perhaps a few dozen in range and off cooldown.
     *
     * The cooldown is a timestamp rather than a list of who is busy. A list needs a membership test to skip and a
     * removal to expire, both linear, and it still needs a second number beside it to say when the entry comes out.
     * One float answers both questions in one compare: before it, this body is either mid clip or resting, and the
     * difference between those two does not matter to anything here.
     *
     * Claimed here rather than after the clip is played, so a body cannot be handed out twice by a later pass in
     * the same frame, and so the main thread's half of the frame is a play call and nothing else.
     */
    /// <summary>
    /// Picks out the bodies close enough to the target and off cooldown, and starts their cooldown as it does.
    /// </summary>
    [BurstCompile(FloatPrecision.Low, FloatMode.Fast)]
    public struct HordeAttackScanJob : IJob
    {

        [ReadOnly] public NativeArray<float3> Positions;

        public NativeArray<float> NextAllowed;
        public NativeList<int> Due;

        public float3 Target;
        public float RangeSquared;
        public float Now;
        public float Cooldown;
        public int Count;

        public void Execute()
        {
            for (int i = 0; i < Count; i++)
            {
                if (Now < NextAllowed[i]) continue;

                /*
                 * Flattened onto the ground plane, the same way the mover decides a body has arrived. A target
                 * standing on a step or floating a metre up would otherwise push every body in the crowd out of
                 * range at once, and the failure looks like the range value being ignored.
                 */
                if (math.distancesq(Positions[i].xz, Target.xz) > RangeSquared) continue;

                NextAllowed[i] = Now + Cooldown;
                Due.Add(i);
            }
        }

    }
}
