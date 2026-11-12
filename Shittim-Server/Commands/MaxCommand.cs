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
            await connection.SendChatMessage($"Maxed out {count} characters and {equipped} unique weapons!");
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
            await connection.SendChatMessage($"Maxed out {characterData.DevName}! (Lv90, {characterData.MaxStarGrade}★, Favor 100, Skills 10/5/10/10, PotentialStats maxed, {weaponNote})");
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

            foreach (var character in ownedCharacters)
            {
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
            await connection.SendChatMessage($"Equipped and maxed {equipped} unique weapons ({skipped} characters have none).");
        }

        private static void ApplyMaxWeapon(WeaponDBServer weapon, CharacterWeaponExcelT weaponData)
        {
            weapon.StarGrade = MaxWeaponStarGrade(weaponData);
            weapon.Level = MaxWeaponLevel(weaponData);
            weapon.Exp = 0;
        }

        // MaxLevel carries one cap per breakthrough grade, so its highest entry is the ceiling and its
        // length is how many grades the weapon can reach. Both come from the table rather than being
        // hardcoded, because the caps differ between weapon releases.
        private static int MaxWeaponLevel(CharacterWeaponExcelT weapon) =>
            weapon.MaxLevel is { Count: > 0 } ? weapon.MaxLevel.Max() : 0;

        private static int MaxWeaponStarGrade(CharacterWeaponExcelT weapon)
        {
            var grades = weapon.MaxLevel?.Count ?? 0;
            if (weapon.Unlock is { Count: > 0 })
                grades = Math.Min(grades, weapon.Unlock.Count);

            return Math.Max(grades, 1);
        }
    }
}
