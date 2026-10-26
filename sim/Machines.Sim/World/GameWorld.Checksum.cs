namespace Machines.Sim.World;

public sealed partial class GameWorld
{
    /// <summary>
    /// A fingerprint of the world's state for lockstep: players exchange it every few ticks, and any difference
    /// means their simulations went apart (a desync). Covers every actor in id order (position, facing, hit
    /// points, armour, order, race) and every race's bank, score and research, as exact bits (FNV-1a, 64 bit).
    /// </summary>
    public ulong Checksum()
    {
        var hash = 14695981039346656037UL;
        void Mix(long value)
        {
            for (var i = 0; i < 8; i++)
            {
                hash ^= (byte)(value >> (8 * i));
                hash *= 1099511628211UL;
            }
        }
        void MixFloat(float value) => Mix(BitConverter.SingleToInt32Bits(value));

        Mix(Tick);
        foreach (var actor in _actors.Values.OrderBy(a => a.Id))
        {
            Mix(actor.Id);
            Mix((int)actor.Race);
            MixFloat(actor.Position.X);
            MixFloat(actor.Position.Y);
            Mix(actor.HitPoints);
            Mix(actor.Armour);
            if (actor is Machine machine)
            {
                MixFloat(machine.Facing);
                Mix(machine.Order.GetType().Name.Length);
                Mix(machine.Inside ?? -1);
            }
            else if (actor is Construction building)
            {
                MixFloat(building.Built);
            }
        }
        foreach (var state in Races.Values.OrderBy(r => r.Race))
        {
            Mix((int)state.Race);
            MixFloat(state.Bmu);
            Mix(state.Score.Gross);
            Mix(state.Researched.Count);
            Mix(state.Defeated ? 1 : 0);
        }
        return hash;
    }
}
