namespace FarmAssistant.Fishing.Core;

/// <summary>
/// Counts of each tackle in <c>BobberBar.bobbers</c> that changes the minigame.
/// Cork only makes the bar taller; the mod reads the bar height directly, so it is
/// counted for the offline minigame replica, not by the controller.
/// </summary>
public readonly record struct Tackle(int Barbed, int Lead, int TreasureHunter, int Trap, int Cork)
{
    public const string BarbedId = "(O)691";
    public const string LeadId = "(O)692";
    public const string TreasureHunterId = "(O)693";
    public const string TrapId = "(O)694";
    public const string CorkId = "(O)695";

    public static Tackle FromIds(IEnumerable<string>? ids)
    {
        int barbed = 0, lead = 0, hunter = 0, trap = 0, cork = 0;
        if (ids != null)
        {
            foreach (string id in ids)
            {
                switch (id)
                {
                    case BarbedId: barbed++; break;
                    case LeadId: lead++; break;
                    case TreasureHunterId: hunter++; break;
                    case TrapId: trap++; break;
                    case CorkId: cork++; break;
                }
            }
        }
        return new Tackle(barbed, lead, hunter, trap, cork);
    }
}
