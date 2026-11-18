namespace Machines.Sim.World;

public sealed partial class GameWorld
{
    /// <summary>
    /// A fingerprint of the world's state for lockstep: players exchange it every few ticks, and any difference
    /// means their simulations went apart (a desync). Covers every actor in id order (position, facing, hit
    /// points, armour, order, race) and every race's bank, score, research and allies, as exact bits (FNV-1a, 64 bit).
    /// </summary>
    private long? _terrainFingerprint;

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
        // Shots are stopped by the ground: worlds with different ground go apart.
        Mix(_terrainFingerprint ??= Terrain?.Fingerprint() ?? 0);
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
            // Whose it is, only when that is not its race's first player: never in a match with one player per race.
            if (actor.OwnerOverride is { } owner)
            {
                Mix(OwnerTag);
                Mix(owner.Value);
            }
        }
        foreach (var artefact in _artefacts.Values.Where(a => a.HitPoints != a.MaxHitPoints || a.Armour != a.MaxArmour).OrderBy(a => a.Id))
        {
            Mix(artefact.Id);
            Mix(artefact.HitPoints);
            Mix(artefact.Armour);
        }
        Mix(_artefacts.Count);
        if (Script is not null)
            Mix(Script.Fingerprint());
        foreach (var mine in LandMines)
            Mix(mine.Id);
        foreach (var wreck in Debris)
        {
            Mix(wreck.Id);
            Mix(wreck.Quantity);
        }
        if (Wired is not null)
            Mix(WiredFingerprint());
        if (Battle is not null)
            Mix(BattleFingerprint());
        // The sighted drivers' views and their guns: nothing in a match without one.
        foreach (var (id, yaw) in PlayerYaws)
        {
            Mix(id);
            MixFloat(yaw);
            MixFloat(_heads.GetValueOrDefault(id));
        }
        foreach (var state in Races.Values.OrderBy(r => r.Race))
        {
            Mix((int)state.Race);
            MixFloat(state.Bmu);
            Mix(state.Score.Gross);
            Mix(state.Researched.Count);
            Mix(state.Defeated ? 1 : 0);
            Mix(state.Allies.Aggregate(0, (bits, ally) => bits | 1 << (int)ally));
        }
        // Who is away and who keeps whose machines, only where a race has several players.
        if (Players.AnyShared)
        {
            Mix(PlayersTag);
            Mix(Players.AwayPlayers.Count);
            foreach (var away in Players.AwayPlayers)
                Mix(away.Value);
            Mix(Players.Keepers.Count);
            foreach (var (kept, keeper) in Players.Keepers)
            {
                Mix(kept.Value);
                Mix(keeper.Value);
            }
        }
        // The bots' seats people joined late, only once there is one.
        if (Players.Joined.Count > 0)
        {
            Mix(JoinedTag);
            Mix(Players.Joined.Count);
            foreach (var joined in Players.Joined)
                Mix(joined.Value);
        }
        return hash;
    }

    /// <summary>The tags before an actor's owner, the players' presence and the seats joined late in the checksum, mixed only when there is any.</summary>
    private const long OwnerTag = 0x4F574E4552, PlayersTag = 0x504C4159455253, JoinedTag = 0x4A4F494E4544;
}
