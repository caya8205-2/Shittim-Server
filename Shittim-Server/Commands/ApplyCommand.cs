using Shittim.Services.Client;
using Schale.Data;
using Schale.Data.GameModel;
using Schale.FlatData;
using Schale.Excel;
using Serilog;

namespace Shittim.Commands
{
    // Applies exactly the values the caller asks for instead of taking a character to the caps. The
    // Control Center builds the argument string from its sliders, so every field is optional and only
    // the fields present are touched - "max" is just the shortcut that MaxCommand already offers.
    [CommandHandler("apply", "Set exact character values (level, star, skills, potential, unique equipment, gear)",
        "!apply <all|id:1,2,3|name> [level:1-90] [star:1-5|base] [skill:1-10|max] [ex:1-5|max] [ps:0-25|max] [ue:1-60|off] [gear:1 [gearlvl:N] [geartier:N] [gearinclude:1]]")]
    internal class ApplyCommand : Command
    {
        public ApplyCommand(IClientConnection connection, string[] args, bool validate = true) : base(connection, args, validate) { }

        [Argument(0, @".+", "Target and options", ArgumentFlags.Remaining)]
        public string Expression { get; set; } = "all";

        // Level is independent of rarity: every student can reach 90 whatever its star grade, because
        // the star only sets base rarity and stats. Only the unique weapon has per-grade level caps.
        private static readonly int[] WeaponCapByStar = { 30, 40, 50, 60 };

        // Unique equipment unlocks at 5★ in-game; below that the client has no equipment screen for it.
        private const int UniqueEquipmentUnlockStarGrade = 5;

        public override async Task Execute()
        {
            using var context = await connection.Context.CreateDbContextAsync();
            var characterExcel = connection.ExcelTableService.GetTable<CharacterExcelT>();

            var tokens = (Expression ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (tokens.Length == 0)
            {
                await connection.SendChatMessage("Usage: !apply <all|id:1,2,3|name> [level:] [star:] [skill:] [ex:] [ps:] [ue:] [gear:]");
                return;
            }

            var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var targetTokens = new List<string>();
            foreach (var token in tokens)
            {
                // "id:1,2,3" is the id selector, not a generic option: it is stored under "id" so the
                // resolver can turn it into ids instead of trying to match it as a character name.
                if (token.StartsWith("id:", StringComparison.OrdinalIgnoreCase))
                {
                    options["id"] = token[3..];
                    continue;
                }

                var separator = token.IndexOf(':');
                if (separator > 0 && separator < token.Length - 1)
                    options[token[..separator]] = token[(separator + 1)..];
                else
                    targetTokens.Add(token);
            }

            var characters = ResolveTarget(context, characterExcel, targetTokens, options, out var resolveError);
            if (characters == null)
            {
                await connection.SendChatMessage(resolveError!);
                return;
            }

            if (characters.Count == 0)
            {
                await connection.SendChatMessage("You don't own any characters matching that target.");
                return;
            }

            int? level = ParseBounded(options, "level", 1, 90, out var levelError);
            var starIsBase = options.TryGetValue("star", out var starRaw) && starRaw.Equals("base", StringComparison.OrdinalIgnoreCase);
                        string? starError = null;
                        int? star = null;
                        if (!starIsBase)
                            star = ParseBounded(options, "star", 1, 5, out starError);
            int? skill = ParseBounded(options, "skill", 1, 10, out var skillError);
            int? ex = ParseBounded(options, "ex", 1, 5, out var exError);
            int? potential = ParseBounded(options, "ps", 0, 25, out var psError);
            int? weaponLevel = ParseBounded(options, "ue", 1, 60, out var ueError);

            var weaponOff = options.TryGetValue("ue", out var weaponRaw) && weaponRaw.Equals("off", StringComparison.OrdinalIgnoreCase);
            var fillGear = options.TryGetValue("gear", out var gearRaw) && gearRaw == "1";
            // Gear level is separate from the decision to touch gear: "equip at level 1" and "equip at
            // max" are both valid, and existing gear is only ever raised towards the requested level.
            int? gearLevel = ParseBounded(options, "gearlvl", 1, 999, out var gearLevelError);
            int? gearTier = ParseBounded(options, "geartier", 1, 10, out var gearTierError);
            var gearIncludeExisting = options.TryGetValue("gearinclude", out var gearIncludeRaw) && gearIncludeRaw == "1";

            var firstError = levelError ?? starError ?? skillError ?? exError ?? psError ?? ueError ?? gearLevelError ?? gearTierError;
            if (firstError != null)
            {
                await connection.SendChatMessage(firstError);
                return;
            }

            if (level == null && star == null && !starIsBase && skill == null && ex == null && potential == null && weaponLevel == null && !weaponOff && !fillGear)
            {
                await connection.SendChatMessage("Nothing to apply: pass at least one of level, star, skill, ex, ps, ue, gear.");
                return;
            }

            // The student level ceiling comes from the account level, not from rarity: an account at level 70
            // caps students at 70 no matter how many stars they have.
            var account = context.Accounts.FirstOrDefault(x => x.ServerId == connection.AccountServerId);
            var levelCap = account != null && account.Level > 0 ? Math.Min(90, account.Level) : 90;

            var weaponExcel = connection.ExcelTableService.GetTable<CharacterWeaponExcelT>();
            var applied = new List<string>();

            int touched = 0, weapons = 0, starClamped = 0, levelClamped = 0, ueSkipped = 0, ueRemoved = 0;

            foreach (var character in characters)
            {
                var characterData = characterExcel.FirstOrDefault(x => x.Id == character.UniqueId);
                if (characterData == null)
                    continue;

                if (starIsBase)
                {
                    var baseGrade = Math.Max(1, characterData.DefaultStarGrade);
                    var baseCeiling = characterData.MaxStarGrade > 0 ? characterData.MaxStarGrade : 5;
                    character.StarGrade = Math.Clamp(baseGrade, 1, baseCeiling);
                }
                else if (star.HasValue)
                {
                    // Stars only ever move up from the character's own rarity: pulling a 3★ down to 1★
                    // is not a state the game represents, and the client breaks on it.
                    var floor = Math.Max(1, characterData.DefaultStarGrade);
                    var ceiling = characterData.MaxStarGrade > 0 ? characterData.MaxStarGrade : 5;
                    // Absolute write: lowering a star grade is allowed, but never below the character's
                    // own rarity, which is the one bound the game itself enforces.
                    var requested = Math.Clamp(star.Value, floor, ceiling);
                    if (requested != star.Value)
                        starClamped++;
                    character.StarGrade = requested;
                }

                if (level.HasValue)
                {
                    var requestedLevel = Math.Min(level.Value, levelCap);
                    if (requestedLevel != level.Value)
                        levelClamped++;
                    character.Level = requestedLevel;
                    character.Exp = 0;
                }

                if ((star.HasValue || starIsBase) && character.StarGrade < UniqueEquipmentUnlockStarGrade)
                {
                    var stale = context.Weapons.FirstOrDefault(x =>
                        x.AccountServerId == connection.AccountServerId &&
                        x.UniqueId == character.UniqueId);
                    if (stale != null)
                    {
                        context.Weapons.Remove(stale);
                        ueRemoved++;
                    }
                }

                if (skill.HasValue)
                {
                    character.PublicSkillLevel = skill.Value;
                    character.PassiveSkillLevel = skill.Value;
                    character.ExtraPassiveSkillLevel = skill.Value;
                }

                if (ex.HasValue)
                    character.ExSkillLevel = ex.Value;

                if (potential.HasValue)
                    character.PotentialStats = new Dictionary<int, int> { { 1, potential.Value }, { 2, potential.Value }, { 3, potential.Value } };

                if (weaponOff || weaponLevel.HasValue)
                {
                    var existing = context.Weapons.FirstOrDefault(x =>
                        x.AccountServerId == connection.AccountServerId &&
                        x.UniqueId == character.UniqueId);

                    if (weaponOff)
                    {
                        if (existing != null)
                            context.Weapons.Remove(existing);
                    }
                    else
                    {
                        var weaponData = weaponExcel.FirstOrDefault(x => x.Id == character.UniqueId);
                        if (weaponData != null && (weaponData.MaxLevel?.Count ?? 0) > 0 && character.StarGrade >= UniqueEquipmentUnlockStarGrade)
                        {
                            if (existing == null)
                            {
                                existing = new WeaponDBServer
                                {
                                    AccountServerId = connection.AccountServerId,
                                    UniqueId = character.UniqueId,
                                    BoundCharacterServerId = character.ServerId,
                                };
                                context.Weapons.Add(existing);
                            }

                            // Star is derived from the requested level so the two can never disagree.
                            var grade = weaponLevel.Value <= 30 ? 1
                                : weaponLevel.Value <= 40 ? 2
                                : weaponLevel.Value <= 50 ? 3
                                : 4;
                            existing.StarGrade = grade;
                            existing.Level = Math.Min(weaponLevel.Value, WeaponCapByStar[grade - 1]);
                            existing.Exp = 0;
                            weapons++;
                        }
                        else if (weaponData != null && (weaponData.MaxLevel?.Count ?? 0) > 0)
                        {
                            // Unique equipment only exists from 5★ up; below that the row would be a
                            // state the game never produces.
                            ueSkipped++;
                        }
                        else
                        {
                            Log.Information("Character {Id} has no unique equipment row; ue skipped.", character.UniqueId);
                        }
                    }
                }

                context.Characters.Update(character);
                touched++;
            }

            await context.SaveChangesAsync();

            var gearPlaced = 0;
            var gearUpgraded = 0;
            var gearTierUsed = 0;
            if (fillGear)
            {
                var gear = await MaxCommand.ApplyGear(
                    connection, context, characterExcel,
                    characters.Select(x => x.UniqueId).ToList(),
                    gearLevel, gearIncludeExisting, gearTier);
                gearPlaced = gear.Placed;
                gearUpgraded = gear.Upgraded;
                gearTierUsed = gear.Tier;
            }

            if (level.HasValue)
                applied.Add($"level {level.Value}{(levelClamped > 0 ? $" capped at {levelCap}" : string.Empty)}");
            if (starIsBase) applied.Add("star reset to base rarity");
            else if (star.HasValue) applied.Add($"star {star.Value}★{(starClamped > 0 ? $" (clamped for {starClamped})" : string.Empty)}");
            if (skill.HasValue) applied.Add($"skills {skill.Value}/{skill.Value}/{skill.Value}");
            if (ex.HasValue) applied.Add($"EX {ex.Value}");
            if (potential.HasValue) applied.Add($"potential {potential.Value}");
            if (weaponLevel.HasValue) applied.Add($"UE {weaponLevel.Value} ({weapons} rows{(ueSkipped > 0 ? $", {ueSkipped} skipped below {UniqueEquipmentUnlockStarGrade}★" : string.Empty)})");
            if (weaponOff) applied.Add("unique equipment removed");
            if (ueRemoved > 0) applied.Add($"UE removed for {ueRemoved} below {UniqueEquipmentUnlockStarGrade}★");
            if (fillGear)
            {
                // The tier actually written, after clamping to the band the level allows.
                var tierNote = gearTierUsed > 0 ? $"T{gearTierUsed} " : "highest tier ";
                var levelNote = gearLevel.HasValue ? $"Lv {gearLevel.Value}" : "max level";
                applied.Add($"{gearPlaced} gear placed ({tierNote}{levelNote}){(gearUpgraded > 0 ? $", {gearUpgraded} upgraded" : string.Empty)}");
            }

            var summary = applied.Count > 0 ? string.Join(", ", applied) : "nothing";
            await connection.SendChatMessage($"Applied to {touched} student(s): {summary}.");
        }

        private List<CharacterDBServer>? ResolveTarget(
            SchaleDataContext context,
            List<CharacterExcelT> characterExcel,
            List<string> targetTokens,
            Dictionary<string, string> options,
            out string? error)
        {
            error = null;

            if (options.Remove("id", out var csv))
            {
                var ids = csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(x => long.TryParse(x, out var value) ? value : 0)
                    .Where(x => x > 0)
                    .ToHashSet();

                if (ids.Count == 0)
                {
                    error = "No valid character ids in that list.";
                    return null;
                }

                return context.Characters
                    .Where(x => x.AccountServerId == connection.AccountServerId && ids.Contains(x.UniqueId))
                    .ToList();
            }

            var target = targetTokens.Count == 0 ? "all" : string.Join(' ', targetTokens);
            if (target.Equals("all", StringComparison.OrdinalIgnoreCase))
                return context.Characters.Where(x => x.AccountServerId == connection.AccountServerId).ToList();

            var characterData = characterExcel.FirstOrDefault(x =>
                x.DevName != null && x.DevName.Contains(target, StringComparison.OrdinalIgnoreCase));

            if (characterData == null)
            {
                error = $"Character '{target}' not found!";
                return null;
            }

            return context.Characters
                .Where(x => x.AccountServerId == connection.AccountServerId && x.UniqueId == characterData.Id)
                .ToList();
        }

        private static int? ParseBounded(Dictionary<string, string> options, string key, int min, int max, out string? error)
        {
            error = null;
            if (!options.TryGetValue(key, out var raw))
                return null;

            if (raw.Equals("max", StringComparison.OrdinalIgnoreCase))
                return max;

            if (!int.TryParse(raw, out var value))
            {
                error = $"'{key}' must be a number between {min} and {max}.";
                return null;
            }

            if (value < min || value > max)
            {
                error = $"'{key}' must be between {min} and {max}.";
                return null;
            }

            return value;
        }
    }
}