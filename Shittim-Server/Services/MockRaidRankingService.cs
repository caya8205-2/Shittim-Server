using BlueArchiveAPI.Configuration;
using BlueArchiveAPI.Services;
using Schale.Data.GameModel;
using Schale.FlatData;
using Schale.MX.GameLogic.DBModel;

namespace Shittim_Server.Services;

// A squad taken from the account's own echelon presets, used as the pool for the placeholder ranking rows.
public sealed record MockSquad(IReadOnlyList<long> Strikers, IReadOnlyList<long> Specials);

// Fills the Total Assault ranking screen on a server that only has one or two real accounts. Entries are
// generated from the season id, so the same season always shows the same board, and the score curve mimics
// a real Binah season rather than random noise. Squads are the player's own presets, shuffled per row;
// the rows themselves are placeholders and no real player data is involved.
public class MockRaidRankingService
{
    // Handles are assembled from syllables instead of student names: the board should read like a list of
    // players, not like the game's character roster.
    private static readonly string[] SyllablesA =
    [
        "ka", "ri", "yu", "mi", "sa", "to", "na", "hi", "re", "ko",
        "ma", "shi", "ta", "no", "ra", "se", "ai", "ki", "so", "ha",
    ];

    private static readonly string[] SyllablesB =
    [
        "ki", "na", "to", "ru", "mi", "ya", "ka", "no", "ri", "su",
        "ne", "ha", "mo", "chi", "zu", "da", "en", "ro", "tsu", "wa",
    ];

    private readonly ExcelTableService _excel;
    private readonly ILogger<MockRaidRankingService> _logger;
    private List<long>? _allPlayable;
    private List<(Difficulty Difficulty, double Share, double FloorFactor, long Min, long Max)>? _bands;

    public MockRaidRankingService(ExcelTableService excel, ILogger<MockRaidRankingService> logger)
    {
        _excel = excel;
        _logger = logger;
    }

    // The client opens medal tabs at ranks 1, 10001, 50001 and 100001. Tier is the medal the client prints
    // on each row: 4 = Platinum, 3 = Gold, 2 = Silver, 1 = Bronze.
    private static readonly (long Start, long End, int Tier)[] TabBrackets =
    [
        (1, 10_000, 4),
        (10_001, 50_000, 3),
        (50_001, 100_000, 2),
        (100_001, 200_000, 1),
    ];

    // Difficulty by ladder position: Platinum is Lunatic/Torment, Insane/Extreme only start in Gold.
    private static readonly (long Start, long End, Difficulty Difficulty)[] Ladder =
    [
        (1, 500, Difficulty.Lunatic),
        (501, 10_000, Difficulty.Torment),
        (10_001, 30_000, Difficulty.Insane),
        (30_001, 70_000, Difficulty.Extreme),
        (70_001, 200_000, Difficulty.Hardcore),
    ];

    public static (long Start, long End, int Tier) BracketOf(long rank)
    {
        foreach (var bracket in TabBrackets)
        {
            if (rank <= bracket.End)
                return bracket;
        }

        return TabBrackets[^1];
    }

    private static Difficulty LadderDifficulty(long rank)
    {
        foreach (var (start, end, difficulty) in Ladder)
        {
            if (rank <= end)
                return difficulty;
        }

        return Ladder[^1].Difficulty;
    }

    // Definitions for Raid_RankingIndex: one entry per medal tab, in board order.
    public List<RaidRankBracket> Brackets()
    {
        return TabBrackets
            .Select(x => new RaidRankBracket
            {
                Difficulty = LadderDifficulty(x.Start),
                BossGroupIndex = null,
                RankCount = x.End - x.Start + 1,
            })
            .ToList();
    }

    public List<SingleRaidUserDB> Build(
        AccountDBServer account,
        long seasonId,
        long ownCharacterUniqueId,
        IReadOnlyList<MockSquad> squads,
        long startRank = 1,
        int pageSize = 100)
    {
        var config = BlueArchiveAPI.Configuration.Config.Instance?.ServerConfiguration?.MockRaidRanking;
        if (config == null || !config.Enabled)
            return [];

        var top = Math.Max(config.TopScore, config.BottomScore);
        var bottom = Math.Min(config.TopScore, config.BottomScore);

        var firstRank = Math.Max(1, startRank);
        var page = Math.Clamp(pageSize, 1, 100);
        var entries = new List<SingleRaidUserDB>(page);

        for (var index = 0; index < page; index++)
        {
            var rank = firstRank + index;
            var (difficulty, score) = LadderScore(rank, top, bottom);

            var accountId = 9_000_000 + rank;

            // Tier is the medal bracket the row belongs to; the difficulty label on screen comes from the
            // score, so the two must not be conflated.
            var bracket = BracketOf(rank);

            entries.Add(new SingleRaidUserDB
            {
                AccountId = accountId,
                Nickname = HandleFor(rank),
                RepresentCharacterUniqueId = Representative(rank),
                RepresentCharacterCostumeId = 0,
                Level = bracket.Tier >= 3 ? 90 : bracket.Tier == 2 ? 88 : 62 + (int)(rank % 23),
                Tier = bracket.Tier,
                Rank = rank,
                BestRankingPoint = score,
                BestRankingPointDetail = Math.Round(95.0 + (rank % 70), 3),
                // Squad and equipment follow the row's difficulty, so a row that cleared Insane never
                // displays a fully maxed roster.
                RaidTeamSettingDB = BuildTeam(seasonId, accountId, squads, bracket, rank),
            });
        }

        // The player's own row is only meaningful inside the bracket their score belongs to.
        var raidData = account.ContentInfo?.RaidDataInfo;
        if (raidData != null && raidData.BestRankingPoint > 0)
        {
            var ownRank = RankForScore(raidData.BestRankingPoint, top, bottom);

            if (ownRank >= firstRank && ownRank < firstRank + page)
            {
                var ownBracket = BracketOf(ownRank);

                entries.RemoveAll(x => x.Rank == ownRank);
                entries.Add(new SingleRaidUserDB
                {
                    AccountId = account.ServerId,
                    Nickname = account.Nickname,
                    RepresentCharacterUniqueId = ownCharacterUniqueId,
                    RepresentCharacterCostumeId = 0,
                    Level = Math.Max(1, account.Level),
                    Tier = ownBracket.Tier,
                    Rank = ownRank,
                    BestRankingPoint = raidData.BestRankingPoint,
                    BestRankingPointDetail = 0,
                    // The row shows the squad from the best clear; presets are only a fallback.
                    RaidTeamSettingDB = OwnTeam(account) ?? BuildTeam(seasonId, account.ServerId, squads, ownBracket, ownRank, allowRealAccount: true),
                });
            }
        }

        return entries.OrderBy(x => x.Rank).ToList();
    }

    // Absolute ladder position of a score, for the lobby panel. Derived from the same ladder the board is
    // built from, so the rank shown in the lobby agrees with the row in the ranking list.
    public long RankOf(long bestRankingPoint)
    {
        var config = BlueArchiveAPI.Configuration.Config.Instance?.ServerConfiguration?.MockRaidRanking;
        if (config == null || !config.Enabled || bestRankingPoint <= 0)
            return 0;

        var top = Math.Max(config.TopScore, config.BottomScore);
        var bottom = Math.Min(config.TopScore, config.BottomScore);

        return RankForScore(bestRankingPoint, top, bottom);
    }

    // Ladder position for a score, used to place the player's own row in the right bracket.
    public long RankForScore(long score, long top, long bottom)
    {
        long best = Ladder[^1].End;

        foreach (var (start, end, difficulty) in Ladder)
        {
            var (min, max) = ScoreRange(difficulty, top, bottom);
            if (score >= min)
                return Math.Clamp(RankWithin(start, end, score, min, max), start, end);

            best = start;
        }

        return best;
    }

    private static long RankWithin(long start, long end, long score, long min, long max)
    {
        var span = Math.Max(1, end - start + 1);
        if (max <= min)
            return start;

        var fraction = Math.Clamp((double)(max - score) / (max - min), 0, 1);
        return start + (long)Math.Round(fraction * (span - 1));
    }

    // Difficulty and score for an absolute ladder position. Brackets are contiguous so a page never mixes
    // two difficulties, and the top 50 (Platinum) is Lunatic as on a real board.
    private (Difficulty Difficulty, long Score) LadderScore(long rank, long top, long bottom)
    {
        foreach (var (start, end, difficulty) in Ladder)
        {
            if (rank > end)
                continue;

            var (min, max) = ScoreRange(difficulty, top, bottom);
            var span = Math.Max(1, end - start + 1);
            var offset = Math.Clamp(rank - start, 0, span - 1);
            var fraction = span <= 1 ? 0d : (double)offset / (span - 1);

            var score = max - (long)Math.Round((max - min) * Math.Pow(fraction, 1.25));
            return (difficulty, Math.Clamp(score, min, max));
        }

        var last = Ladder[^1];
        var (lastMin, _) = ScoreRange(last.Difficulty, top, bottom);
        return (last.Difficulty, lastMin);
    }

    private (long Min, long Max) ScoreRange(Difficulty difficulty, long top, long bottom)
    {
        foreach (var band in Bands(top, bottom))
        {
            if (band.Difficulty == difficulty)
                return (band.Min, band.Max);
        }

        return (bottom, top);
    }

    // Relative spending tier the row represents, from the medal bracket and how deep inside it the rank
    // sits: Platinum = whaler, Bronze = F2P/new. Depth matters because even inside a bracket the top rows
    // are further along than the ones below.
    private static double StrengthForBracket((long Start, long End, int Tier) bracket, long rank)
    {
        // Depth inside the bracket: 0 at its top, 1 at its bottom.
        var span = Math.Max(1, bracket.End - bracket.Start);
        var depth = Math.Clamp((double)(rank - bracket.Start) / span, 0, 1);

        // Base by medal (4 Platinum .. 1 Bronze), scaled down as the rank falls inside that bracket.
        return bracket.Tier switch
        {
            4 => 1.0 - 0.15 * depth,
            3 => 0.75 - 0.15 * depth,
            2 => 0.52 - 0.14 * depth,
            _ => 0.34 - 0.18 * depth,
        };
    }

    // How far along a single character is: 5T = maxed unique equipment, 5 = mystic unlocked but no UE,
    // 4 = no UE at all, 3 = early. The mix gives the "some UE60, some UE50/40, most none" look.
    private enum CharacterSpec { Early, Base, Mystic, Ue30, Ue40, Ue50, Ue60 }

    // Chooses a spec for one slot from the row's overall strength. Platinum leans to maxed UE; Bronze is
    // almost entirely below mystic; Silver/Gold sit in between, with only one or two UE characters.
    private static CharacterSpec SpecFor(double strength, Random random, int slot = 0)
    {
        var roll = random.NextDouble();

        if (strength >= 0.85)
        {
            // Whaler: mostly maxed UE60, some UE50/40.
            return roll < 0.72 ? CharacterSpec.Ue60
                : roll < 0.88 ? CharacterSpec.Ue50
                : CharacterSpec.Ue40;
        }

        if (strength >= 0.60)
        {
            // Dolphin: the odd maxed carry, rest UE30/50 and plain mystic.
            return roll < 0.18 ? CharacterSpec.Ue60
                : roll < 0.42 ? CharacterSpec.Ue50
                : roll < 0.60 ? CharacterSpec.Ue30
                : roll < 0.88 ? CharacterSpec.Mystic
                : CharacterSpec.Base;
        }

        if (strength >= 0.38)
        {
            // Low spender: at most two UE30 in the whole team, the rest mystic-no-UE or plain 4-star.
            if (slot >= 2)
                return roll < 0.62 ? CharacterSpec.Mystic
                    : roll < 0.93 ? CharacterSpec.Base
                    : CharacterSpec.Early;

            return roll < 0.16 ? CharacterSpec.Ue30
                : roll < 0.56 ? CharacterSpec.Mystic
                : roll < 0.90 ? CharacterSpec.Base
                : CharacterSpec.Early;
        }

        // F2P / new: never mystic, never UE. 4-star at best, early-game students below that.
        return roll < 0.62 ? CharacterSpec.Base : CharacterSpec.Early;
    }

    private static RaidCharacterDB SpecCharacter(long uniqueId, int slot, long accountId, CharacterSpec spec, int serverSlot = -1) => spec switch
    {
        CharacterSpec.Ue60 => BuildSpec(uniqueId, slot, accountId, star: 5, level: 90, ue: 4, serverSlot: serverSlot),
        CharacterSpec.Ue50 => BuildSpec(uniqueId, slot, accountId, star: 5, level: 88, ue: 3, serverSlot: serverSlot),
        CharacterSpec.Ue40 => BuildSpec(uniqueId, slot, accountId, star: 5, level: 85, ue: 3, serverSlot: serverSlot),
        CharacterSpec.Ue30 => BuildSpec(uniqueId, slot, accountId, star: 5, level: 85, ue: 2, serverSlot: serverSlot),
        CharacterSpec.Mystic => BuildSpec(uniqueId, slot, accountId, star: 5, level: 85, ue: 0, serverSlot: serverSlot),
        CharacterSpec.Base => BuildSpec(uniqueId, slot, accountId, star: 4, level: 85, ue: 0, serverSlot: serverSlot),
        _ => BuildSpec(uniqueId, slot, accountId, star: 3, level: 80, ue: 0, serverSlot: serverSlot),
    };

    private static RaidCharacterDB BuildSpec(long uniqueId, int slot, long accountId, int star, int level, int ue, int serverSlot)
    {
        // Star grade is capped at 5 (mystic): the client renders anything above that as extra yellow stars.
        var clampedStar = Math.Clamp(star, 3, 5);

        return new()
        {
            ServerId = accountId * 10 + (serverSlot < 0 ? slot : serverSlot),
            UniqueId = uniqueId,
            StarGrade = clampedStar,
            Level = Math.Clamp(level, 1, 90),
            SlotIndex = slot,
            AccountId = accountId,
            IsAssist = false,
            // Unique equipment only exists once the student is 5-star (mystic unlocked).
            HasWeapon = clampedStar == 5 && ue > 0,
            WeaponStarGrade = clampedStar == 5 ? Math.Clamp(ue, 0, 4) : 0,
            CostumeId = 0,
            CombatStyleIndex = 0,
        };
    }

    private long Representative(long rank)
    {
        var pool = AllPlayable();
        if (pool.Count == 0)
            return 0;

        var random = new Random(unchecked((int)(rank * 2654435761L)));
        return pool[random.Next(pool.Count)];
    }

    // Answer for Raid_GetBestTeam, so the Details button on a placeholder row opens the same squad the row
    // previews. Real account ids answer null: their team lives in the account's own tables.
    // Score band for a position on the board: target ranges per difficulty rather than one smooth curve,
    // because the client labels each row by the band its score falls into.
    // The client labels every row with the difficulty its score belongs to, so each band is placed inside the
    // real score range of that difficulty. A clear score is DefaultClearScore + HPPercentScore, and the time
    // component can add up to MaximumScore on top of it.
    private (long Min, long Max, int Tier, double Fraction) BandFor(int index, int count, long top, long bottom)
    {
        var bands = Bands(top, bottom);
        var position = (double)index / Math.Max(1, count);
        var cursor = 0.0;

        for (var i = 0; i < bands.Count; i++)
        {
            var (difficulty, share, floorFactor, min, max) = bands[i];
            var next = cursor + share;

            // The last band absorbs any rounding left over.
            if (position < next || i == bands.Count - 1)
            {
                var local = share <= 0 ? 0d : (position - cursor) / share;
                var bandMin = (long)(max * floorFactor);

                return (bandMin, max, (int)difficulty, Math.Clamp(local, 0, 1));
            }

            cursor = next;
        }

        var last = bands[^1];
        return ((long)(last.Max * last.FloorFactor), last.Max, (int)last.Difficulty, 0d);
    }

    private List<(Difficulty Difficulty, double Share, double FloorFactor, long Min, long Max)> Bands(long top, long bottom)
    {
        if (_bands != null)
            return _bands;

        var list = new List<(Difficulty, double, double, long, long)>();

        try
        {
            var stages = _excel.GetTable<RaidStageExcelT>().ToList();

            foreach (var (difficulty, share, floorFactor) in new[]
                     {
                         (Difficulty.Lunatic, 0.30, 0.985),
                         (Difficulty.Torment, 0.40, 0.970),
                         (Difficulty.Insane, 0.15, 0.95),
                         (Difficulty.Extreme, 0.10, 0.95),
                         (Difficulty.Hardcore, 0.05, 0.95),
                     })
            {
                var ceiling = CeilingFor(stages, difficulty);
                if (ceiling <= 0)
                    continue;

                list.Add((difficulty, share, floorFactor, (long)(ceiling * floorFactor), ceiling));
            }
        }
        catch
        {
            // Table unavailable: keep whatever the configured range gives.
        }

        if (list.Count == 0)
        {
            var high = Math.Max(top, bottom);
            list.Add((Difficulty.Lunatic, 0.30, 0.97, (long)(high * 0.97), high));
            list.Add((Difficulty.Torment, 0.40, 0.94, (long)(high * 0.60), (long)(high * 0.75)));
            list.Add((Difficulty.Extreme, 0.30, 0.90, (long)(high * 0.45), (long)(high * 0.55)));
        }

        _bands = list;
        return _bands;
    }

    // Upper score the client still accepts for a difficulty. Above it the row is not recognised and the
    // client falls back to printing "Normal", which is why placeholder scores must stay under these caps.
    // Lunatic and Torment are the observed caps; the rest follow the same clear-to-total ratio.
    private static long CeilingFor(List<RaidStageExcelT> stages, Difficulty difficulty)
    {
        if (difficulty == Difficulty.Lunatic)
            return 53_500_000;

        if (difficulty == Difficulty.Torment)
            return 39_700_000;

        var rows = stages.Where(x => x.Difficulty == difficulty).ToList();
        if (rows.Count == 0)
            return 0;

        return (long)(rows.Max(x => x.DefaultClearScore + x.HPPercentScore) * 1.2);
    }

    private static double Fraction(int index, int span) => span <= 1 ? 0d : (double)index / (span - 1);

    // The squad of the player's best clear, rebuilt as a leaderboard team. Slots keep the order they were
    // recorded in so the row matches the formation that was actually used.
    private static RaidTeamSettingDB? OwnTeam(AccountDBServer account)
    {
        var members = account.ContentInfo?.RaidDataInfo?.BestTeam;
        if (members == null || members.Count == 0)
            return null;

        var strikers = members.Where(x => !x.IsSpecial).OrderBy(x => x.SlotIndex).ToList();
        var specials = members.Where(x => x.IsSpecial).OrderBy(x => x.SlotIndex).ToList();

        if (strikers.Count == 0)
            return null;

        return new RaidTeamSettingDB
        {
            AccountId = account.ServerId,
            TryNumber = 1,
            EchelonType = EchelonType.Raid,
            EchelonExtensionType = EchelonExtensionType.Base,
            MainCharacterDBs = strikers.Select((x, slot) => OwnCharacter(x, slot, account.ServerId)).ToList(),
            SupportCharacterDBs = specials.Select((x, slot) => OwnCharacter(x, slot, account.ServerId)).ToList(),
            SkillCardMulliganCharacterIds = [],
            TSSInteractionUniqueId = 0,
            LeaderCharacterUniqueId = strikers[0].UniqueId,
        };
    }

    private static RaidCharacterDB OwnCharacter(BestTeamMember member, int slot, long accountId) => new()
    {
        ServerId = accountId * 10 + (member.IsSpecial ? 4 : 0) + slot,
        UniqueId = member.UniqueId,
        StarGrade = Math.Clamp(member.StarGrade, 1, 5),
        Level = Math.Clamp(member.Level, 1, 90),
        SlotIndex = slot,
        AccountId = accountId,
        IsAssist = false,
        HasWeapon = true,
        WeaponStarGrade = Math.Clamp(member.StarGrade - 1, 1, 4),
        CostumeId = 0,
        CombatStyleIndex = 0,
    };

    public RaidTeamSettingDB? BuildTeam(
        long seasonId,
        long accountId,
        IReadOnlyList<MockSquad> squads,
        (long Start, long End, int Tier) bracket,
        long rank,
        bool allowRealAccount = false)
    {
        var strength = StrengthForBracket(bracket, rank);
        if (accountId < 9_000_000 && !allowRealAccount)
            return null;

        // Seeded by season and account, so the same row always previews the same squad.
        var random = new Random(unchecked((int)(seasonId * 31 + accountId)));

        var squad = squads.Count > 0 ? squads[random.Next(squads.Count)] : null;

        var strikers = squad != null ? squad.Strikers.ToList() : FallbackStrikers();
        var specials = squad != null ? squad.Specials.ToList() : FallbackSupports();

        if (strikers.Count == 0)
            return null;

        // Pick a squad that fits the row's strength: top rows field composed Lunatic teams, lower
        // brackets clear with simpler comps instead of a full UE60 mirror of the viewer's preset.
        if (squad == null && squads.Count > 1)
        {
            var ordered = squads
                .Select((q, i) => new { q, w = SquadWeight(q) })
                .OrderBy(x => x.w).ThenBy(x => x.q.Strikers[0]).ToList();

            var idx = (int)Math.Clamp(Math.Round((1.0 - strength) * (ordered.Count - 1)), 0, ordered.Count - 1);
            idx = Math.Clamp(idx + random.Next(-2, 3), 0, ordered.Count - 1);

            squad = ordered[idx].q;
            strikers = squad.Strikers.ToList();
            specials = squad.Specials.ToList();
        }

        // Shuffle only the order: which students are on the team stays the preset, position does not.
        Shuffle(random, strikers);
        Shuffle(random, specials);

        if (squad == null && strength < 0.75)
        {
            // Generic placeholder rows shed late-team members on lower brackets.
            var strikerKeep = strength < 0.55 ? 4 : 4;
            var specialKeep = strength < 0.55 ? 1 : 2;
            strikers = strikers.Take(strikerKeep).ToList();
            specials = specials.Take(specialKeep).ToList();
        }

        return new RaidTeamSettingDB
        {
            AccountId = accountId,
            TryNumber = 1,
            EchelonType = EchelonType.Raid,
            EchelonExtensionType = EchelonExtensionType.Base,
            MainCharacterDBs = strikers.Take(4)
                .Select((id, slot) => SpecCharacter(id, slot, accountId, SpecFor(strength, random, slot))).ToList(),
            SupportCharacterDBs = specials.Take(2)
                .Select((id, slot) => SpecCharacter(id, slot, accountId, SpecFor(strength, random, 4 + slot), serverSlot: 4 + slot)).ToList(),
            SkillCardMulliganCharacterIds = [],
            TSSInteractionUniqueId = 0,
            LeaderCharacterUniqueId = strikers[0],
        };
    }

    // Used only when the account has no saved presets, so the board never falls back to empty slots.
    private List<long> FallbackStrikers()
    {
        var pool = AllPlayable();
        if (pool.Count == 0)
            return [];

        return pool.Where(id => id is >= 10000 and < 20000).Take(4).ToList();
    }

    private List<long> FallbackSupports()
    {
        var pool = AllPlayable();
        return pool.Where(id => id is >= 20000 and < 30000).Take(2).ToList();
    }

    private static void Shuffle(Random random, List<long> items)
    {
        for (var i = items.Count - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
    }

    private static int SquadWeight(MockSquad squad) =>
        squad.Strikers.Count + squad.Specials.Count * 2;

    // Deterministic per ladder position: the same rank always yields the same handle, and rank keeps two
    // rows from colliding even when the syllable draw repeats.
    private static string HandleFor(long rank)
    {
        var random = new Random(unchecked((int)(rank * 2654435761L) ^ 0x5f3759df));
        var first = SyllablesA[random.Next(SyllablesA.Length)];
        var second = SyllablesB[random.Next(SyllablesB.Length)];
        var name = char.ToUpperInvariant(first[0]) + first[1..] + second;

        if (random.Next(3) == 0)
            name += random.Next(10, 999).ToString();
        else if (random.Next(4) == 0)
            name += "_" + random.Next(1, 99);

        if (random.Next(5) == 0)
            name = name.ToLowerInvariant();

        return name;
    }

    private static string Handle(Random random, HashSet<string> used)
    {
        for (var attempt = 0; attempt < 64; attempt++)
        {
            var first = SyllablesA[random.Next(SyllablesA.Length)];
            var second = SyllablesB[random.Next(SyllablesB.Length)];
            var name = char.ToUpperInvariant(first[0]) + first[1..] + second;

            // Roughly a third get a numeric tail and some are lowercase, so the list is not uniform.
            if (random.Next(3) == 0)
                name += random.Next(10, 999).ToString();
            else if (random.Next(4) == 0)
                name += "_" + random.Next(1, 99);

            if (random.Next(5) == 0)
                name = name.ToLowerInvariant();

            if (used.Add(name))
                return name;
        }

        var fallback = "sensei" + used.Count;
        used.Add(fallback);
        return fallback;
    }

    private List<long> AllPlayable()
    {
        if (_allPlayable != null)
            return _allPlayable;

        try
        {
            _allPlayable = _excel.GetTable<CharacterExcelT>()
                .Where(x => x.IsPlayable && x.IsPlayableCharacter && !x.IsNPC && !x.IsDummy)
                .Select(x => x.Id)
                .Distinct()
                .ToList();
        }
        catch
        {
            _allPlayable = [];
        }

        return _allPlayable;
    }
}