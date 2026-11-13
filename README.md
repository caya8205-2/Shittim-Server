# Shittim Server

A private server for Blue Archive's Steam release, written in C# on ASP.NET Core (.NET 10). Progress lives in a local SQLite database, and it's far enough along that the game is just playable: log in, pull, clear stages, decorate the cafe.

## About this fork

This repository is `caya8205-2/Shittim-Server`, tracking [Neoexm/Shittim-Server](https://github.com/Neoexm/Shittim-Server) with runtime fixes merged on top.

**"Stable" here describes the runtime, not the game version.** Of the builds available at the time of writing, this is the one whose ASP.NET server and mitmproxy redirect keep running through a session: no preset packets dying in a handler exception, no squad screen that stops taking input, no proxy that has to be restarted mid-session. It does **not** mean "ships the newest Blue Archive build on Steam". The server compiles its Excel data against the 1.93 client line, so a 1.96 install has to be rolled back before it can log in. Read "latest" as *latest stable runtime*, not *latest game version*.

### Included here and not yet upstream

- Squad presets work end to end: creating a preset, importing a formation into one, renaming a group, and switching between groups and the `Recent` tab. Upstream, `Echelon_PresetSave` throws on a slot that does not exist yet, and the preset packets drop a `0`-valued `GroupIndex`/`Index`, which leaves a card titled `Unit {0}` with placeholder units and marks every tab as selected at once. Sent upstream as [#50](https://github.com/Neoexm/Shittim-Server/pull/50).
- `!apply` writes exactly the values it is given - level, star, skills, EX, potential, unique equipment and gear - instead of only maxing everything. Level is capped by the account level, star never drops below the character's own rarity (`star:base` resets to it), unique equipment is only valid at 5★, and gear tier follows the level band (T1 1-10 … T10 66-70).
- `!character clear [hard]` empties the roster: the default form keeps the starting students and resets them, `hard` keeps only the account's Student Rep.
- The Control Center gained a slider dialog for those values, presets for the old one-click actions, a database snapshot before every command with restore on the Updates page, and a second danger action for clearing characters.

Once that pull request lands, this fork is intended to be a plain mirror again.

### Rolling the Steam client back to 1.93

The server builds its game data against the 1.93 client, so a 1.96+ install will not get past the login. The rollback is an older depot manifest:

```
download_depot 3557620 3557621 6557220373208852788
```

That line runs in the **Steam console**. The console tab is hidden by default, so open it first:

- Press `Win+R`, type `steam://open/console`, press Enter.
- Or paste `steam://open/console` into a browser address bar and allow the browser to open Steam.

Either way Steam shows a **Console** tab next to your library; type the command there. `steam.exe -console` also works, but only when Steam is not already running — otherwise the flag is silently dropped. If you use `steamcmd` instead, log in with an account that owns the game: `download_depot` checks the app's licence, and Blue Archive's free-to-play licence lives on an account, so `+login anonymous` answers `missing license for depot (No subscription)`. Free to play still means you have to claim it, and Steam Guard makes the first login interactive — after that the same command works from either client.

- app `3557620`, depot `3557621`.
- `6557220373208852788` is the 27 Oct 2026 manifest, verified to unpack `1.93.453174`. The server's cache names `1.93.454564` — one patch inside the same 1.93 line.
- ~23 GB, unpacked into a **second** copy at `steamapps/content/app_3557620/depot_3557621/`. It leaves your install untouched until you copy it over.
- Verify before copying: `grep -aoE 'ba_live\.1\.9[0-9]\.[0-9]+' BlueArchive_Data/resources.assets` has to print the target version.
- Copy with `robocopy <depot> <install> /MIR /MT:8` (exit codes 0-7 are success), then stop Steam from repairing it back: with Steam closed, set `buildid` and `TargetBuildID` to the same value in `appmanifest_3557620.acf` and `BytesToDownload` to `0`.
- Launch through Steam's **Play** button. A direct `BlueArchive.exe` launch quits after ~3 s with `RestartRequired`, and Steam Offline Mode dies at the title screen with `70010006`.

Once the game is at the lobby, `steamapps/content/app_3557620/` can be deleted to reclaim the second copy.

Questions, bugs, support, or anything else: https://discord.gg/GANwPn9xX6

## Features

- Play without touching the official servers
- Pull on gacha banners with the real rates or custom rates, or set up whatever banner you want from the Control Center
- Replay the koyuki incident
- See hidden game notices
- Replay any old event and minigame
- Clear campaign stages: normal, hard, extra, sweeps, and strategy maps with a working enemy phase
- Decorate the cafe, save and load presets, and get rotating visitors to invite
- Claim daily/weekly missions, achievements and attendance rewards
- Craft, open item boxes and select tickets, and spend in the shops (AP, eligma, secret stones)
- Read the story at your own pace, or unlock all of it with one button
- Give yourself any student, item or currency through the admin panel, and send yourself mail
- Run as many accounts as you like from one install


## Installation

Grab Shittim Control Center from the [releases page](https://github.com/Neoexm/Shittim-Server/releases). It handles the whole setup: downloads the server, installs the .NET 10 SDK and mitmproxy if they're missing, and trusts the proxy's CA certificate. When the readiness card says everything is ok, press the start server button, wait for the server to start then launch Blue Archive from Steam.

The Control Center acts as the admin panel. accounts, inventory, mail, gacha, events, and all other features can be found there. The console also keeps itself, aswell as the server fully up to date

## Disclaimer

For educational and research purposes only. Not affiliated with Nexon.
