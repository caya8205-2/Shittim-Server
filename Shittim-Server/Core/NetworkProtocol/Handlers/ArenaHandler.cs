using AutoMapper;
using Microsoft.EntityFrameworkCore;
using BlueArchiveAPI.Services;
using Schale.Data;
using Schale.Data.GameModel;
using Schale.Data.ModelMapping;
using Schale.MX.GameLogic.DBModel;
using Schale.MX.Logic.Battles.Summary;
using Schale.MX.NetworkProtocol;
using Schale.MX.GameLogic.Parcel;
using Schale.FlatData;
using Shittim_Server.Core;
using Shittim_Server.Services;

namespace Shittim_Server.Core.NetworkProtocol.Handlers;

public class ArenaHandler : ProtocolHandlerBase
{
    // Fallback accrual when the ArenaRewardExcel Time row is absent from the shipped data.
    private const long DefaultTimeRewardGoldPerHour = 10000;

    // Ladder position for an account that has never won a battle: mid-table, so the first opponents are
    // not the fully maxed top ten.
    private const long DefaultArenaRank = 30;
    private const int TimeRewardAccrualCapHours = 24;

    private readonly ISessionKeyService _sessionService;
    private readonly ExcelTableService _excelService;
    private readonly ParcelHandler _parcelHandler;
    private readonly IMapper _mapper;
    private readonly LeaderboardPresetsService _presets;

    public ArenaHandler(
        IProtocolHandlerRegistry registry,
        ISessionKeyService sessionService,
        ExcelTableService excelService,
        ParcelHandler parcelHandler,
        IMapper mapper,
        LeaderboardPresetsService presets) : base(registry)
    {
        _sessionService = sessionService;
        _excelService = excelService;
        _parcelHandler = parcelHandler;
        _mapper = mapper;
        _presets = presets;
    }

    private ArenaPlayerInfoDB BuildPlayerInfo(AccountDBServer account)
    {
        var info = account.ContentInfo.ArenaDataInfo;
        var now = account.GameSettings.ServerDateTime();

        return new ArenaPlayerInfoDB
        {
            CurrentSeasonId = info.SeasonId,
            PlayerGroupId = 1,
            CurrentRank = StoredRank(account),
            SeasonRecord = 1,
            AllTimeRecord = 1,
            CumulativeTimeReward = AccruedTimeReward(account),
            TimeRewardLastUpdateTime = info.TimeRewardLastUpdateTime ?? now,
            BattleEnterActiveTime = info.BattleEnterActiveTime ?? now,
            DailyRewardActiveTime = NextDailyRewardActiveTime(account)
        };
    }

    // The client sends BattleSummary with every hero ServerId set to 0, and the result screen looks those
    // ids up to draw the squad cards. Filling them from the battle teams is what turns the empty level-53
    // placeholders into the actual students.
    // Squad entries coming back from the client have ServerId 0; the result screen resolves its cards by
    // that id, so it is rebuilt from the account's own character rows by unique id.
    private static void RepairTeamServerIds(SchaleDataContext db, long accountServerId, ArenaTeamSettingDB team)
    {
        var owned = db.GetAccountCharacters(accountServerId).ToList();

        foreach (var group in new[] { team.MainCharacters, team.SupportCharacters })
        {
            if (group == null)
                continue;

            foreach (var character in group)
            {
                if (character.ServerId > 0)
                    continue;

                var match = owned.FirstOrDefault(c => c.UniqueId == character.UniqueId);
                if (match != null)
                    character.ServerId = match.ServerId;
            }
        }
    }

    private static void HydrateBattleSummary(BattleSummary summary, ArenaUserDB? attacker, ArenaUserDB? defender)
    {
        HydrateGroup(summary.Group01Summary, attacker);
        HydrateGroup(summary.Group02Summary, defender);
    }

    private static void HydrateGroup(GroupSummary? group, ArenaUserDB? user)
    {
        var team = user?.TeamSettingDB;
        if (group == null || team == null)
            return;

        var roster = new List<ArenaCharacterDB>();
        if (team.MainCharacters != null)
            roster.AddRange(team.MainCharacters);
        if (team.SupportCharacters != null)
            roster.AddRange(team.SupportCharacters);

        HydrateCollection(group.Heroes, roster, user!.AccountServerId);
        HydrateCollection(group.Supporters, roster, user.AccountServerId);
    }

    private static void HydrateCollection(HeroSummaryCollection? heroes, List<ArenaCharacterDB> roster, long accountServerId)
    {
        if (heroes == null)
            return;

        foreach (var hero in heroes)
        {
            hero.OwnerAccountId = accountServerId;

            var match = roster.FirstOrDefault(x => x.UniqueId == hero.CharacterId);
            if (match != null)
                hero.ServerId = match.ServerId;
        }
    }

    private static long StoredRank(AccountDBServer account)
    {
        var rank = account.ContentInfo.ArenaDataInfo.Rank;
        return rank > 0 ? rank : DefaultArenaRank;
    }

    private long AccruedTimeReward(AccountDBServer account)
    {
        var info = account.ContentInfo.ArenaDataInfo;
        var now = account.GameSettings.ServerDateTime();
        var since = info.TimeRewardLastUpdateTime ?? now;
        var hours = Math.Clamp((now - since).TotalHours, 0, TimeRewardAccrualCapHours);
        return (long)(hours * TimeRewardRatePerHour().amount);
    }

    private (ParcelType type, long id, long amount) TimeRewardRatePerHour()
    {
        var timeRow = _excelService.GetTable<ArenaRewardExcelT>()
            .FirstOrDefault(x => x.ArenaRewardType == ArenaRewardType.Time
                && x.RankStart <= 1 && 1 <= x.RankEnd
                && (x.RewardParcelAmount?.Count ?? 0) > 0);

        if (timeRow == null)
            return (ParcelType.Currency, (long)CurrencyTypes.Gold, DefaultTimeRewardGoldPerHour);

        return (timeRow.RewardParcelType[0], timeRow.RewardParcelUniqueId[0], timeRow.RewardParcelAmount[0]);
    }

    private DateTime NextDailyRewardActiveTime(AccountDBServer account)
    {
        var info = account.ContentInfo.ArenaDataInfo;
        var now = account.GameSettings.ServerDateTime();
        if (info.LastDailyRewardClaimTime is DateTime last && last.Date == now.Date)
            return now.Date.AddDays(1);
        return now;
    }

    [ProtocolHandler(Protocol.Arena_Login)]
    public async Task<ArenaLoginResponse> Login(
        SchaleDataContext db,
        ArenaLoginRequest request,
        ArenaLoginResponse response)
    {
        var account = await _sessionService.GetAuthenticatedUser(db, request.SessionKey);

        response.ArenaPlayerInfoDB = BuildPlayerInfo(account);

        return response;
    }

    [ProtocolHandler(Protocol.Arena_EnterLobby)]
    public async Task<ArenaEnterLobbyResponse> EnterLobby(
        SchaleDataContext db,
        ArenaEnterLobbyRequest request,
        ArenaEnterLobbyResponse response)
    {
        var account = await _sessionService.GetAuthenticatedUser(db, request.SessionKey);

        // Three rows directly above the player's rank, which is what the ladder offers in game.
        var window = Math.Max(1, StoredRank(account) - 3);
        var nonce = Random.Shared.Next(0, 900);

        response.ArenaPlayerInfoDB = BuildPlayerInfo(account);
        response.OpponentUserDBs = ArenaService.CreateOpponents(db, account, _mapper, _presets, (int)window, nonce);
        response.MapId = 1001;
        ArenaDump.Write("enter-lobby", response.OpponentUserDBs);

        return response;
    }

    [ProtocolHandler(Protocol.Arena_OpponentList)]
    public async Task<ArenaOpponentListResponse> OpponentList(
        SchaleDataContext db,
        ArenaOpponentListRequest request,
        ArenaOpponentListResponse response)
    {
        var account = await _sessionService.GetAuthenticatedUser(db, request.SessionKey);

        var window = Math.Max(1, StoredRank(account) - 3);
        var nonce = Random.Shared.Next(0, 900);

        response.PlayerRank = StoredRank(account);
        response.OpponentUserDBs = ArenaService.CreateOpponents(db, account, _mapper, _presets, (int)window, nonce);
        ArenaDump.Write("opponent-list", response.OpponentUserDBs);

        return response;
    }

    [ProtocolHandler(Protocol.Arena_SyncEchelonSettingTime)]
    public async Task<ArenaSyncEchelonSettingTimeResponse> SyncEchelonSettingTime(
        SchaleDataContext db,
        ArenaSyncEchelonSettingTimeRequest request,
        ArenaSyncEchelonSettingTimeResponse response)
    {
        var account = await _sessionService.GetAuthenticatedUser(db, request.SessionKey);

        response.EchelonSettingTime = account.GameSettings.ServerDateTime();

        return response;
    }

    [ProtocolHandler(Protocol.Arena_EnterBattlePart1)]
    public async Task<ArenaEnterBattlePart1Response> EnterBattlePart1(
        SchaleDataContext db,
        ArenaEnterBattlePart1Request request,
        ArenaEnterBattlePart1Response response)
    {
        var account = await _sessionService.GetAuthenticatedUser(db, request.SessionKey);

        response.ArenaBattleDB = new ArenaBattleDB
        {
            Season = 1,
            Group = 1,
            BattleStartTime = account.GameSettings.ServerDateTime(),
            Seed = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            AttackingUserDB = BuildAttackingUser(db, account),
            DefendingUserDB = await BuildDefendingUser(db, account, request.OpponentAccountServerId, request.OpponentRank)
        };

        ArenaDump.Write("battle-part1", response.ArenaBattleDB);

        return response;
    }

    // The attacker's own squad: the client resolves these characters from its own account data.
    private ArenaUserDB BuildAttackingUser(SchaleDataContext db, AccountDBServer account) => new()
    {
        AccountServerId = account.ServerId,
        NickName = account.Nickname ?? "Player",
        Rank = 1,
        Level = account.Level,
        RepresentCharacterUniqueId = account.RepresentCharacterServerId,
        AccountAttachmentDB = db.GetAccountAttachments(account.ServerId).FirstMapTo(_mapper),
        TeamSettingDB = ArenaService.CreateArenaTeamSetting(db, account, _mapper, false)
    };

    // Defending squad for one opponent id: a real account, a placeholder id from the ranking list, or the
    // legacy dummy. Placeholder squads are regenerated from the same seed the list used.
    private async Task<ArenaUserDB> BuildDefendingUser(
        SchaleDataContext db,
        AccountDBServer account,
        long opponentAccountServerId,
        long opponentRank)
    {
        var opponentAccount = await db.Accounts.FirstOrDefaultAsync(x => x.ServerId == opponentAccountServerId);

        if (opponentAccount != null)
        {
            return ArenaService.CreateArenaUser(
                opponentAccount.ServerId,
                opponentAccount.RepresentCharacterServerId,
                opponentAccount.Nickname ?? "Opponent",
                opponentRank,
                opponentAccount.Level,
                ArenaService.CreateArenaTeamSetting(db, opponentAccount, _mapper, true));
        }

        if (opponentAccountServerId >= ArenaService.MockAccountIdFloor)
        {
            var squads = _presets.Squads("pvp");
            var team = ArenaService.BuildArenaTeam(db, account, _mapper, squads, opponentAccountServerId, opponentRank);

            return ArenaService.CreateArenaUser(
                opponentAccountServerId,
                team?.LeaderCharacterId ?? 0,
                ArenaService.MockName(opponentAccountServerId),
                opponentRank,
                90,
                team ?? ArenaService.DummyTeamFormation);
        }

        return ArenaService.CreateArenaUser(
            opponentAccountServerId,
            10065,
            "Dummy Opponent",
            opponentRank,
            90,
            ArenaService.DummyTeamFormation);
    }

    [ProtocolHandler(Protocol.Arena_EnterBattlePart2)]
    public async Task<ArenaEnterBattlePart2Response> EnterBattlePart2(
        SchaleDataContext db,
        ArenaEnterBattlePart2Request request,
        ArenaEnterBattlePart2Response response)
    {
        var account = await _sessionService.GetAuthenticatedUser(db, request.SessionKey);

        ArenaDump.Write("battle-part2-request", request.ArenaBattleDB);

        // The result screen is drawn from this payload, so rebuild any squad the client sent back thin
        // instead of echoing it: a missing team would render as empty level-53 cards.
        var battle = request.ArenaBattleDB ?? new ArenaBattleDB();

        if (battle.AttackingUserDB?.TeamSettingDB?.MainCharacters is not { Count: > 0 })
            battle.AttackingUserDB = BuildAttackingUser(db, account);

        if (battle.DefendingUserDB?.TeamSettingDB?.MainCharacters is not { Count: > 0 })
        {
            var opponentId = battle.DefendingUserDB?.AccountServerId ?? 0;
            var opponentRank = battle.DefendingUserDB?.Rank ?? 1;
            battle.DefendingUserDB = await BuildDefendingUser(db, account, opponentId, opponentRank);
        }

        // The client's copies of both squads also arrive with ServerId 0, so repair them first, otherwise
        // the summary has nothing to point at and the result cards stay empty.
        if (battle.AttackingUserDB?.TeamSettingDB is { } attackingTeam)
            RepairTeamServerIds(db, account.ServerId, attackingTeam);

        if (battle.DefendingUserDB?.TeamSettingDB is { } defendingTeam)
            RepairTeamServerIds(db, account.ServerId, defendingTeam);

        if (battle.BattleSummary != null)
            HydrateBattleSummary(battle.BattleSummary, battle.AttackingUserDB, battle.DefendingUserDB);

        response.ArenaBattleDB = battle;
        response.ArenaPlayerInfoDB = BuildPlayerInfo(account);
        ArenaDump.Write("battle-part2-response", response.ArenaBattleDB);

        var ticketCost = _excelService.GetTable<ConstArenaExcelT>().FirstOrDefault()?.TicketCost ?? 1;
        if (ticketCost <= 0) ticketCost = 1;

        var currency = db.Currencies.FirstOrDefault(x => x.AccountServerId == account.ServerId);
        if (currency != null
            && currency.CurrencyDict.TryGetValue(CurrencyTypes.ArenaTicket, out var tickets)
            && tickets >= ticketCost)
        {
            var resolver = await _parcelHandler.BuildParcel(
                db, account,
                new ParcelResult(ParcelType.Currency, (long)CurrencyTypes.ArenaTicket, ticketCost),
                isConsume: true);
            await db.SaveChangesAsync();
            response.AccountCurrencyDB = resolver.ParcelResult.AccountCurrencyDB;
        }

        response.AccountCurrencyDB ??= db.GetAccountCurrencies(account.ServerId).FirstMapTo(_mapper);

        return response;
    }

    // the unsplit enter; live clients send the Part1/Part2 pair instead but the protocol is still declared
    [ProtocolHandler(Protocol.Arena_EnterBattle)]
    public async Task<ArenaEnterBattleResponse> EnterBattle(
        SchaleDataContext db,
        ArenaEnterBattleRequest request,
        ArenaEnterBattleResponse response)
    {
        var account = await _sessionService.GetAuthenticatedUser(db, request.SessionKey);

        var opponentAccount = await db.Accounts.FirstOrDefaultAsync(x => x.ServerId == request.OpponentAccountServerId);
        var defendingUser = opponentAccount != null
            ? ArenaService.CreateArenaUser(
                opponentAccount.ServerId,
                opponentAccount.RepresentCharacterServerId,
                opponentAccount.Nickname ?? "Opponent",
                request.OpponentIndex + 1,
                opponentAccount.Level,
                ArenaService.CreateArenaTeamSetting(db, opponentAccount, _mapper, true))
            : ArenaService.CreateArenaUser(
                request.OpponentAccountServerId,
                10065,
                "Dummy Opponent",
                request.OpponentIndex + 1,
                90,
                ArenaService.DummyTeamFormation);

        response.ArenaBattleDB = new ArenaBattleDB
        {
            Season = 1,
            Group = 1,
            BattleStartTime = account.GameSettings.ServerDateTime(),
            Seed = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            AttackingUserDB = new ArenaUserDB
            {
                AccountServerId = account.ServerId,
                NickName = account.Nickname ?? "Player",
                Rank = 1,
                Level = account.Level,
                RepresentCharacterUniqueId = account.RepresentCharacterServerId,
                AccountAttachmentDB = db.GetAccountAttachments(account.ServerId).FirstMapTo(_mapper),
                TeamSettingDB = ArenaService.CreateArenaTeamSetting(db, account, _mapper, false)
            },
            DefendingUserDB = defendingUser
        };
        response.ArenaPlayerInfoDB = BuildPlayerInfo(account);
        response.AccountCurrencyDB = db.GetAccountCurrencies(account.ServerId).FirstMapTo(_mapper);

        return response;
    }

    [ProtocolHandler(Protocol.Arena_CumulativeTimeReward)]
    public async Task<ArenaCumulativeTimeRewardResponse> CumulativeTimeReward(
        SchaleDataContext db,
        ArenaCumulativeTimeRewardRequest request,
        ArenaCumulativeTimeRewardResponse response)
    {
        var account = await _sessionService.GetAuthenticatedUser(db, request.SessionKey);
        var info = account.ContentInfo.ArenaDataInfo;
        var now = account.GameSettings.ServerDateTime();

        var accrued = AccruedTimeReward(account);
        if (accrued > 0)
        {
            var (type, id, _) = TimeRewardRatePerHour();
            var resolver = await _parcelHandler.BuildParcel(
                db, account, new ParcelResult(type, id, accrued));
            response.ParcelResult = resolver.ParcelResult;
        }
        else
        {
            response.ParcelResult = new ParcelResultDB
            {
                AccountDB = account.ToMap(_mapper),
                AccountCurrencyDB = db.GetAccountCurrencies(account.ServerId).FirstOrDefaultMapTo(_mapper)
            };
        }

        info.CumulativeTimeReward = 0;
        info.TimeRewardLastUpdateTime = now;
        db.Entry(account).Property(x => x.ContentInfo).IsModified = true;
        await db.SaveChangesAsync();

        response.TimeRewardAmount = 0;
        response.TimeRewardLastUpdateTime = now;

        return response;
    }

    [ProtocolHandler(Protocol.Arena_DailyReward)]
    public async Task<ArenaDailyRewardResponse> DailyReward(
        SchaleDataContext db,
        ArenaDailyRewardRequest request,
        ArenaDailyRewardResponse response)
    {
        var account = await _sessionService.GetAuthenticatedUser(db, request.SessionKey);
        var info = account.ContentInfo.ArenaDataInfo;
        var now = account.GameSettings.ServerDateTime();

        var alreadyClaimedToday = info.LastDailyRewardClaimTime is DateTime last && last.Date == now.Date;
        if (!alreadyClaimedToday)
        {
            var dailyRow = _excelService.GetTable<ArenaRewardExcelT>()
                .FirstOrDefault(x => x.ArenaRewardType == ArenaRewardType.Daily
                    && x.RankStart <= 1 && 1 <= x.RankEnd
                    && (x.RewardParcelAmount?.Count ?? 0) > 0);

            var parcels = dailyRow != null
                ? ParcelResult.ConvertParcelResult(dailyRow.RewardParcelType, dailyRow.RewardParcelUniqueId, dailyRow.RewardParcelAmount)
                : new List<ParcelResult> { new(ParcelType.Currency, (long)CurrencyTypes.ArenaTicket, 5) };

            var resolver = await _parcelHandler.BuildParcel(db, account, parcels);
            response.ParcelResult = resolver.ParcelResult;

            info.LastDailyRewardClaimTime = now;
            db.Entry(account).Property(x => x.ContentInfo).IsModified = true;
            await db.SaveChangesAsync();
        }
        else
        {
            response.ParcelResult = new ParcelResultDB
            {
                AccountDB = account.ToMap(_mapper),
                AccountCurrencyDB = db.GetAccountCurrencies(account.ServerId).FirstOrDefaultMapTo(_mapper)
            };
        }

        response.DailyRewardActiveTime = NextDailyRewardActiveTime(account);

        return response;
    }

    [ProtocolHandler(Protocol.Arena_RankList)]
    public async Task<ArenaRankListResponse> RankList(
        SchaleDataContext db,
        ArenaRankListRequest request,
        ArenaRankListResponse response)
    {
        var account = await _sessionService.GetAuthenticatedUser(db, request.SessionKey);

        var rankedUsers = new List<ArenaUserDB>();
        var rank = 1;
        foreach (var other in db.Accounts.OrderByDescending(x => x.Level).ThenBy(x => x.ServerId).ToList())
        {
            rankedUsers.Add(ArenaService.CreateArenaUser(
                other.ServerId,
                other.RepresentCharacterServerId,
                other.Nickname ?? "Sensei",
                rank++,
                other.Level,
                ArenaService.CreateArenaTeamSetting(db, other, _mapper, true)));
        }

        response.TopRankedUserDBs = rankedUsers
            .Skip(Math.Max(0, request.StartIndex))
            .Take(request.Count > 0 ? request.Count : rankedUsers.Count)
            .ToList();

        return response;
    }

    [ProtocolHandler(Protocol.Arena_History)]
    public async Task<ArenaHistoryResponse> History(
        SchaleDataContext db,
        ArenaHistoryRequest request,
        ArenaHistoryResponse response)
    {
        await _sessionService.GetAuthenticatedUser(db, request.SessionKey);

        // No arena battle persistence yet; an empty (non-null) list keeps the client happy.
        response.ArenaHistoryDBs = [];
        response.ArenaDamageReportDB = [];

        return response;
    }

    [ProtocolHandler(Protocol.Arena_SettingChange)]
    public async Task<ArenaSettingChangeResponse> SettingChange(
        SchaleDataContext db,
        ArenaSettingChangeRequest request,
        ArenaSettingChangeResponse response)
    {
        await _sessionService.GetAuthenticatedUser(db, request.SessionKey);
        return response;
    }

    [ProtocolHandler(Protocol.Arena_SettingAnonymous)]
    public async Task<ArenaSettingAnonymousResponse> SettingAnonymous(
        SchaleDataContext db,
        ArenaSettingAnonymousRequest request,
        ArenaSettingAnonymousResponse response)
    {
        var account = await _sessionService.GetAuthenticatedUser(db, request.SessionKey);

        account.ContentInfo.Option.ArenaIsAnonymous = request.IsAnonymous;
        db.Entry(account).Property(x => x.ContentInfo).IsModified = true;
        await db.SaveChangesAsync();

        return response;
    }

    [ProtocolHandler(Protocol.Arena_BattleResult)]
    public async Task<ArenaBattleResultResponse> BattleResult(
        SchaleDataContext db,
        ArenaBattleResultRequest request,
        ArenaBattleResultResponse response)
    {
        var account = await _sessionService.GetAuthenticatedUser(db, request.SessionKey);
        ArenaDump.Write("battle-result-request", request.ArenaBattleDB);

        var battle = request.ArenaBattleDB;
        var summary = battle?.BattleSummary;

        if (summary != null && summary.Winner == Schale.MX.Logic.Battles.GroupTag.Group01)
        {
            var opponentRank = battle?.DefendingUserDB?.Rank ?? 0;
            var currentRank = StoredRank(account);

            // Winning against a higher ranked opponent takes over their position, which is how the ladder
            // is climbed in game.
            if (opponentRank > 0 && opponentRank < currentRank)
                account.ContentInfo.ArenaDataInfo.Rank = opponentRank;

            await db.SaveChangesAsync();
        }

        return response;
    }

    [ProtocolHandler(Protocol.Arena_CheckSeasonCloseReward)]
    public async Task<ArenaCheckSeasonCloseRewardResponse> CheckSeasonCloseReward(
        SchaleDataContext db,
        ArenaCheckSeasonCloseRewardRequest request,
        ArenaCheckSeasonCloseRewardResponse response)
    {
        await _sessionService.GetAuthenticatedUser(db, request.SessionKey);
        return response;
    }

    [ProtocolHandler(Protocol.Arena_RecordSync)]
    public async Task<ArenaRecordSyncResponse> RecordSync(
        SchaleDataContext db,
        ArenaRecordSyncRequest request,
        ArenaRecordSyncResponse response)
    {
        await _sessionService.GetAuthenticatedUser(db, request.SessionKey);
        return response;
    }

    [ProtocolHandler(Protocol.Arena_TicketPurchase)]
    public async Task<ArenaTicketPurchaseResponse> TicketPurchase(
        SchaleDataContext db,
        ArenaTicketPurchaseRequest request,
        ArenaTicketPurchaseResponse response)
    {
        await _sessionService.GetAuthenticatedUser(db, request.SessionKey);
        return response;
    }

    [ProtocolHandler(Protocol.Arena_DamageReport)]
    public async Task<ArenaDamageReportResponse> DamageReport(
        SchaleDataContext db,
        ArenaDamageReportRequest request,
        ArenaDamageReportResponse response)
    {
        await _sessionService.GetAuthenticatedUser(db, request.SessionKey);
        return response;
    }
}
