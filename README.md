# KRLZ — Discord Community Ecosystem, Party-Game Engine & Web Panel (.NET 10)

I built **KRLZ** as a full-featured community automation platform, virtual economy, multiplayer party-game engine, and web administration dashboard in **C# 13 / .NET 10**.

Instead of cramming everything into a single monolith, I split the solution (`KRLZ.sln`, ~34.8k LOC) into three projects so the database layer and domain models stay shared between the Discord worker and the ASP.NET Core web panel:

```text
KRLZ.sln
├── src/Bot.Core/        # Shared entities, 19 Dapper repositories, SQLite connection factory & 5 SQL migrations
├── src/Bot/             # .NET 10 Worker Service, 23 Discord interaction modules (177 slash commands) & SkiaSharp renderer
└── src/Bot.Web/         # ASP.NET Core Web API (30 endpoints + Discord OAuth2) & single-page admin dashboard
```

---

## What's Inside & How I Built It

### 1. Data Layer & Live SQLite Backups (`src/Bot.Core/Data`)
* **Embedded SQL Migrations (`DbMigrator.cs`)**: On startup, the bot automatically checks and applies 5 versioned SQL migration scripts (`001_init.sql` through `005_party_games.sql`), creating and indexing **63 SQLite tables**.
* **19 Async Dapper Repositories**: For high-frequency interactions (economy transactions, auction bids, casino rounds, voice sessions), I used **Dapper** over `Microsoft.Data.Sqlite` in shared WAL cache mode with custom type handlers (`DapperTypeHandlers.cs`).
* **Hot Zero-Downtime Backups (`DatabaseBackupService.cs`)**: Runs live database snapshots using SQLite's `VACUUM INTO` (`krlz-db-YYYYMMDD-HHMMSS.sqlite`) on a background schedule and via `/dbbackup now`, rotating old backups automatically without locking active connections.

### 2. Server-Side 2D Graphics with SkiaSharp (`ProfileCardService.cs`)
* Rather than using slow headless browsers to render images, I wrote a 59 KB **SkiaSharp** compositor that draws PNG cards directly in memory:
  * **Profile & Rank Cards (`/profile`, `/rank`)**: Draws user avatars, XP progress bars, reputation stats, marriage/clan/family badges, and custom accents over 7 theme backgrounds (`crimson`, `cyberpunk`, `emerald`, `nebula`, `solar`, `void_gold`, `default`).
  * **Visual Leaderboards & Clan Cards (`/balance`, `/clan info`, `/top`)**: Renders clean tabular rankings using bundled `Inter-Bold` and `Inter-Regular` fonts.

### 3. Multiplayer Party-Games Engine (`PartyGameService.cs` & `PartyGamesModule.cs`)
One of my favorite parts of this project (~242 KB of code across the service and module). I implemented **7 stateful multiplayer games** that run entirely inside Discord via buttons, select menus, modals, and ephemeral messages, persisted in `party_game_sessions` and `party_game_players`:
* **`/mafia`** — Full Day/Night state machine with hidden role distribution, night actions (Mafia, Doctor, Detective), and town voting.
* **`/bunker`** — Post-apocalyptic survival game with generated catastrophe conditions, character attribute cards (profession, health, hobby, phobia, baggage), phased reveals, and voting.
* **`/spyfall`** — Hidden-location social deduction where players question each other to catch the spy before the spy guesses the location.
* **`/codenames`** — 5×5 word-grid team game with ephemeral Spymaster key maps and turn-based guessing.
* **`/liarsbar`** — Bluffing card game with call-out mechanics and elimination rounds.
* **`/quiz` & `/raid`** — Multiplayer trivia rounds and co-op Raid Boss fights (`raid_boss_sessions`, `raid_boss_hits`) with live damage leaderboards.

### 4. Economy, Banking, Crafting, Auctions & Mini-Games
* **Banking & Currency (`/bank`, `/pay`, `/daily`, `/work`)**: Bank accounts (`bank_accounts`), transfers, and full transaction logs (`transactions`).
* **Fishing QTE, Crafting & P2P Trade Escrows (`FishingAndItemsModule.cs`)**: Interactive fishing mini-game (`/fish`), recipe crafting (`/craft`), and safe player-to-player trade offers (`PendingTradeOffer`, `/trade`).
* **Auction House & Custom Role Exchange (`AuctionModule.cs`)**: Players can list inventory items or custom roles for bidding or instant buyout (`/auction create`, `/auction bid`, `/auction buyout`, `/auction cancel`).
* **Casino & PvP (`CasinoModule.cs`, `PvpAndMinesModule.cs`)**: Blackjack (`BlackjackCardDto`), 30-second multiplayer Roulette tables (`RouletteTableRound`), live multiplier Crash (`/crash` with real-time Cash-Out), weighted Jackpot pools (`JackpotPoolRound`), 5×5 Minesweeper (`/mines`), and 1v1 PvP Duels (`/duel`).

### 5. Clans, Social Graph, BattlePass & Anti-Raid Security
* **Clans & Clan Wars (`ClanModule.cs`)**: Clan creation with dedicated roles/channels, officer hierarchy, shared clan treasury (`/clan deposit`, `/clan upgrade`), and clan wars (`/war declare`).
* **Families, Marriages & Pets (`SocialExpansionModule.cs`)**: Marriages (`/marry`), family trees (`/family`), reputation (`/rep`), and virtual pets with expeditions (`/pet adopt`, `feed`, `expedition`, `claim`).
* **BattlePass & Quests (`ProgressionModule.cs`)**: Daily/weekly quests (`quests`, `user_quests`), tiered seasonal rewards (`battlepass_seasons`), and achievements.
* **Security, AutoMod & Join-to-Create Voice (`SecurityModule.cs`, `PrivateVoiceService.cs`)**: Whitelist guards, guild structure backups (`/backup create`, `/backup restore`), `/security lockdown` and `/security panic` modes, word filters, anti-spam (`AntispamService.cs`), and dynamic personal voice rooms (`/voice-panel`).

### 6. ASP.NET Core Web Dashboard (`src/Bot.Web`)
* Exposes **30 REST API routes** in `Bot.Web/Program.cs` with Discord OAuth2 login (`/auth/discord`, `/auth/callback`) and a single-page frontend (`wwwroot/index.html`) to inspect server telemetry (`/api/overview`), edit user balances/inventory (`/api/users/{id}/modify`), manage clans, shop items, quests, tickets, and party-game word packs (`/api/party-games/content`).

---

## Running Locally

1. **Clone the repo:**
   ```bash
   git clone https://github.com/Milyka/KRLZ-Discord-Ecosystem.git
   cd KRLZ-Discord-Ecosystem
   ```
2. **Set up `settings.json`:**
   Copy `settings.example.json` to `settings.json` (and `src/Bot/settings.json`) and add your bot token:
   ```json
   {
     "Token": "YOUR_DISCORD_BOT_TOKEN_HERE",
     "GuildId": 0,
     "DatabaseConnectionString": "Data Source=krlz.db;Cache=Shared"
   }
   ```
3. **Start the bot worker:**
   ```bash
   dotnet build KRLZ.sln -c Release
   dotnet run --project src/Bot/Bot.csproj
   ```
4. **Start the web panel (optional):**
   ```bash
   dotnet run --project src/Bot.Web/Bot.Web.csproj
   ```
