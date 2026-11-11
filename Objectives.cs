using System.Windows;

namespace RiftPen;

/// <summary>An epic monster's state at one moment: alive ("up"), or how long until it spawns.</summary>
public sealed record ObjectiveTimer(string Name, Point Pit, bool Up, double SpawnsIn, string Kind);

/// <summary>
/// Works out dragon, Void Grub, Rift Herald and Baron timers from the kills in Riot's timeline. Spawn times follow
/// the current Summoner's Rift rules (drakes 5:00 then every 5:00, Elder every 6:00 once a team has its soul,
/// Void Grubs 8:00, Rift Herald 16:00, Baron 20:00 then every 6:00); replays from much older patches may differ.
/// </summary>
public static class Objectives
{
    public static readonly Point DragonPit = new(9866, 4414), BaronPit = new(5007, 10471);

    const double DragonFirst = 300, DragonRespawn = 300, ElderRespawn = 360;
    const double GrubsSpawn = 480, HeraldSpawn = 960, HeraldGone = 1185, BaronFirst = 1200, BaronRespawn = 360;

    static int KillerTeam(GameTimeline g, GameEvent e) => g.P(e.KillerId)?.Team ?? (e.TeamId == 100 ? 200 : e.TeamId == 200 ? 100 : 0);

    public static List<ObjectiveTimer> At(GameTimeline g, double t)
    {
        var list = new List<ObjectiveTimer>();
        var kills = g.Events.Where(e => e.Type == "ELITE_MONSTER_KILL" && e.Time <= t).ToList();

        // Dragon pit: elemental drakes until a team has four (its soul), then the Elder Dragon.
        var drakes = kills.Where(e => e.MonsterType == "DRAGON").ToList();
        var elemental = drakes.Where(e => e.MonsterSubType != "ELDER_DRAGON").ToList();
        bool soul = elemental.GroupBy(e => KillerTeam(g, e)).Any(grp => grp.Key != 0 && grp.Count() >= 4);
        string dragonName = soul ? "Elder Dragon" : "Drake";
        if (drakes.Count == 0) list.Add(Make(dragonName, DragonPit, DragonFirst - t, "dragon"));
        else
        {
            var last = drakes[^1];
            double respawn = soul || last.MonsterSubType == "ELDER_DRAGON" ? ElderRespawn : DragonRespawn;
            list.Add(Make(dragonName, DragonPit, last.Time + respawn - t, "dragon"));
        }

        // Baron pit: Void Grubs, then the Rift Herald, then Baron Nashor.
        int grubs = kills.Count(e => e.MonsterType == "HORDE");
        bool heraldTaken = kills.Any(e => e.MonsterType == "RIFTHERALD");
        var barons = kills.Where(e => e.MonsterType == "BARON_NASHOR").ToList();
        if (t < GrubsSpawn) list.Add(Make("Void Grubs", BaronPit, GrubsSpawn - t, "grubs"));
        else if (grubs < 3 && t < HeraldSpawn - 15) list.Add(Make("Void Grubs", BaronPit, 0, "grubs"));
        else if (!heraldTaken && t < HeraldGone) list.Add(Make("Rift Herald", BaronPit, HeraldSpawn - t, "herald"));
        else list.Add(Make("Baron", BaronPit, (barons.Count == 0 ? BaronFirst : barons[^1].Time + BaronRespawn) - t, "baron"));
        return list;
    }

    static ObjectiveTimer Make(string name, Point pit, double spawnsIn, string kind) =>
        new(name, pit, spawnsIn <= 0, Math.Max(0, spawnsIn), kind);

    public static string Clock(double seconds) => TimeSpan.FromSeconds(Math.Ceiling(Math.Max(0, seconds))).ToString(@"m\:ss");
}
