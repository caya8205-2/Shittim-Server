using Shittim.Services.Client;
using Schale.Data;
using Schale.Data.GameModel;
using Schale.FlatData;
using Schale.Excel;

namespace Shittim.Commands
{
    [CommandHandler("max", "Max out character stats and unique equipment", "!max <all|ue|charactername>")]
    internal class MaxCommand : Command
    {
        public MaxCommand(IClientConnection connection, string[] args, bool validate = true) : base(connection, args, validate) { }

        [Argument(0, @"^.+$", "Character name or 'all'")]
        public string CharacterName { get; set; } = "all";

        public override async Task Execute()
        {
            using var context = await connection.Context.CreateDbContextAsync();
            var characterExcel = connection.ExcelTableService.GetTable<CharacterExcelT>();

            if (CharacterName.ToLower() == "all")
            {
                await MaxAllCharacters(context, characterExcel);
            }
            else if (CharacterName.ToLower() == "ue")
            {
                await MaxAllUniqueWeapons(context);
            }
            else if (CharacterName.ToLower() == "gear")
            {
                var ownIds = context.Characters
                    .Where(x => x.AccountServerId == connection.AccountServerId)
                    .Select(x => x.UniqueId)
                    .ToList();
                var gear = await ApplyGear(connection, context, characterExcel, ownIds);
                await connection.SendChatMessage($"Equipped {gear.Placed} empty gear slots at max level.");
            }
            else if (CharacterName.StartsWith("id:", StringComparison.OrdinalIgnoreCase))
            {
                await MaxCharactersById(context, characterExcel, CharacterName[3..]);
            }
            else
            {
                await MaxSingleCharacter(context, characterExcel, CharacterName);
            }
        }

        private async Task MaxAllCharacters(SchaleDataContext context, List<CharacterExcelT> characterExcel)
        {
            var ownedCharacters = context.Characters
                .Where(x => x.AccountServerId == connection.AccountServerId)
                .ToList();

            if (ownedCharacters.Count == 0)
            {
                await connection.SendChatMessage("You don't own any characters!");
                return;
            }

            var weaponExcel = connection.ExcelTableService.GetTable<CharacterWeaponExcelT>();
            var weaponsByCharacter = context.Weapons
                .Where(x => x.AccountServerId == connection.AccountServerId)
                .ToDictionary(x => x.UniqueId);

            int count = 0;
            int equipped = 0;

            foreach (var character in ownedCharacters)
            {
                var characterData = characterExcel.FirstOrDefault(x => x.Id == character.UniqueId);
                if (characterData == null)
                    continue;

                ApplyMaxCharacter(character, characterData);

                context.Characters.Update(character);
                count++;

                // Unique equipment only exists for characters the weapon table covers, and its row is
                // otherwise created by equipping it in the client - which is the step that forces the
                // equip and breakthrough animations. Create the row here and take it to the caps the
                // table defines, so nothing has to be equipped by hand.
                var weaponData = weaponExcel.FirstOrDefault(x => x.Id == character.UniqueId);
                if (weaponData == null || MaxWeaponLevel(weaponData) <= 0)
                    continue;

                if (!weaponsByCharacter.TryGetValue(character.UniqueId, out var weapon))
                {
                    weapon = new WeaponDBServer
                    {
                        AccountServerId = connection.AccountServerId,
                        UniqueId = character.UniqueId,
                        BoundCharacterServerId = character.ServerId,
                    };
                    context.Weapons.Add(weapon);
                    weaponsByCharacter[character.UniqueId] = weapon;
                }

                ApplyMaxWeapon(weapon, weaponData);
                equipped++;
            }

            await context.SaveChangesAsync();
            var gear = await ApplyGear(connection, context, characterExcel, ownedCharacters.Select(x => x.UniqueId).ToList());
            await connection.SendChatMessage($"Maxed out {count} characters, {equipped} unique weapons, filled {gear.Placed} equipment slots.");
        }

        private async Task MaxSingleCharacter(SchaleDataContext context, List<CharacterExcelT> characterExcel, string name)
        {
            var characterData = characterExcel.FirstOrDefault(x => 
                x.DevName != null && 
                x.DevName.Contains(name, StringComparison.OrdinalIgnoreCase));

            if (characterData == null)
            {
                await connection.SendChatMessage($"Character '{name}' not found!");
                return;
            }

            var character = context.Characters.FirstOrDefault(x => 
                x.AccountServerId == connection.AccountServerId && 
                x.UniqueId == characterData.Id);

            if (character == null)
            {
                await connection.SendChatMessage($"You don't own {characterData.DevName}!");
                return;
            }

            ApplyMaxCharacter(character, characterData);
            context.Characters.Update(character);

            var weaponExcel = connection.ExcelTableService.GetTable<CharacterWeaponExcelT>();
            var weaponData = weaponExcel.FirstOrDefault(x => x.Id == characterData.Id);
            var weaponNote = "no unique equipment";

            if (weaponData != null && MaxWeaponLevel(weaponData) > 0)
            {
                var weapon = context.Weapons.FirstOrDefault(x =>
                    x.AccountServerId == connection.AccountServerId &&
                    x.UniqueId == characterData.Id);

                if (weapon == null)
                {
                    weapon = new WeaponDBServer
                    {
                        AccountServerId = connection.AccountServerId,
                        UniqueId = characterData.Id,
                        BoundCharacterServerId = character.ServerId,
                    };
                    context.Weapons.Add(weapon);
                }

                ApplyMaxWeapon(weapon, weaponData);
                weaponNote = $"UE {weapon.StarGrade}★ Lv.{weapon.Level}";
            }

            await context.SaveChangesAsync();
            var gear = await ApplyGear(connection, context, characterExcel, new[] { characterData.Id });
            await connection.SendChatMessage($"Maxed out {characterData.DevName}! (Lv90, {characterData.MaxStarGrade}★, Favor 100, Skills 10/5/10/10, PotentialStats maxed, {weaponNote}, {gear.Placed} gear slots filled)");
        }

        // Equipment tiers are bands of level, and every band starts at the previous tier's maximum because
        // evolution keeps the level: T1 1-10, T2 10-20, T3 20-30, T4 30-40, T5 40-45, T6 45-50, T7 50-55,
        // T8 55-60, T9 60-65, T10 65-70. A tier therefore becomes reachable exactly when its band does,
        // and no placement can produce a state like T2 at level 9.
        internal static int GearTierForLevel(int level) => level switch
        {
            <= 10 => 1,
            <= 20 => 2,
            <= 30 => 3,
            <= 40 => 4,
            <= 45 => 5,
            <= 50 => 6,
            <= 55 => 7,
            <= 60 => 8,
            <= 65 => 9,
            _ => 10,
        };

        // Highest tier whose band includes this level: level 10 may already be evolved to T2, level 45 to
        // T6, and so on. Anything above that would be a tier the game has not unlocked yet.
        internal static int GearMaxTierForLevel(int level) => level switch
        {
            >= 65 => 10,
            >= 60 => 9,
            >= 55 => 8,
            >= 50 => 7,
            >= 45 => 6,
            >= 40 => 5,
            >= 30 => 4,
            >= 20 => 3,
            >= 10 => 2,
            _ => 1,
        };

        // Slots are defined per character in the Excel table (EquipmentSlot), so each character gets the
        // best item for the slot it actually has rather than one fixed set of categories. Slots the
        // player already filled are left alone - only empty ones receive an item.
        // Static so ApplyCommand can reuse it: filling gear is the same operation whether the caller
        // asked for "everything maxed" or for a single chosen field.
        internal static async Task<(int Placed, int Upgraded, int Tier)> ApplyGear(IClientConnection connection, SchaleDataContext context, List<CharacterExcelT> characterExcel, ICollection<long>? onlyCharacterUniqueIds = null, int? level = null, bool includeExisting = false, int? tier = null)
        {
            // The tier slider may sit above the tier the level requires (evolution is manual), but never
            // below it and never beyond a tier whose band has not been reached yet.
            int? requiredTier = level.HasValue ? GearTierForLevel(level.Value) : null;
            int? maxTier = level.HasValue ? GearMaxTierForLevel(level.Value) : null;
            int? effectiveTier = tier.HasValue && requiredTier.HasValue
                ? Math.Clamp(tier.Value, requiredTier.Value, maxTier!.Value)
                : (tier ?? requiredTier);
            var ownedCharacters = context.Characters
                .Where(x => x.AccountServerId == connection.AccountServerId
                    && (onlyCharacterUniqueIds == null || onlyCharacterUniqueIds.Contains(x.UniqueId)))
                .ToList();

            if (ownedCharacters.Count == 0)
            {
                await connection.SendChatMessage("You don't own any characters!");
                return (0, 0, effectiveTier ?? 0);
            }

            var equipmentExcel = connection.ExcelTableService.GetTable<EquipmentExcelT>();
            var pending = new List<(CharacterDBServer Character, int Slot, EquipmentDBServer Equipment)>();
            var upgraded = 0;

            foreach (var character in ownedCharacters)
            {
                var characterData = characterExcel.FirstOrDefault(x => x.Id == character.UniqueId);
                if (characterData?.EquipmentSlot == null)
                    continue;

                for (var slot = 0; slot < characterData.EquipmentSlot.Count; slot++)
                {
                    while (character.EquipmentServerIds.Count <= slot)
                        character.EquipmentServerIds.Add(0);

                    var currentServerId = character.EquipmentServerIds[slot];
                    if (currentServerId != 0)
                    {
                        if (!includeExisting)
                            continue;

                        // Existing gear is upgraded in place: the tier is raised only as far as the requested level
                        // requires, and the level never drops. Evolution keeps the level in-game, so a band
                        // starts at the previous tier's maximum (T2 covers 10-20, T3 20-30, and so on).
                        var existing = context.Equipments.FirstOrDefault(x => x.ServerId == currentServerId);
                        var existingTemplate = existing == null ? null : equipmentExcel.FirstOrDefault(x => x.Id == existing.UniqueId);
                        if (existing == null || existingTemplate == null)
                            continue;

                        var requestedLevel = level ?? existingTemplate.MaxLevel;
                        var targetTier = effectiveTier ?? (int)existingTemplate.TierInit;

                        // Gear is written as requested - lowering it is allowed as an experiment - but only
                        // inside the band the level belongs to, so a state like T2 at Lv9 can never appear.
                        if (targetTier != (int)existingTemplate.TierInit)
                        {
                            var targetTemplate = equipmentExcel
                                .GetEquipmentExcelByCategory(characterData.EquipmentSlot[slot])
                                .GetCharacterEquipment()
                                .Where(x => (int)x.TierInit == targetTier)
                                .OrderByDescending(x => x.MaxLevel)
                                .FirstOrDefault();

                            if (targetTemplate == null)
                                continue;

                            existing.UniqueId = targetTemplate.Id;
                            existing.Tier = targetTier;
                            existing.Level = Math.Min(requestedLevel, targetTemplate.MaxLevel);
                            upgraded++;
                            continue;
                        }

                        existing.Level = Math.Min(requestedLevel, existingTemplate.MaxLevel);
                        upgraded++;
                        continue;
                    }

                    var best = equipmentExcel
                        .GetEquipmentExcelByCategory(characterData.EquipmentSlot[slot])
                        .GetCharacterEquipment()
                        .Where(x => !effectiveTier.HasValue || (int)x.TierInit == effectiveTier.Value)
                        .OrderByDescending(x => (int)x.TierInit)
                        .ThenByDescending(x => x.MaxLevel)
                        .FirstOrDefault();

                    if (best == null)
                        continue;

                    var equipment = new EquipmentDBServer
                    {
                        UniqueId = best.Id,
                        Level = Math.Min(level ?? best.MaxLevel, best.MaxLevel),
                        Tier = (int)best.TierInit,
                        StackCount = 1,
                        BoundCharacterServerId = character.ServerId,
                    };

                    context.AddEquipment(connection.AccountServerId, equipment);
                    pending.Add((character, slot, equipment));
                }
            }

            // ServerId only exists after the rows are inserted, so slot ids are assigned in a second
            // pass; assigning straight away would link every character to ServerId 0.
            await context.SaveChangesAsync();

            foreach (var (character, slot, equipment) in pending)
                character.EquipmentServerIds[slot] = equipment.ServerId;

            await context.SaveChangesAsync();
            return (pending.Count, upgraded, effectiveTier ?? 0);
        }

        private static void ApplyMaxCharacter(CharacterDBServer character, CharacterExcelT characterData)
        {
            character.Level = 90;
            character.Exp = 0;
            character.StarGrade = characterData.MaxStarGrade;
            character.FavorRank = 100;
            character.FavorExp = 0;
            character.PublicSkillLevel = 10;
            character.ExSkillLevel = 5;
            character.PassiveSkillLevel = 10;
            character.ExtraPassiveSkillLevel = 10;
            character.PotentialStats = new Dictionary<int, int> { { 1, 25 }, { 2, 25 }, { 3, 25 } };
        }

        // Bulk path for "max only these students", driven by the Control Center's filtered selection.
        private async Task MaxCharactersById(SchaleDataContext context, List<CharacterExcelT> characterExcel, string csv)
        {
            var ids = csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(x => long.TryParse(x, out var id) ? id : 0L)
                .Where(x => x > 0)
                .Distinct()
                .ToList();

            if (ids.Count == 0)
            {
                await connection.SendChatMessage("No valid character ids given.");
                return;
            }

            var selected = context.Characters
                .Where(x => x.AccountServerId == connection.AccountServerId && ids.Contains(x.UniqueId))
                .ToList();

            if (selected.Count == 0)
            {
                await connection.SendChatMessage("None of those characters belong to this account.");
                return;
            }

            var weaponExcel = connection.ExcelTableService.GetTable<CharacterWeaponExcelT>();
            var weaponsByCharacter = context.Weapons
                .Where(x => x.AccountServerId == connection.AccountServerId)
                .ToDictionary(x => x.UniqueId);

            var count = 0;
            var equipped = 0;

            foreach (var character in selected)
            {
                var characterData = characterExcel.FirstOrDefault(x => x.Id == character.UniqueId);
                if (characterData == null)
                    continue;

                ApplyMaxCharacter(character, characterData);
                context.Characters.Update(character);
                count++;

                if (character.StarGrade < UniqueEquipmentUnlockStarGrade)
                    continue;

                var weaponData = weaponExcel.FirstOrDefault(x => x.Id == character.UniqueId);
                if (weaponData == null || MaxWeaponLevel(weaponData) <= 0)
                    continue;

                if (!weaponsByCharacter.TryGetValue(character.UniqueId, out var weapon))
                {
                    weapon = new WeaponDBServer
                    {
                        AccountServerId = connection.AccountServerId,
                        UniqueId = character.UniqueId,
                        BoundCharacterServerId = character.ServerId,
                    };
                    context.Weapons.Add(weapon);
                    weaponsByCharacter[character.UniqueId] = weapon;
                }

                ApplyMaxWeapon(weapon, weaponData);
                equipped++;
            }

            await context.SaveChangesAsync();
            var gear = await ApplyGear(connection, context, characterExcel, ids);
            await connection.SendChatMessage($"Maxed {count} of {ids.Count} selected students, {equipped} unique weapons, filled {gear.Placed} gear slots.");
        }

        private async Task MaxAllUniqueWeapons(SchaleDataContext context)
        {
            var ownedCharacters = context.Characters
                .Where(x => x.AccountServerId == connection.AccountServerId)
                .ToList();

            if (ownedCharacters.Count == 0)
            {
                await connection.SendChatMessage("You don't own any characters!");
                return;
            }

            var weaponExcel = connection.ExcelTableService.GetTable<CharacterWeaponExcelT>();
            var weaponsByCharacter = context.Weapons
                .Where(x => x.AccountServerId == connection.AccountServerId)
                .ToDictionary(x => x.UniqueId);

            int equipped = 0;
            int skipped = 0;
            int belowUnlockStar = 0;

            foreach (var character in ownedCharacters)
            {
                // Unique equipment only unlocks once the student reaches 5★. A standalone run does not
                // raise star grades, so equipping below that would leave a weapon the client has no
                // legitimate state for.
                if (character.StarGrade < UniqueEquipmentUnlockStarGrade)
                {
                    belowUnlockStar++;
                    continue;
                }

                var weaponData = weaponExcel.FirstOrDefault(x => x.Id == character.UniqueId);
                if (weaponData == null || MaxWeaponLevel(weaponData) <= 0)
                {
                    skipped++;
                    continue;
                }

                if (!weaponsByCharacter.TryGetValue(character.UniqueId, out var weapon))
                {
                    weapon = new WeaponDBServer
                    {
                        AccountServerId = connection.AccountServerId,
                        UniqueId = character.UniqueId,
                        BoundCharacterServerId = character.ServerId,
                    };
                    context.Weapons.Add(weapon);
                    weaponsByCharacter[character.UniqueId] = weapon;
                }

                ApplyMaxWeapon(weapon, weaponData);
                equipped++;
            }

            await context.SaveChangesAsync();
            await connection.SendChatMessage($"Equipped and maxed {equipped} unique weapons ({skipped} characters have none, {belowUnlockStar} below {UniqueEquipmentUnlockStarGrade}★).");
        }

        private static void ApplyMaxWeapon(WeaponDBServer weapon, CharacterWeaponExcelT weaponData)
        {
            weapon.StarGrade = MaxWeaponStarGrade(weaponData);
            weapon.Level = MaxWeaponLevel(weaponData);
            weapon.Exp = 0;
        }

        // Progression the client can legitimately reach: ★4 is the highest weapon grade, and its cap is
        // UE60. The table carries a ★5 entry (UE70) that no released progression path unlocks yet, so it
        // is deliberately not reachable here. Star grade and level must agree: taking the highest
        // MaxLevel entry while stopping at ★4 would produce ★4 Lv.70, which is not a real state.
        private const int LegitMaxWeaponStarGrade = 4;

        // Unique equipment is gated behind the 5★ mystic unlock, so a standalone equip pass has to skip
        // students that are still below it.
        private const int UniqueEquipmentUnlockStarGrade = 5;

        private static int MaxWeaponStarGrade(CharacterWeaponExcelT weapon)
        {
            var grades = weapon.MaxLevel?.Count ?? 0;
            if (weapon.Unlock is { Count: > 0 })
                grades = Math.Min(grades, weapon.Unlock.Count);

            return Math.Max(Math.Min(grades, LegitMaxWeaponStarGrade), 1);
        }

        private static int MaxWeaponLevel(CharacterWeaponExcelT weapon)
        {
            if (weapon.MaxLevel is not { Count: > 0 })
                return 0;

            // MaxLevel holds one cap per grade, so the ceiling is the entry for the grade reached above.
            var gradeIndex = Math.Min(MaxWeaponStarGrade(weapon), weapon.MaxLevel.Count) - 1;
            return weapon.MaxLevel[Math.Max(gradeIndex, 0)];
        }
    }
}
