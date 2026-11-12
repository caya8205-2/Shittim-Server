# Shittim Server

A private server for Blue Archive's Steam release, written in C# on ASP.NET Core (.NET 10). Progress lives in a local SQLite database, and it's far enough along that the game is just playable: log in, pull, clear stages, decorate the cafe.

## About this fork

This repository is `caya8205-2/Shittim-Server`, tracking [Neoexm/Shittim-Server](https://github.com/Neoexm/Shittim-Server) with runtime fixes merged on top.

**"Stable" here describes the runtime, not the game version.** Of the builds available at the time of writing, this is the one whose ASP.NET server and mitmproxy redirect keep running through a session: no preset packets dying in a handler exception, no squad screen that stops taking input, no proxy that has to be restarted mid-session. It does **not** mean "ships the newest Blue Archive build on Steam". The server compiles its Excel data against the 1.93 client line, so a 1.96 install has to be rolled back before it can log in. Read "latest" as *latest stable runtime*, not *latest game version*.

### Included here and not yet upstream

- Squad presets work end to end: creating a preset, importing a formation into one, renaming a group, and switching between groups and the `Recent` tab. Upstream, `Echelon_PresetSave` throws on a slot that does not exist yet, and the preset packets drop a `0`-valued `GroupIndex`/`Index`, which leaves a card titled `Unit {0}` with placeholder units and marks every tab as selected at once. Sent upstream as [#50](https://github.com/Neoexm/Shittim-Server/pull/50).

Once that pull request lands, this fork is intended to be a plain mirror again.

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
