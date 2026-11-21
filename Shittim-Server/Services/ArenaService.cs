using AutoMapper;
using Schale.Data;
using Schale.Data.GameModel;
using Schale.Data.ModelMapping;
using Schale.FlatData;
using Schale.MX.GameLogic.DBModel;

namespace Shittim_Server.Services;

public class ArenaService
{
    public static ArenaTeamSettingDB DummyTeamFormation = new()
    {
        EchelonType = EchelonType.ArenaDefence,
        LeaderCharacterId = 10065,
        MainCharacters =
        [
            new ArenaCharacterDB()
            {
                UniqueId = 10065,
                StarGrade = 3,
                Level = 90,
                PublicSkillLevel = 1,
                ExSkillLevel = 1,
                PassiveSkillLevel = 1,
                ExtraPassiveSkillLevel = 1,
                LeaderSkillLevel = 1
            }
        ],
        MapId = 1001,
    };

    // Placeholder accounts live above this id so they can never collide with a real account.
    public const long MockAccountIdFloor = 8_000_000;

    private static readonly string[] NameParts =
    [
        "ka", "ri", "yu", "mi", "sa", "to", "na", "hi", "re", "ko", "ma", "shi", "ta", "no", "ra", "se",
    ];

    // PvP opponents are placeholders: one defence row for the player, plus a board of generated players so
    // the arena looks populated. Teams come from the preset file when it has any, otherwise from the
    // fallback board; the students themselves are picked deterministically per opponent.
    public static List<ArenaUserDB> CreateOpponents(
        SchaleDataContext context,
        AccountDBServer account,
        IMapper mapper,
        LeaderboardPresetsService presets,
        int startRank = 1,
        int nonce = 0)
    {
        // Row one must be an opponent: the player's own defence is shown in its own panel, not in the list.
        // The client shows three rows (rank, rank+1, rank+2). The window moves on every refresh and the
        // nonce makes each refresh produce different squads, so the board does not look frozen.
        var result = new List<ArenaUserDB>();
        var squads = presets.Squads("pvp");

        for (var index = 0; index < 3; index++)
        {
            var rank = startRank + index;
            var opponentId = MockAccountIdFloor + (long)rank * 1000L + nonce % 1000;
            var team = BuildArenaTeam(context, account, mapper, squads, opponentId, rank);
            if (team == null)
                break;

            result.Add(CreateArenaUser(
                opponentId,
                team.LeaderCharacterId,
                MockName(opponentId),
                rank,
                90,
                team));
        }

        return result;
    }

    public static ArenaTeamSettingDB? BuildArenaTeam(
        SchaleDataContext context,
        AccountDBServer account,
        IMapper mapper,
        IReadOnlyList<MockSquad> squads,
        long seed,
        long rank = 1)
    {
        if (squads.Count == 0)
            return null;

        var random = new Random(unchecked((int)(seed * 2654435761L)));
        var squad = squads[random.Next(squads.Count)];

        var strikers = squad.Strikers.ToList();
        var specials = squad.Specials.ToList();

        for (var i = strikers.Count - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (strikers[i], strikers[j]) = (strikers[j], strikers[i]);
        }

        var main = strikers.Take(4).Select((id, slot) => SlotCharacter(context, account, mapper, id, slot, seed)).ToList();
        if (main.Count == 0)
            return null;

        var support = specials.Take(2).Select((id, slot) => SlotCharacter(context, account, mapper, id, 4 + slot, seed)).ToList();

        // Placeholder opponents are not all maxed: the top of the board is close to account level, the
        // lower rows fall off, so a mid-game roster can actually win a fight.
        ApplyRankScaling([.. main, .. support], rank, seed);

        return new ArenaTeamSettingDB
        {
            EchelonType = EchelonType.ArenaDefence,
            LeaderCharacterId = main[0].UniqueId,
            MainCharacters = main,
            SupportCharacters = support,
            SquadCount = 1,
            MapId = 1001,
        };
    }

    // The client shows its unknown-character placeholder (level 53, empty portrait) when a formation entry
    // has no weapon, gear or equipment objects. Placeholder squads therefore copy the row of a real
    // character on the requesting account, and only fall back to a synthetic entry when nothing matches.
    public static List<ArenaCharacterDB> CreateArenaCharacterDBsFromUniqueIds(
        SchaleDataContext context,
        AccountDBServer account,
        IMapper mapper,
        IEnumerable<long> uniqueIds)
    {
        var serverIds = new List<long>();
        var ownedCharacters = context.GetAccountCharacters(account.ServerId).ToList();

        foreach (var uniqueId in uniqueIds)
        {
            var character = ownedCharacters.FirstOrDefault(c => c.UniqueId == uniqueId);
            if (character != null)
                serverIds.Add(character.ServerId);
        }

        return CreateArenaCharacterDBs(context, account, mapper, serverIds);
    }

    // Rank 1 sits at level 90 with five stars, the bottom of the visible board around level 65 with three
    // to four stars. Levels and stars are jittered per slot so rows do not look copy-pasted.
    private static readonly Dictionary<int, int> WeaponMaxLevel = new()
    {
        [1] = 30,
        [2] = 40,
        [3] = 50,
        [4] = 60,
    };

    private static void ApplyRankScaling(List<ArenaCharacterDB> characters, long rank, long seed)
    {
        var safeRank = Math.Clamp(rank, 1, 30);
        var strength = Math.Clamp(1.0 - (safeRank - 1) / 12.0, 0.35, 1.0);
        var random = new Random(unchecked((int)(seed * 7919L)));

        foreach (var character in characters)
        {
            var level = 60 + (int)Math.Round(strength * 30) + random.Next(-4, 5);
            var stars = 2 + (int)Math.Round(strength * 3) + random.Next(-1, 2);

            character.Level = Math.Clamp(level, 55, 90);
            character.StarGrade = Math.Clamp(stars, 2, 5);

            // Unique equipment follows the same curve: a low row rarely has more than a ★2 weapon, and its
            // level is capped by the star grade band (30/40/50/60).
            if (character.WeaponDB != null)
            {
                var weaponStars = Math.Clamp(stars - 1 + random.Next(0, 2), 1, 4);
                var cap = WeaponMaxLevel[weaponStars];
                character.WeaponDB.StarGrade = weaponStars;
                character.WeaponDB.Level = Math.Clamp((int)Math.Round(level * cap / 60.0), 1, cap);
            }

            // Weak rows also carry fewer equipped items and lower tiers, instead of a full maxed set.
            var tierBand = Math.Clamp((int)Math.Round(strength * 10), 1, 10);

            if (character.EquipmentDBs is { Count: > 0 })
            {
                var keep = Math.Clamp((int)Math.Round(strength * character.EquipmentDBs.Count), 1, character.EquipmentDBs.Count);
                var kept = character.EquipmentDBs.Take(keep).ToList();

                foreach (var equipment in kept)
                {
                    equipment.Tier = Math.Max(1, tierBand);
                    equipment.Level = Math.Clamp((int)Math.Round(level * 0.8), 1, 70);
                }

                character.EquipmentDBs = kept;
            }

            if (character.GearDB != null)
            {
                character.GearDB.Tier = Math.Max(1, tierBand);
                character.GearDB.Level = Math.Clamp((int)Math.Round(level * 0.8), 1, 70);
            }
        }
    }

    private static ArenaCharacterDB SlotCharacter(
        SchaleDataContext context,
        AccountDBServer account,
        IMapper mapper,
        long uniqueId,
        int slot,
        long seed)
    {
        var character = CreateArenaCharacterDBsFromUniqueIds(context, account, mapper, [uniqueId]).FirstOrDefault()
            ?? ArenaCharacter(uniqueId, slot, seed);

        character.SlotIndex = slot;
        return character;
    }

    private static ArenaCharacterDB ArenaCharacter(long uniqueId, int slot, long seed) => new()
    {
        ServerId = seed * 10 + slot,
        UniqueId = uniqueId,
        StarGrade = 5,
        Level = 90,
        PublicSkillLevel = 10,
        ExSkillLevel = 5,
        PassiveSkillLevel = 10,
        ExtraPassiveSkillLevel = 10,
        LeaderSkillLevel = 1,
        // Without a per-slot index every character lands in slot 0 and the client draws one icon per role.
        SlotIndex = slot,
        FavorRankInfo = new Dictionary<long, long> { { uniqueId, 20 } },
    };

    public static string MockName(long seed)
    {
        var random = new Random(unchecked((int)(seed * 40503L)));
        var first = NameParts[random.Next(NameParts.Length)];
        var second = NameParts[random.Next(NameParts.Length)];
        var name = char.ToUpperInvariant(first[0]) + first[1..] + second;
        return random.Next(2) == 0 ? name + random.Next(10, 99) : name;
    }

    public static ArenaUserDB CreateArenaUser(long accountId, long representCharacterServerId, string nickname, long rank, int level, ArenaTeamSettingDB teamSettingDB)
    {
        return new ArenaUserDB()
        {
            AccountServerId = accountId,
            RepresentCharacterUniqueId = representCharacterServerId,
            NickName = nickname ?? "Unknown Sensei",
            Rank = rank,
            Level = level,
            TeamSettingDB = teamSettingDB
        };
    }

    public static ArenaTeamSettingDB CreateArenaTeamSetting(
        SchaleDataContext context, AccountDBServer account, IMapper mapper, bool isDefense = false)
    {
        var echelonType = isDefense ? EchelonType.ArenaDefence : EchelonType.ArenaAttack;
        var arenaEchelon = context.GetAccountEchelons(account.ServerId)
            .OrderBy(e => e.ServerId)
            .LastOrDefault(e =>
                e.EchelonType == echelonType &&
                e.EchelonNumber == 1 &&
                e.ExtensionType == EchelonExtensionType.Base);

        if (arenaEchelon == null) return DummyTeamFormation;

        var leaderCharacterId = context.Characters
            .FirstOrDefault(c => c.ServerId == arenaEchelon.LeaderServerId)?.UniqueId ?? 0;

        return new ArenaTeamSettingDB()
        {
            EchelonType = arenaEchelon.EchelonType,
            LeaderCharacterId = leaderCharacterId,
            MainCharacters = CreateArenaCharacterDBs(context, account, mapper, arenaEchelon.MainSlotServerIds),
            SupportCharacters = CreateArenaCharacterDBs(context, account, mapper, arenaEchelon.SupportSlotServerIds),
            MapId = 1001
        };
    }

    public static List<ArenaCharacterDB> CreateArenaCharacterDBs(SchaleDataContext context, AccountDBServer account, IMapper mapper, List<long> characterServerIds)
    {
        List<ArenaCharacterDB> arenaCharacters = [];
        
        foreach (var characterServerId in characterServerIds)
        {
            var character = context.GetAccountCharacters(account.ServerId)
                .FirstOrDefault(c => c.ServerId == characterServerId);
            if (character == null) continue;

            var arenaCharacter = new ArenaCharacterDB
            {
                UniqueId = character.UniqueId,
                StarGrade = character.StarGrade,
                Level = character.Level,
                PublicSkillLevel = character.PublicSkillLevel,
                ExSkillLevel = character.ExSkillLevel,
                PassiveSkillLevel = character.PassiveSkillLevel,
                ExtraPassiveSkillLevel = character.ExtraPassiveSkillLevel,
                LeaderSkillLevel = character.LeaderSkillLevel,
                FavorRankInfo = new() { { character.UniqueId, character.FavorRank } },
                PotentialStats = character.PotentialStats,
                EquipmentDBs = character.EquipmentServerIds
                    .Select(i => context.GetAccountEquipments(account.ServerId).FirstOrDefault(e => e.ServerId == i))
                    .Where(e => e != null)
                    .Select(e => e!.ToMap(mapper))
                    .ToList()
            };

            var weaponDB = context.GetAccountWeapons(account.ServerId)
                .FirstOrDefault(w => w.BoundCharacterServerId == character.ServerId);
            if (weaponDB != null) 
                arenaCharacter.WeaponDB = weaponDB.ToMap(mapper);

            var gearDB = context.GetAccountGears(account.ServerId)
                .FirstOrDefault(g => g.BoundCharacterServerId == character.ServerId);
            if (gearDB != null) 
                arenaCharacter.GearDB = gearDB.ToMap(mapper);

            arenaCharacters.Add(arenaCharacter);
        }

        return arenaCharacters;
    }
}
