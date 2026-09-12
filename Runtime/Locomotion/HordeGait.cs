namespace MiHordeAnimation
{
    /*
     * What a body is doing, as far as the animation is concerned. Kept deliberately small: these are the states a
     * clip can be chosen from, not a description of the game's own state machine, and anything that needs to know
     * more than which clip should be playing wants its own.
     *
     * Spawning and dead are in here rather than being a separate flag because they are the two states the gait
     * must not be allowed to overrule. Everything else is decided every frame from how fast the body is moving,
     * and these two are decided once and held.
     */
    /// <summary>
    /// Which clip a horde body should be playing, chosen from how fast it is moving.
    /// </summary>
    public enum HordeGait
    {
        IDLE, // standing still, or moving too slowly to be worth a walk
        WALKING,
        RUNNING,
        SPAWNING, // playing its entrance, and not to be interrupted
        DEAD // holding the last frame of its death, and never leaving this state
    }
}
