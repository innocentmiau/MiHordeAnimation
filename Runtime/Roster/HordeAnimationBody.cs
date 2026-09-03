using MiVertexAnimation;
using UnityEngine;

namespace MiHordeAnimation
{
    /*
     * A component per entity, but not a script per entity in the sense that costs anything. There is no Update and
     * no FixedUpdate here, so once an entity has registered it is a row in an array and nothing on it is dispatched
     * again for as long as it lives. The whole per frame cost of the crowd's animation lives in one manager.
     *
     * Something has to exist on the prefab regardless, because the manager has to be told which VATAnimator belongs
     * to which body and there is no cheap way to ask a transform that later. Doing it here means the lookup happens
     * once, on the frame the entity is enabled, rather than in a search over the roster.
     *
     * The index is handed out by the manager and written back here rather than kept in a dictionary. A dictionary
     * keyed on a UnityEngine.Object hashes and then compares through the overridden equality, which calls out to
     * native, and at a few thousand lookups a frame that is the whole cost of the system. An int is a subscript.
     */
    /// <summary>
    /// Marks an entity as animated by the horde and holds the VATAnimator the manager plays clips on.
    /// </summary>
    [DisallowMultipleComponent]
    public class HordeAnimationBody : MonoBehaviour
    {

        [Tooltip("The animator to play clips on. Left empty it is found in the children on the first enable.")]
        [SerializeField] private VATAnimator animator;

        /// <summary>
        /// The animator this body plays its clips on, or null when the prefab has none.
        /// </summary>
        public VATAnimator Animator => animator;

        /// <summary>
        /// Where this body sits in the manager's roster, or minus one when it is not registered.
        /// </summary>
        public int AnimationIndex { get; set; } = -1;

        private void Reset() => animator = GetComponentInChildren<VATAnimator>(true);

        private void OnEnable()
        {
            animator = animator ? animator : GetComponentInChildren<VATAnimator>(true);

            HordeAttackAnimator.Register(this);
        }

        private void OnDisable() => HordeAttackAnimator.Unregister(this);

    }
}
