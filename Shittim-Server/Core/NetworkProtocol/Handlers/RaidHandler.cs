using AutoMapper;
using Microsoft.EntityFrameworkCore;
using BlueArchiveAPI.Services;
using Schale.Data;
using Schale.Data.GameModel;
using Schale.Data.ModelMapping;
using Schale.MX.NetworkProtocol;
using Schale.MX.GameLogic.Parcel;
using Schale.MX.GameLogic.DBModel;
using Schale.MX.Logic.Battles.Summary;
using Schale.MX.Core.Math;
using Schale.FlatData;
using Shittim_Server.Core;
using Shittim_Server.Services;
using Shittim.Services;
using Shittim_Server.GameClient;

namespace Shittim_Server.Core.NetworkProtocol.Handlers;

public class RaidHandler : ProtocolHandlerBase
{
    private readonly ISessionKeyService _sessionService;
    private readonly ExcelTableService _excelService;
    private readonly IMapper _mapper;
    private readonly RaidManager _raidManager;
    private readonly ParcelHandler _parcelHandler;
    private readonly MockRaidRankingService _mockRanking;
    private readonly LeaderboardPresetsService _presets;

    public RaidHandler(
        IProtocolHandlerRegistry registry,
        ISessionKeyService sessionService,
        ExcelTableService excelService,
        IMapper mapper,
        RaidManager raidManager,
        ParcelHandler parcelHandler,
        MockRaidRankingService mockRanking,
        LeaderboardPresetsService presets) : base(registry)
    {
        _sessionService = sessionService;
        _excelService = excelService;
        _mapper = mapper;
        _raidManager = raidManager;
        _parcelHandler = parcelHandler;
        _mockRanking = mockRanking;
        _presets = presets;
    }

    [ProtocolHandler(Protocol.Raid_Login)]
    public async Task<RaidLoginResponse> Login(
        SchaleDataContext db,
        RaidLoginRequest request,
        RaidLoginResponse response)
    {
        var account = await _sessionService.GetAuthenticatedUser(db, request.SessionKey);

        return response;
    }

    [ProtocolHandler(Protocol.Raid_Lobby)]
    public async Task<RaidLobbyResponse> Lobby(
        SchaleDataContext db,
        RaidLobbyRequest request,
        RaidLobbyResponse response)
    {
        var account = await _sessionService.GetAuthenticatedUser(db, request.SessionKey);

        var raidLobby = await _raidManager.GetUpdatedLobby(db, account);

        // The lobby panel reads these directly. Both come from the same ladder the ranking rows use, so a 39M
        // score cannot be shown as first place nor as the Platinum medal.
        var rank = _mockRanking.RankOf(account.ContentInfo.RaidDataInfo.BestRankingPoint);

        if (rank > 0)
        {
            raidLobby.Ranking = rank;
            raidLobby.Tier = MockRaidRankingService.BracketOf(rank).Tier;
        }

        response.SeasonType = RaidSeasonType.Open;
        response.RaidLobbyInfoDB = raidLobby.ToMap(_mapper);
        response.ServerTimeTicks = _raidManager.GetRaidTimeTicks(account).Ticks;

        return response;
    }

    [ProtocolHandler(Protocol.Raid_List)]
    public async Task<RaidListResponse> List(
        SchaleDataContext db,
        RaidListRequest request,
        RaidListResponse response)
    {
        var account = await _sessionService.GetAuthenticatedUser(db, request.SessionKey);

        // no other players to browse: the only room that can exist is this account's own run, handed back so the client can rejoin it
        var playing = _raidManager.GetRaidData(db, account);

        response.CreateRaidDBs = [];
        response.EnterRaidDBs = playing != null ? [playing.ToMap(_mapper)] : [];
        response.ListRaidDBs = [];
        response.ServerTimeTicks = _raidManager.GetRaidTimeTicks(account).Ticks;

        return response;
    }

    [ProtocolHandler(Protocol.Raid_Search)]
    public async Task<RaidSearchResponse> Search(
        SchaleDataContext db,
        RaidSearchRequest request,
        RaidSearchResponse response)
    {
        var account = await _sessionService.GetAuthenticatedUser(db, request.SessionKey);

        response.RaidDBs = [];
        response.ServerTimeTicks = _raidManager.GetRaidTimeTicks(account).Ticks;

        return response;
    }

    [ProtocolHandler(Protocol.Raid_CompleteList)]
    public async Task<RaidCompleteListResponse> CompleteList(
        SchaleDataContext db,
        RaidCompleteListRequest request,
        RaidCompleteListResponse response)
    {
        var account = await _sessionService.GetAuthenticatedUser(db, request.SessionKey);

        var seasonId = account.ContentInfo.RaidDataInfo.SeasonId;
        var raidLobby = _raidManager.GetLobbyData(db, account);

        response.RaidDBs = db.GetAccountRaids(account.ServerId)
            .Where(x => x.ContentType == ContentType.Raid && x.SeasonId == seasonId && x.RaidState != RaidStatus.Playing && !x.IsPractice)
            .ToMapList(_mapper);
        response.StackedDamage = db.GetAccountRaidBattles(account.ServerId)
            .Where(x => x.ContentType == ContentType.Raid && x.IsClear).AsEnumerable()
            .Sum(b => b.RaidMembers.Sum(m => m.DamageCollection.Sum(d => d.GivenDamage)));
        response.ReceiveRewardId = raidLobby.ReceiveRewardIds;
        response.CurSeasonUniqueId = seasonId;
        response.ServerTimeTicks = _raidManager.GetRaidTimeTicks(account).Ticks;

        return response;
    }

    [ProtocolHandler(Protocol.Raid_Detail)]
    public async Task<RaidDetailResponse> Detail(
        SchaleDataContext db,
        RaidDetailRequest request,
        RaidDetailResponse response)
    {
        var account = await _sessionService.GetAuthenticatedUser(db, request.SessionKey);

        var raid = db.GetAccountRaids(account.ServerId).FirstOrDefault(x => x.ServerId == request.RaidServerId);
        // battle rows are reused per run, so the same stage played twice leaves two of them - the newest belongs to this raid
        var battle = db.GetAccountRaidBattles(account.ServerId)
            .Where(x => x.RaidUniqueId == raid.UniqueId)
            .OrderByDescending(x => x.ServerId).FirstOrDefault();

        response.RaidDetailDB = new RaidDetailDB
        {
            RaidUniqueId = raid.UniqueId,
            EndDate = raid.End,
            DamageTable = battle.RaidMembers.Select(m => new RaidPlayerInfoDB
            {
                RaidServerId = raid.ServerId,
                AccountId = m.AccountId,
                Nickname = m.AccountName,
                CharacterId = m.CharacterId,
                JoinDate = raid.Begin,
                DamageAmount = m.DamageCollection.Sum(d => d.GivenDamage)
            }).ToList()
        };
        response.ParticipateCharacterServerIds = raid.ParticipateCharacterServerIds.TryGetValue(account.ServerId, out var ids) ? ids : [];
        response.ServerTimeTicks = _raidManager.GetRaidTimeTicks(account).Ticks;

        return response;
    }

    [ProtocolHandler(Protocol.Raid_CreateBattle)]
    public async Task<RaidCreateBattleResponse> CreateBattle(
        SchaleDataContext db,
        RaidCreateBattleRequest request,
        RaidCreateBattleResponse response)
    {
        var account = await _sessionService.GetAuthenticatedUser(db, request.SessionKey);

        account.ContentInfo.RaidDataInfo.CurrentRaidUniqueId = request.RaidUniqueId;
        account.ContentInfo.RaidDataInfo.CurrentDifficulty = request.Difficulty;

        db.Entry(account).Property(x => x.ContentInfo).IsModified = true;
        await db.SaveChangesAsync();

        var raidDB = await _raidManager.CreateOrUpdateRaid(db, account, request.IsPractice, request.RaidUniqueId);
        var raidBattleDB = await _raidManager.CreateOrUpdateBattle(db, account, request.RaidUniqueId);

        response.RaidDB = raidDB.ToMap(_mapper);
        response.RaidBattleDB = raidBattleDB.ToMap(_mapper);
        response.AccountCurrencyDB = db.GetAccountCurrencies(account.ServerId).FirstMapTo(_mapper);
        response.ServerTimeTicks = _raidManager.GetRaidTimeTicks(account).Ticks;
        response.MissionProgressDBs = [];

        if (request.AssistUseInfo != null)
        {
            response.AssistCharacterDB = RaidService.FinishingAssistCharacterInfo(
                SchaleService.GetAssistCharacter(request.AssistUseInfo.EchelonType)
                    .FirstOrDefault(x => x.AssistCharacterServerId == request.AssistUseInfo.CharacterDBId),
                request.AssistUseInfo);
        }

        return response;
    }

    [ProtocolHandler(Protocol.Raid_EnterBattle)]
    public async Task<RaidEnterBattleResponse> EnterBattle(
        SchaleDataContext db,
        RaidEnterBattleRequest request,
        RaidEnterBattleResponse response)
    {
        var account = await _sessionService.GetAuthenticatedUser(db, request.SessionKey);

        var raidDB = _raidManager.GetRaidData(db, account);
        var raidBattleDB = _raidManager.GetRaidBattleData(db, account);

        response.RaidDB = raidDB.ToMap(_mapper);
        response.RaidBattleDB = raidBattleDB.ToMap(_mapper);
        response.AccountCurrencyDB = db.GetAccountCurrencies(account.ServerId).FirstMapTo(_mapper);
        response.ServerTimeTicks = _raidManager.GetRaidTimeTicks(account).Ticks;
        response.MissionProgressDBs = [];

        if (request.AssistUseInfo != null)
        {
            response.AssistCharacterDB = RaidService.FinishingAssistCharacterInfo(
                SchaleService.GetAssistCharacter(request.AssistUseInfo.EchelonType)
                    .FirstOrDefault(x => x.AssistCharacterServerId == request.AssistUseInfo.CharacterDBId),
                request.AssistUseInfo);
        }

        return response;
    }

    [ProtocolHandler(Protocol.Raid_BattleUpdate)]
    public async Task<RaidBattleUpdateResponse> BattleUpdate(
        SchaleDataContext db,
        RaidBattleUpdateRequest request,
        RaidBattleUpdateResponse response)
    {
        var account = await _sessionService.GetAuthenticatedUser(db, request.SessionKey);

        var raidBattle = _raidManager.GetRaidBattleData(db, account);
        var targetStage = _excelService.GetTable<RaidStageExcelT>().FirstOrDefault(x => x.Id == raidBattle.RaidUniqueId);
        var targetBoss = _excelService.GetTable<CharacterStatExcelT>().FirstOrDefault(x => x.CharacterId == targetStage.BossCharacterId[request.RaidBossIndex]);

        // mid-battle checkpoint so a crashed client re-enters at the boss's live HP; the member damage table settles from the battle summary at EndBattle
        raidBattle.RaidBossIndex = request.RaidBossIndex;
        raidBattle.CurrentBossHP = targetBoss.MaxHP100 - request.CumulativeDamage;
        raidBattle.CurrentBossGroggy = request.CumulativeGroggyPoint;
        db.RaidBattles.Update(raidBattle);
        await db.SaveChangesAsync();

        response.RaidBattleDB = raidBattle.ToMap(_mapper);

        return response;
    }

    [ProtocolHandler(Protocol.Raid_EndBattle)]
    public async Task<RaidEndBattleResponse> EndBattle(
        SchaleDataContext db,
        RaidEndBattleRequest request,
        RaidEndBattleResponse response)
    {
        var account = await _sessionService.GetAuthenticatedUser(db, request.SessionKey);

        bool isCleared = await _raidManager.SaveBattle(db, account, request.Summary, request.IsPractice);

        if (!isCleared)
        {
            account.ContentInfo.RaidDataInfo.TimeBonus += request.Summary.EndFrame;
            db.Entry(account).Property(x => x.ContentInfo).IsModified = true;
            await db.SaveChangesAsync();
            response.ServerTimeTicks = _raidManager.GetRaidTimeTicks(account).Ticks;
            return response;
        }

        var targetStage = _raidManager.GetRaidStage(account);

        var totalTime = (request.Summary.EndFrame + account.ContentInfo.RaidDataInfo.TimeBonus) / 30f;
        var timeScore = MathService.CalculateTimeScore(totalTime, targetStage.PerSecondMinusScore);
        var hpPercentScorePoint = targetStage.HPPercentScore;
        var defaultClearPoint = targetStage.DefaultClearScore;

        var rankingPoint = timeScore + hpPercentScorePoint + defaultClearPoint;

        if (!request.IsPractice)
        {
            var previousBest = account.ContentInfo.RaidDataInfo.BestRankingPoint;

            account.ContentInfo.RaidDataInfo.BestRankingPoint = rankingPoint > previousBest ? rankingPoint : previousBest;
            account.ContentInfo.RaidDataInfo.TotalRankingPoint += rankingPoint;

            // The squad that produced the best score is kept so the leaderboard row shows the real team.
            if (rankingPoint >= previousBest)
                account.ContentInfo.RaidDataInfo.BestTeam = CaptureTeam(request.Summary);
        }
        account.ContentInfo.RaidDataInfo.TimeBonus = 0;
        db.Entry(account).Property(x => x.ContentInfo).IsModified = true;
        await db.SaveChangesAsync();

        await _raidManager.EndBossBattle(db, account, RaidStatus.Clear, rankingPoint);

        response.RankingPoint = rankingPoint;
        response.BestRankingPoint = account.ContentInfo.RaidDataInfo.BestRankingPoint;
        response.ClearTimePoint = timeScore;
        response.HPPercentScorePoint = hpPercentScorePoint;
        response.DefaultClearPoint = defaultClearPoint;
        response.ServerTimeTicks = _raidManager.GetRaidTimeTicks(account).Ticks;

        return response;
    }

    [ProtocolHandler(Protocol.Raid_GiveUp)]
    public async Task<RaidGiveUpResponse> GiveUp(
        SchaleDataContext db,
        RaidGiveUpRequest request,
        RaidGiveUpResponse response)
    {
        var account = await _sessionService.GetAuthenticatedUser(db, request.SessionKey);

        var giveUpRaid = new RaidGiveUpDB
        {
            Ranking = await MockRank(db, account, account.ContentInfo.RaidDataInfo.SeasonId),
            RankingPoint = account.ContentInfo.RaidDataInfo.TotalRankingPoint,
            BestRankingPoint = account.ContentInfo.RaidDataInfo.BestRankingPoint
        };

        await _raidManager.EndBossBattle(db, account, RaidStatus.Close);

        response.RaidGiveUpDB = giveUpRaid;
        response.ServerTimeTicks = _raidManager.GetRaidTimeTicks(account).Ticks;

        return response;
    }

    [ProtocolHandler(Protocol.Raid_Reward)]
    public async Task<RaidRewardResponse> Reward(
        SchaleDataContext db,
        RaidRewardRequest request,
        RaidRewardResponse response)
    {
        var account = await _sessionService.GetAuthenticatedUser(db, request.SessionKey);

        var raid = db.GetAccountRaids(account.ServerId).FirstOrDefault(x => x.ServerId == request.RaidServerId);

        response.RankingPoint = account.ContentInfo.RaidDataInfo.TotalRankingPoint;
        response.BestRankingPoint = account.ContentInfo.RaidDataInfo.BestRankingPoint;
        response.ServerTimeTicks = _raidManager.GetRaidTimeTicks(account).Ticks;

        // Clear is cleared-but-unclaimed; paying settles the row to Close so RewardAll cannot pay it again
        if (request.IsPractice || raid.RaidState != RaidStatus.Clear)
            return response;

        var targetStage = _excelService.GetTable<RaidStageExcelT>().FirstOrDefault(x => x.Id == raid.UniqueId);
        var drops = RollStageRewards(targetStage.RaidRewardGroupId);
        var resolver = await _parcelHandler.BuildParcel(db, account, drops);

        raid.RaidState = RaidStatus.Close;
        db.Raids.Update(raid);
        await db.SaveChangesAsync();

        response.ParcelResultDB = resolver.ParcelResult;

        return response;
    }

    [ProtocolHandler(Protocol.Raid_RewardAll)]
    public async Task<RaidRewardAllResponse> RewardAll(
        SchaleDataContext db,
        RaidRewardAllRequest request,
        RaidRewardAllResponse response)
    {
        var account = await _sessionService.GetAuthenticatedUser(db, request.SessionKey);

        var raidStageExcels = _excelService.GetTable<RaidStageExcelT>();
        var unclaimed = db.GetAccountRaids(account.ServerId)
            .Where(x => x.ContentType == ContentType.Raid && x.RaidState == RaidStatus.Clear && !x.IsPractice)
            .ToList();

        var drops = new List<ParcelResult>();
        foreach (var raid in unclaimed)
        {
            var targetStage = raidStageExcels.FirstOrDefault(x => x.Id == raid.UniqueId);
            drops.AddRange(RollStageRewards(targetStage.RaidRewardGroupId));
            raid.RaidState = RaidStatus.Close;
        }

        var resolver = await _parcelHandler.BuildParcel(db, account, drops);
        await db.SaveChangesAsync();

        response.ParcelResultDB = resolver.ParcelResult;
        response.ServerTimeTicks = _raidManager.GetRaidTimeTicks(account).Ticks;

        return response;
    }

    [ProtocolHandler(Protocol.Raid_Share)]
    public async Task<RaidShareResponse> Share(
        SchaleDataContext db,
        RaidShareRequest request,
        RaidShareResponse response)
    {
        var account = await _sessionService.GetAuthenticatedUser(db, request.SessionKey);

        // there is no clan feed to post into, so sharing is just an echo
        response.RaidDB = db.GetAccountRaids(account.ServerId).FirstOrDefaultMapTo(x => x.ServerId == request.RaidServerId, _mapper);

        return response;
    }


    // Board key for the season's boss, so Binah rows show Binah squads. Falls back to the shared board
    // when the season table or the file has no matching entry.
    // Ladder position on the placeholder board: one more than the number of rows with a higher score.

    // Reads the squad that fought the battle out of the summary the client submits.
    private static List<BestTeamMember> CaptureTeam(BattleSummary summary)
    {
        var members = new List<BestTeamMember>();
        var group = summary.Group01Summary;
        if (group == null)
            return members;

        var slot = 0;

        foreach (var hero in group.Heroes ?? [])
        {
            members.Add(new BestTeamMember
            {
                UniqueId = hero.CharacterId,
                Level = hero.Level,
                StarGrade = hero.Grade,
                SlotIndex = slot++,
                IsSpecial = false,
            });
        }

        foreach (var supporter in group.Supporters ?? [])
        {
            members.Add(new BestTeamMember
            {
                UniqueId = supporter.CharacterId,
                Level = supporter.Level,
                StarGrade = supporter.Grade,
                SlotIndex = slot++,
                IsSpecial = true,
            });
        }

        return members;
    }
    private async Task<long> MockRank(SchaleDataContext db, AccountDBServer account, long seasonId)
    {
        var squads = await MockSquads(db, account.ServerId, seasonId);
        var board = _mockRanking.Build(account, seasonId, account.RepresentCharacterServerId, squads);

        return board.FirstOrDefault(x => x.AccountId == account.ServerId)?.Rank ?? 1;
    }

    private string MockBoardKey(long seasonId)
    {
        try
        {
            var season = _excelService.GetTable<RaidSeasonManageExcel>()
                .FirstOrDefault(x => x.SeasonId == seasonId);

            if (season.OpenRaidBossGroupLength > 0)
            {
                var raw = season.OpenRaidBossGroup(0);
                if (!string.IsNullOrWhiteSpace(raw))
                    return new string(raw.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
            }
        }
        catch
        {
            // Table unavailable: the shared board still renders.
        }

        return "totalAssault";
    }

    // Placeholder rows borrow the account's own echelon presets, so the board shows squads that actually
    // exist instead of random students. Duplicate presets collapse to one entry.
    private async Task<List<MockSquad>> MockSquads(SchaleDataContext db, long accountServerId, long seasonId)
    {
        // The file is the primary source: the game only holds five presets per group, which is not enough
        // for every difficulty and raid type. Account presets are the fallback, then generated squads.
        var fromFile = _presets.Squads(MockBoardKey(seasonId));
        if (fromFile.Count > 0)
            return fromFile.ToList();

        var presets = await db.EchelonPresets
            .Where(x => x.AccountServerId == accountServerId)
            .ToListAsync();

        return presets
            .Where(x => x.StrikerUniqueIds is { Length: >= 4 } && x.SpecialUniqueIds is { Length: >= 2 })
            .Where(x => x.StrikerUniqueIds.Take(4).All(id => id > 0) && x.SpecialUniqueIds.Take(2).All(id => id > 0))
            .Select(x => new MockSquad(x.StrikerUniqueIds.Take(4).ToList(), x.SpecialUniqueIds.Take(2).ToList()))
            .GroupBy(x => string.Join(',', x.Strikers) + "|" + string.Join(',', x.Specials))
            .Select(g => g.First())
            .ToList();
    }

    [ProtocolHandler(Protocol.Raid_OpponentList)]
    public async Task<RaidOpponentListResponse> OpponentList(
        SchaleDataContext db,
        RaidOpponentListRequest request,
        RaidOpponentListResponse response)
    {
        var account = await _sessionService.GetAuthenticatedUser(db, request.SessionKey);

        // The client asks for this list when the ranking tab opens. Placeholder players come from the
        // mock generator, and the real account is placed by its own BestRankingPoint.
        var represent = await db.Characters.FirstOrDefaultAsync(x =>
            x.AccountServerId == account.ServerId && x.ServerId == account.RepresentCharacterServerId);
        var seasonId = account.ContentInfo?.RaidDataInfo?.SeasonId ?? 0;

        var squads = await MockSquads(db, account.ServerId, seasonId);

        // Each tab opens at a different starting rank and scrolling asks for the next window through the
        // same field. The reward group decides which medal tier each row carries.
        var startRank = request.Rank ?? 1L;

        response.OpponentUserDBs = _mockRanking.Build(
            account, seasonId, represent?.UniqueId ?? 0, squads, startRank, 100);
        response.ServerTimeTicks = _raidManager.GetRaidTimeTicks(account).Ticks;

        return response;
    }

    [ProtocolHandler(Protocol.Raid_RankingIndex)]
    public async Task<RaidRankingIndexResponse> RankingIndex(
        SchaleDataContext db,
        RaidRankingIndexRequest request,
        RaidRankingIndexResponse response)
    {
        var account = await _sessionService.GetAuthenticatedUser(db, request.SessionKey);

        // Bracket definitions back the four tabs (Platinum, Gold, Silver, Bronze). An empty list leaves the
        // client with no tier boundaries, so every tab falls back to the same view.
        response.RankBrackets = _mockRanking.Brackets();
        response.ServerTimeTicks = _raidManager.GetRaidTimeTicks(account).Ticks;

        return response;
    }

    [ProtocolHandler(Protocol.Raid_RankingReward)]
    public async Task<RaidRankingRewardResponse> RankingReward(
        SchaleDataContext db,
        RaidRankingRewardRequest request,
        RaidRankingRewardResponse response)
    {
        var account = await _sessionService.GetAuthenticatedUser(db, request.SessionKey);

        var raidLobby = await _raidManager.GetUpdatedLobby(db, account);
        var targetSeason = _excelService.GetTable<RaidSeasonManageExcelT>().FirstOrDefault(x => x.SeasonId == account.ContentInfo.RaidDataInfo.SeasonId);
        var bracket = _excelService.GetTable<RaidRankingRewardExcelT>()
            .FirstOrDefault(x => x.RankingRewardGroupId == targetSeason.RankingRewardGroupId && x.RankStart <= raidLobby.Ranking && raidLobby.Ranking <= x.RankEnd);

        response.ReceivedRankingRewardId = raidLobby.ReceivedRankingRewardId;
        if (bracket == null || raidLobby.ReceivedRankingRewardId == bracket.Id)
            return response;

        var parcels = bracket.RewardParcelType
            .Select((type, i) => new ParcelResult(type, bracket.RewardParcelUniqueId[i], bracket.RewardParcelAmount[i]))
            .ToList();
        var resolver = await _parcelHandler.BuildParcel(db, account, parcels);

        raidLobby.ReceivedRankingRewardId = bracket.Id;
        db.SingleRaidLobbyInfos.Update(raidLobby);
        await db.SaveChangesAsync();

        response.ReceivedRankingRewardId = bracket.Id;
        response.ParcelResultDB = resolver.ParcelResult;

        return response;
    }

    [ProtocolHandler(Protocol.Raid_SeasonReward)]
    public async Task<RaidSeasonRewardResponse> SeasonReward(
        SchaleDataContext db,
        RaidSeasonRewardRequest request,
        RaidSeasonRewardResponse response)
    {
        var account = await _sessionService.GetAuthenticatedUser(db, request.SessionKey);

        var raidLobby = await _raidManager.GetUpdatedLobby(db, account);
        var targetSeason = _excelService.GetTable<RaidSeasonManageExcelT>().FirstOrDefault(x => x.SeasonId == account.ContentInfo.RaidDataInfo.SeasonId);

        // the lobby is seeded with every SeasonRewardId already marked received, so this normally settles to an empty payout; the difference set keeps the claim honest if that seeding ever changes
        var unclaimed = targetSeason.SeasonRewardId.Except(raidLobby.ReceiveRewardIds).ToList();
        var seasonRewardExcels = _excelService.GetTable<RaidStageSeasonRewardExcelT>();

        var parcels = new List<ParcelResult>();
        foreach (var id in unclaimed)
        {
            var row = seasonRewardExcels.FirstOrDefault(x => x.SeasonRewardId == id);
            if (row == null) continue;
            parcels.AddRange(row.SeasonRewardParcelType.Select((type, i) => new ParcelResult(type, row.SeasonRewardParcelUniqueId[i], row.SeasonRewardAmount[i])));
        }

        var resolver = await _parcelHandler.BuildParcel(db, account, parcels);
        raidLobby.ReceiveRewardIds.AddRange(unclaimed);
        db.SingleRaidLobbyInfos.Update(raidLobby);
        await db.SaveChangesAsync();

        response.ReceiveRewardIds = raidLobby.ReceiveRewardIds;
        response.ParcelResultDB = resolver.ParcelResult;

        return response;
    }

    [ProtocolHandler(Protocol.Raid_Sweep)]
    public async Task<RaidSweepResponse> Sweep(
        SchaleDataContext db,
        RaidSweepRequest request,
        RaidSweepResponse response)
    {
        var account = await _sessionService.GetAuthenticatedUser(db, request.SessionKey);

        var targetStage = _excelService.GetTable<RaidStageExcelT>().FirstOrDefault(x => x.Id == request.UniqueId);

        var rewards = new List<List<ParcelInfo>>();
        var allDrops = new List<ParcelResult>();
        for (var i = 0; i < request.SweepCount; i++)
        {
            var run = RollStageRewards(targetStage.RaidRewardGroupId);
            rewards.Add(run.Select(r => new ParcelInfo
            {
                Key = new ParcelKeyPair { Type = r.Type, Id = r.Id },
                Amount = r.Amount,
                Multiplier = BasisPoint.One,
                Probability = BasisPoint.One
            }).ToList());
            allDrops.AddRange(run);
        }

        var resolver = await _parcelHandler.BuildParcel(db, account, allDrops);
        await db.SaveChangesAsync();

        response.TotalSeasonPoint = account.ContentInfo.RaidDataInfo.TotalRankingPoint;
        response.Rewards = rewards;
        response.ParcelResultDB = resolver.ParcelResult;
        response.ServerTimeTicks = _raidManager.GetRaidTimeTicks(account).Ticks;

        return response;
    }

    [ProtocolHandler(Protocol.Raid_GetBestTeam)]
    public async Task<RaidGetBestTeamResponse> GetBestTeam(
        SchaleDataContext db,
        RaidGetBestTeamRequest request,
        RaidGetBestTeamResponse response)
    {
        var account = await _sessionService.GetAuthenticatedUser(db, request.SessionKey);
        var seasonId = account.ContentInfo?.RaidDataInfo?.SeasonId ?? 0;

        // The Details button on a ranking row asks for that row's squad. Placeholder ids encode the rank,
        // so the same bracket depth and equipment profile are reproduced here.
        var squads = await MockSquads(db, account.ServerId, seasonId);
        var rank = Math.Max(1, request.SearchAccountId - 9_000_000);
        var team = _mockRanking.BuildTeam(
            seasonId, request.SearchAccountId, squads, MockRaidRankingService.BracketOf(rank), rank);
        response.RaidTeamSettingDBs = team == null ? [] : [team];

        return response;
    }

    [ProtocolHandler(Protocol.Raid_Revive)]
    public async Task<RaidReviveResponse> Revive(
        SchaleDataContext db,
        RaidReviveRequest request,
        RaidReviveResponse response)
    {
        await _sessionService.GetAuthenticatedUser(db, request.SessionKey);
        return response;
    }

    [ProtocolHandler(Protocol.Raid_SeasonInfo)]
    public async Task<RaidSeasonInfoResponse> SeasonInfo(
        SchaleDataContext db,
        RaidSeasonInfoRequest request,
        RaidSeasonInfoResponse response)
    {
        await _sessionService.GetAuthenticatedUser(db, request.SessionKey);
        return response;
    }

    private List<ParcelResult> RollStageRewards(long rewardGroupId) =>
        _excelService.GetTable<RaidStageRewardExcelT>()
            .Where(x => x.GroupId == rewardGroupId && MathService.GenerateProbability(x.ClearStageRewardProb))
            .Select(x => new ParcelResult(x.ClearStageRewardParcelType, x.ClearStageRewardParcelUniqueID, x.ClearStageRewardAmount))
            .ToList();
}
