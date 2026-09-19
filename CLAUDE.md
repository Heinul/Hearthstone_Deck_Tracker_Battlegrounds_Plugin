# BgFree — HDT Battlegrounds plugin

Goal: replicate HDT Tier7 paid overlays (hero / trinket / quest pick stats, comp stats) using Firestone public data. Personal use only. Korean UI.

## Build / run
- Close HDT first (loaded DLL is locked). Then `dotnet build` → `%APPDATA%\HearthstoneDeckTracker\Plugins\BgFree\BgFree.dll`.
- HDT 1.57.12 at `%LOCALAPPDATA%\HearthstoneDeckTracker\app-1.57.12` (csproj `HdtDir`; bump after HDT auto-update).
- Enable once: HDT → Options → Plugins → BgFree. HDT log: `%APPDATA%\HearthstoneDeckTracker\Logs\hdt_log.txt`.
- Fixture: `fixtures/Power.log` (209 MB, 6 full BG games, gitignored). Regex checks run against it.

## Rules
- Commits / PRs / README: no AI attribution lines (no `Co-Authored-By: Claude`, no "Generated with Claude Code"). Conventional Commits, plain text.
- No new NuGet. Refs only: HearthstoneDeckTracker.exe, HearthDb, HearthMirror, Newtonsoft.Json (all `Private=false`).
- Code-only WPF, no XAML.
- `OnUpdate` runs every ~100 ms on the WPF UI thread. Never block it: HTTP/disk in `Task.Run`, touch `Core.OverlayCanvas` only from `OnUpdate`.
- Never throw out of `OnUpdate`: HDT auto-disables plugin after 100 exceptions. One try/catch in the tick; nullable everywhere; JSON keys guarded; on failure render "n/a", never blank crash.
- No memory reading beyond what HDT exposes. No hsreplay.net scraping.
- Card names: `HearthDb.Cards.GetFromDbfId(dbfId, true)?.GetLocName(Locale.koKR)`. HDT's `Config.Instance.SelectedLanguage` is enUS by default; don't rely on it.
- Show `dataPoints` next to every number; fall back to broader MMR bucket when thin.

## HDT API (verified by reflection 2026-09-19)
- `Hearthstone_Deck_Tracker.Plugins.IPlugin`: Name, Description, ButtonText, Author, Version, MenuItem, OnLoad/OnUnload/OnButtonPress/OnUpdate.
- `Hearthstone_Deck_Tracker.API.Core`: `Game` (GameV2), `OverlayCanvas` (Canvas), `OverlayWindow`, `MainWindow`.
- `GameV2`: `IsBattlegroundsMatch/SoloMatch/DuosMatch`, `IsBattlegroundsHeroPickingDone`, `IsBattlegroundsCombatPhase`, `BattlegroundsHeroPickState.OfferedHeroDbfIds` (int[]) / `.PickedHeroDbfId` (int?), `CurrentBattlegroundsRating` (int?), `PowerLog` (List<string>, cleared per game → poll with index cursor; reset when Count shrinks), `Entities` (Dictionary<int,Entity>), `GameEntity`, `Player`, `Opponent`, `CurrentGameStats`, `IsInMenu`, `IsRunning`, `GetBattlegroundsBoardStateFor(heroEntityId)`.
- `Hearthstone_Deck_Tracker.Hearthstone.BattlegroundsUtils` (static): `GetAvailableRaces()` (HashSet<Race>, memory, may be null/empty early → fall back to races of shop minions), `GetOriginalHeroId(cardId)`, `GetBattlegroundsAnomalyDbfId(gameEntity)`, `GetAvailableTiers(heroCardId)`.
- `Entity`: `Id, CardId, Name, Card, Tags, IsHero, IsMinion, IsInPlay, IsPlayer, GetTag(GameTag), HasTag, IsInZone(Zone), IsControlledBy(playerId)`.
- `OverlayWindow`: `BoardWidth/BoardHeight/CardWidth`, `BattlegroundsHeroPickingViewModel`, `BattlegroundsTrinketPickingViewModel` (HDT's own Tier7 VMs — inspect before writing own UI).
- `Hearthstone_Deck_Tracker.Utility.Extensions.OverlayExtensions.SetIsOverlayHitTestVisible(element, true)` — required for any clickable/hoverable overlay element (overlay window is click-through by default).
- `LogEvents.OnPowerLogLine` gets only `PowerTaskList.*` lines, NOT `GameState.*` → useless for choices; use `Core.Game.PowerLog`.
- `HearthDb.Cards`: `GetFromDbfId(int, bool)`, `AllByDbfId`, `BaconPoolMinionsByDbfId`, `CardIdToDbfId`. `Card`: `Id, DbfId, Name, Race, SecondaryRace, TechLevel, GetLocName(Locale)`.

## Data (Firestone CDN, hourly, unlicensed → personal use, 1 fetch/hour/file, cache to disk)
Base `https://static.zerotoheroes.com/api/bgs/`
- `hero-stats/mmr-{100|50|25|10|1}/{past-seven|last-patch|all-time}/overview-from-hourly.gz.json` — keys: heroCardId, averagePosition, conservativePositionEstimate, totalPicked/totalOffered, placementDistribution[], tribeStats[{tribe, impactAveragePosition}], mmrPercentiles[{percentile, mmr}]. Plain JSON despite .gz name.
- `duo/hero-stats/...` same shape for Duos.
- `trinket-stats/{time}/overview-from-hourly.gz.json` — trinketCardId (e.g. BG36_MagicItem_206, matches Power.log), pickRate, averagePlacement, averagePlacementAtMmr[].
- `quest-stats/mmr-{pct}/{time}/...` (empty while quests out of rotation), `comp-stats/{time}/...` (archetype English slugs), `card-stats/mmr-{pct}/{time}/...`.
- Bracket = smallest mmrPercentiles entry with mmr <= CurrentBattlegroundsRating. Hero skins → parent via `GetOriginalHeroId`.
- Cards JSON (if ever needed outside HearthDb): `https://api.hearthstonejson.com/v1/latest/koKR/cards.json`.

## Power.log facts (from fixture)
- Trinket offer: `GameState.DebugPrintEntityChoices() ... ChoiceType=GENERAL`, Source cardId `BG30_Trinket_1st|2nd`, entities `cardId=BG(30|36)_MagicItem_N`. Never pin one set prefix; match `_MagicItem_`.
- Hero offer: `ChoiceType=MULLIGAN`, 4 entities, hero ids `TB_BaconShop_HERO_*` or `BG\d+_HERO_*`, often `_SKIN_` variants. Reroll active this season (`BACON_MULLIGAN_HERO_REROLL_ACTIVE`) → OfferedHeroDbfIds changes; re-diff.
- Tags may print numeric (`tag=3533`); phase = `BOARD_VISUAL_STATE` (1 shop, 2 combat) + `BACON_IN_COMBAT_PHASE`.
- Not in fixture (unverified shapes): Duos, quests, anomalies, reconnect.

## Overlay strategy (decided M1)
- Zero own UI: push data into HDT's own hero-picking overlay. `Core.OverlayWindow.BattlegroundsHeroPickingViewModel.SetHeroStats(IEnumerable<HSReplay.Responses.BattlegroundsHeroPickStats.BattlegroundsSingleHeroPickStats>, null, null, false)`; DTO has public ctor + setters (HeroDbfId, Tier string "s|a|b|c|d|f", AvgPlacement, PickRate 0-100, PlacementDistribution double[8] percentages). Order must match `OfferedHeroDbfIds` (zone order). Null entries allowed (empty header).
- VM.Visibility = HeroStats != null. Control is hosted unconditionally in OverlayWindow.xaml; not gated by Tier7 config. `StatsVisibility` follows `Config.ShowBattlegroundsHeroPicking` (true).
- HDT calls `Reset()` on turn start, mulligan done, game end, and its Tier7 path calls `ShowDisabledMessage()` ~500 ms after mulligan start (overwrites `Message.Text`). Plugin re-applies when `HeroStats == null` or offered set changes, and re-sets `Message.Text` each tick.
- Same pattern likely works for `BattlegroundsTrinketPickingViewModel` / `BattlegroundsQuestPickingViewModel` (M3): reflect their SetXxx signatures first.
- Cache dir: `%APPDATA%\HearthstoneDeckTracker\BgFree\cache` (outside Plugins/, which HDT syncs on start).
- Tier letters are OWN bands on averagePosition (s ≤3.8, a ≤4.1, b ≤4.4, c ≤4.7, d ≤5.0, f). Not HSReplay's.

## Milestones
- M0 done 2026-09-19: csproj + empty Plugin builds, DLL in plugins dir.
- M1 built 2026-09-19 (needs in-game confirmation): Stats.cs (Firestone hero-stats mmr-100/past-seven, 1 h disk cache, stale fallback) + Plugin.cs tick → HDT hero-picking VM. Self-check button: fixture MULLIGAN blocks → 24/24 heroes resolve (verified offline with python: all 24 fixture ids are Firestone keys). Duos skipped (solo endpoint only).
- M2 built 2026-09-19 (needs in-game confirmation): per-(solo|duo, mmr-{100|50|25|10|1}) files, bucket from `CurrentBattlegroundsRating` vs `mmrPercentiles` (all-MMR shown while bucket loads; hero rows with <100 dataPoints in a narrow bucket fall back to all-MMR); tribe adjustment = avg + Σ `tribeStats.impactAveragePosition` for `BattlegroundsUtils.GetAvailableRaces()` (Race int == Firestone tribe), labelled "(추정)"; Duos endpoint; tier bands map duos 1-4 onto 1-8. Self-check now prints buckets, rating→bucket, tribes.
- M3 built 2026-09-19 (needs in-game confirmation): trinket pick stats → `OverlayWindow.BattlegroundsTrinketPickingViewModel.SetTrinketStats` (offer = `Core.Game.Player.OfferedEntityIds` whose entities are all `IsBattlegroundsTrinket`; 4 offered per choice; `ChoicesVisible` normally set by HDT's memory watcher, we set it true on apply; VM does NOT accept null entries → pass blank DTO). Comp stats → `OverlayWindow.BattlegroundsSessionViewModelVM.SetBattlegroundsCompositionStatsViewModel(List<LobbyComp>)` + force `AvailableCompStatsSectionVisibility/CompStatsBodyVisibility` Visible each tick (HDT's `UpdateCompositionStatsVisibility` collapses it when Tier7 overlay is off; solo only). Loader<T> = background TTL refresh, never blocks OnUpdate. Quests skipped: no HSReplay DTO in HDT 1.57.12 and Firestone quest file empty (out of rotation). Fixture: 11 trinket offers / 44 ids all resolve.
- Firestone comp-stats file is 47 MB (6 h TTL) and includes `heroStats[].finalBoards[].finalComp.board[].cardID` — real first-place boards per archetype+hero. Key minions = top-3 most frequent cards (golden `_G` stripped). Archetype slug prefix → Race int; Korean labels hand-written in MoreStats.cs.
- M4 built 2026-09-19 (needs in-game confirmation): example first-place boards into HDT's Inspiration panel. `CompStats` now keeps every board (11.5k boards, minion tags → Attack/Health/Premium/DivineShield/Taunt/Poisonous/Venomous/Reborn/Deathrattle/Windfury/ZonePos). `BoardsFor(hero, tribes)`: this hero's boards (highest MMR first) then other heroes' from popular playable comps, 20 max, 4 per page. Injection: `BattlegroundsInspirationGameViewModel(InspirationApiResponse.ResponseData.Game)` public; `Games`/`Pages` setters are PRIVATE → `GetSetMethod(true)` reflection (fails silently if HDT renames). `ZonePosition` must be a boxed `long`. Hero power = HearthDb hero card `Entity.GetTag(HERO_POWER)`. Shown once per game via public `OverlayWindow.ShowBgsInspiration()` at the first shopping turn (any click closes); the minion-browser Inspiration button (`BtnTier7Inspiration`, internal field via reflection) is re-enabled each tick to reopen. Never call `SetKeyMinion` / enable `BattlegroundsMinionsVM.IsInspirationEnabled`: HDT would spend the user's free Tier7 trials on hsreplay.net.
- M5 built 2026-09-19 (needs in-game confirmation): (a) 내 성적 line under the hero-pick message from `BattlegroundsLastGames.Instance.Games` (public nested `GameItem`: Hero cardId (may be a skin → `GetOriginalHeroId`), Placement, Duos, FriendlyGame; never pruned; not filtered by account). (b) Shop hints: own `Canvas` on `Core.OverlayCanvas`, one label per tavern minion = Firestone card-stats delta (avg placement when NOT bought − when bought, turn row if ≥50 games else overall marked `*`), positioned from HDT's `OppBoardItemsControl` item containers via `TransformToAncestor(Core.OverlayCanvas)` (internal field → reflection). Tavern minions = `Core.Game.Opponent.Board.Where(IsMinion)` by ZONE_POSITION; BG round = (GameEntity TURN + 1) / 2; golden → normal via `Cards.TripleToNormalDbfIds`. (c) Tavern pinning panel forced on via public `OverlayWindow.BgsMinionPinningVisibility` (pinning VM has no network/trial calls).
- In-game confirmed 2026-09-19 (Duos game): shop hint labels, tavern pinning panel, inspiration boards applied (log). Fixes: card-stats `turnStats[].turn` can be null (nullable); Duos detected from player entities' `BACON_DUO_TEAM_ID` (HDT's mirror read can lag). Firestone has NO duo files for comp/trinket/card/quest (403) — only `duo/hero-stats`; in Duos the plugin shows solo trinket/card/board data labelled "솔로 데이터" and skips comps (HDT hides that section in Duos anyway). Every apply logs `BgFree: ... applied (key)` to hdt_log.txt for diagnosis.
- Comp-aware shop hints (2026-09-19 evening): `CompStats.Comp.CardFreq` = share of the comp's first-place boards containing each card; `Infer(myBoardIds, tribes)` = comps scored by mean freq of my minions (≥0.12, within 60% of best, top 2; needs ≥3 minions on my board). Shop label gets ` ★` when max fit ≥ 0.25 and a `✕ ` prefix in silver when fit < 0.03 (strong card, wrong comp). No opacity dimming: 40% was invisible on the dark board (user feedback). Labels 18px·scale, header 15px, near-opaque background with thin border. Gold header "예상 조합: …" left of the tavern row. Verified offline: undead top-3 board → undead_butcher 90%; elemental cards fit 0 there.
- Remaining ideas: quests when back in rotation; timewarp flag.
