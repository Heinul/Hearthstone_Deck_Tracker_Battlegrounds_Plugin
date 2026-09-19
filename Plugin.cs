using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using HearthDb.Enums;
using Hearthstone_Deck_Tracker.API;
using Hearthstone_Deck_Tracker.Controls.Overlay.Battlegrounds.Inspiration;
using Hearthstone_Deck_Tracker.Hearthstone;
using Hearthstone_Deck_Tracker.Hearthstone.Entities;
using Hearthstone_Deck_Tracker.Plugins;
using Hearthstone_Deck_Tracker.Utility.Logging;
using HSReplay.Responses;

namespace BgFree;

public class Plugin : IPlugin
{
    public string Name => "BgFree";
    public string Description => "Battlegrounds hero / trinket / comp stats from Firestone public data (personal Tier7 replacement)";
    public string ButtonText => "Self-check";
    public string Author => "Heinul";
    public Version Version => new(0, 4, 1);
    public MenuItem MenuItem => null!;

    // Self-update: HDT has no plugin updater. On load, compare the latest GitHub release tag with Version; if newer, drop
    // its BgFree.dll into %AppData%\HearthstoneDeckTracker\Plugins\BgFree\ (not locked; HDT copies it in on next start).
    const string Repo = "Heinul/Hearthstone_Deck_Tracker_Battlegrounds_Plugin";

    async Task CheckForUpdateAsync()
    {
        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("BgFree-updater");
            var release = Newtonsoft.Json.Linq.JObject.Parse(await http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest"));
            var tag = (release["tag_name"]?.ToString() ?? "").TrimStart('v', 'V');
            if (!Version.TryParse(tag, out var latest) || latest <= Version) { Log.Info($"BgFree: up to date (latest {tag})"); return; }
            var url = release["assets"]?.FirstOrDefault(a => a["name"]?.ToString() == "BgFree.dll")?["browser_download_url"]?.ToString();
            if (url == null) { Log.Info($"BgFree: release {tag} has no BgFree.dll asset"); return; }
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HearthstoneDeckTracker", "Plugins", "BgFree");
            Directory.CreateDirectory(dir);
            var tmp = Path.Combine(dir, "BgFree.dll.new");
            File.WriteAllBytes(tmp, await http.GetByteArrayAsync(url));
            File.Copy(tmp, Path.Combine(dir, "BgFree.dll"), true);
            File.Delete(tmp);
            Log.Info($"BgFree: downloaded {tag}; applied on next HDT start");
            Application.Current.Dispatcher.Invoke(() => MessageBox.Show($"BgFree {tag} 다운로드 완료. HDT를 다시 시작하면 적용됩니다.", "BgFree 업데이트"));
        }
        catch (Exception e) { Log.Error($"BgFree: update check failed: {e.Message}"); }
    }

    const int ThinDataPoints = 100;   // below this in a narrow MMR bucket, fall back to the all-MMR row

    readonly Dictionary<(bool duos, int pct), Loader<HeroStats>> _heroes = new();
    readonly Loader<TrinketStats> _trinkets = new("trinket-stats", TimeSpan.FromHours(1), TrinketStats.LoadAsync);
    readonly Loader<CompStats> _comps = new("comp-stats", TimeSpan.FromHours(6), CompStats.LoadAsync);
    string _heroApplied = "", _heroMessage = "", _trinketApplied = "", _trinketMessage = "", _compApplied = "";
    int _errors;

    public void OnLoad()
    {
        Heroes(false, 100);
        _trinkets.Get();
        _comps.Get();
        Task.Run(CheckForUpdateAsync);
    }

    public void OnUnload() { }

    // Runs every ~100 ms on HDT's UI thread. Never throw: HDT disables the plugin after 100 exceptions.
    public void OnUpdate()
    {
        try { Tick(); }
        catch (Exception e) { if (++_errors <= 5) Log.Error($"BgFree: {e}"); }
    }

    void Tick()
    {
        var game = Core.Game;
        if (!game.IsBattlegroundsMatch || game.IsInMenu)
        {
            _heroApplied = _trinketApplied = _compApplied = _inspApplied = "";
            _inspShownThisGame = false;
            return;
        }
        // HDT's game-type read can be briefly wrong at game start; player entities carry BACON_DUO_TEAM_ID only in Duos.
        var players = game.Entities.Values.Where(e => e.IsPlayer).ToList();
        var duos = players.Count > 0 ? players.Any(e => e.GetTag(GameTag.BACON_DUO_TEAM_ID) > 0) : game.IsBattlegroundsDuosMatch;
        var all = Heroes(duos, 100);
        var pct = all == null ? 100 : HeroStats.Bucket(game.CurrentBattlegroundsRating, all.MmrPercentiles);
        var tribes = Tribes();

        if (!game.IsBattlegroundsHeroPickingDone)
        {
            if (all != null) HeroTick(game, duos, all, pct, tribes);
            return;
        }
        // Firestone publishes duo files for hero stats only; trinket / card / comp / boards below are solo data, labelled so in Duos.
        TrinketTick(game, pct, duos);
        if (!duos) CompTick(pct, tribes);   // HDT hides the comp section in Duos too
        InspirationTick(game, tribes, duos);
        ShopTick(game, pct, tribes);
    }

    // Per-card hint above each tavern minion: placement gain from buying it this turn (Firestone card-stats).
    // Labels sit on our own canvas, positioned from HDT's opponent-board slot containers (internal field -> reflection).
    static readonly System.Reflection.FieldInfo? OppBoardItems = typeof(Hearthstone_Deck_Tracker.Windows.OverlayWindow).GetField("OppBoardItemsControl", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
    readonly Dictionary<int, Loader<CardStats>> _cards = new();
    readonly System.Windows.Controls.Canvas _shopPanel = new() { IsHitTestVisible = false };
    readonly List<Border> _shopLabels = new();
    readonly Border _shopHeader = MakeLabel();   // "예상 조합: …" to the left of the tavern row
    readonly Border _levelLabel = MakeLabel();   // level-up hint under the tavern upgrade button
    string _shopApplied = "", _levelApplied = "";

    static string NormalCardId(Entity e)
    {
        var dbf = HearthDb.Cards.TripleToNormalDbfIds.TryGetValue(e.Card.DbfId, out var n) ? n : e.Card.DbfId;
        return HearthDb.Cards.DbfIdToCardId.TryGetValue(dbf, out var cid) ? cid : e.CardId ?? "";
    }

    void ShopTick(GameV2 game, int pct, List<int> tribes)
    {
        var overlay = Core.OverlayWindow;
        var shopping = !game.IsBattlegroundsCombatPhase;
        if (shopping && Hearthstone_Deck_Tracker.Config.Instance.ShowBattlegroundsBrowser && overlay.BgsMinionPinningVisibility != Visibility.Visible)
            overlay.BgsMinionPinningVisibility = Visibility.Visible;   // HDT's tavern pinning: client-side only, Tier7-gated by HDT

        if (_shopPanel.Parent == null) { Core.OverlayCanvas.Children.Add(_shopPanel); System.Windows.Controls.Panel.SetZIndex(_shopPanel, 100); _shopPanel.Children.Add(_shopHeader); _shopPanel.Children.Add(_levelLabel); }
        if (!_cards.TryGetValue(pct, out var loader))
            _cards[pct] = loader = new Loader<CardStats>($"card-stats mmr-{pct}", TimeSpan.FromHours(1), () => CardStats.LoadAsync(pct));
        var stats = loader.Get();
        var items = OppBoardItems?.GetValue(overlay) as System.Windows.Controls.ItemsControl;
        if (!shopping || stats == null || items == null) { _shopPanel.Visibility = Visibility.Collapsed; _shopApplied = ""; return; }

        var minions = game.Opponent.Board.Where(x => x.IsMinion).OrderBy(x => x.GetTag(GameTag.ZONE_POSITION)).ToList();
        var turn = ((game.GameEntity?.GetTag(GameTag.TURN) ?? 0) + 1) / 2;
        var mine = game.Player.Board.Where(x => x.IsMinion).Select(NormalCardId).Where(id => id != "").ToList();
        var key = $"{pct}|{turn}|{string.Join(",", minions.Select(m => m.Id))}|{string.Join(",", mine)}";
        if (key != _shopApplied)
        {
            // Comp fit: which comps my board resembles (needs 3+ minions), and how often each shop card sits on their winning boards.
            var comps = _comps.Get();
            var inferred = comps != null && mine.Count >= 3 ? comps.Infer(mine, tribes) : new List<(CompStats.Comp comp, double score)>();
            var header = (System.Windows.Controls.TextBlock)_shopHeader.Child;
            header.Text = inferred.Count > 0 ? "예상 조합: " + string.Join(" / ", inferred.Select(x => $"{CompStats.Label(x.comp.Archetype)} {x.score:0%}")) : "";
            header.Foreground = System.Windows.Media.Brushes.Gold;
            _shopHeader.Visibility = inferred.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            while (_shopLabels.Count < minions.Count) { var b = MakeLabel(); _shopLabels.Add(b); _shopPanel.Children.Add(b); }
            for (var i = 0; i < _shopLabels.Count; i++)
            {
                var label = _shopLabels[i];
                if (i >= minions.Count) { label.Visibility = Visibility.Collapsed; continue; }
                var cardId = NormalCardId(minions[i]);
                var d = stats.Delta(cardId, turn);
                var text = (System.Windows.Controls.TextBlock)label.Child;
                if (d is { } v)
                {
                    var fit = inferred.Count > 0 ? inferred.Max(x => x.comp.Fit(cardId)) : -1;   // -1 = no inference yet
                    var offComp = fit >= 0 && fit < 0.03;   // strong card maybe, but absent from my comp's winning boards
                    text.Text = $"{(offComp ? "✕ " : "")}{(v.delta >= 0 ? "+" : "")}{v.delta:0.0}{(v.turnSpecific ? "" : "*")}{(fit >= 0.25 ? " ★" : "")}";
                    text.Foreground = offComp ? System.Windows.Media.Brushes.Silver
                                    : v.delta >= 0.3 ? System.Windows.Media.Brushes.LimeGreen : v.delta >= 0.1 ? System.Windows.Media.Brushes.PaleGreen
                                    : v.delta > -0.1 ? System.Windows.Media.Brushes.LightGray : v.delta > -0.3 ? System.Windows.Media.Brushes.Orange : System.Windows.Media.Brushes.OrangeRed;
                    label.Visibility = Visibility.Visible;
                }
                else label.Visibility = Visibility.Collapsed;
            }
            _shopApplied = key;
            Log.Info($"BgFree: shop hints applied ({key}) labels={_shopLabels.Count(l => l.Visibility == Visibility.Visible)} comp={header.Text}");
        }
        // Follow HDT's slot containers every tick (window moves / resizes). Slot i == i-th tavern minion in zone order.
        var scale = overlay.Height / 1080.0;
        var shown = false;
        Point? first = null;
        for (var i = 0; i < minions.Count && i < _shopLabels.Count; i++)
        {
            var label = _shopLabels[i];
            if (items.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement slot || !slot.IsVisible || slot.ActualWidth == 0) { label.Visibility = Visibility.Hidden; continue; }
            Point p;
            try { p = slot.TransformToAncestor(Core.OverlayCanvas).Transform(new Point(0, 0)); }
            catch { label.Visibility = Visibility.Hidden; continue; }
            first ??= p;
            if (label.Visibility != Visibility.Visible) continue;
            ((System.Windows.Controls.TextBlock)label.Child).FontSize = 18 * scale;
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            System.Windows.Controls.Canvas.SetLeft(label, p.X + slot.ActualWidth / 2 - label.DesiredSize.Width / 2);
            System.Windows.Controls.Canvas.SetTop(label, p.Y - label.DesiredSize.Height - 2 * scale);
            shown = true;
        }
        if (_shopHeader.Visibility == Visibility.Visible && first is Point f)
        {
            ((System.Windows.Controls.TextBlock)_shopHeader.Child).FontSize = 15 * scale;
            _shopHeader.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            System.Windows.Controls.Canvas.SetLeft(_shopHeader, f.X - _shopHeader.DesiredSize.Width - 10 * scale);
            System.Windows.Controls.Canvas.SetTop(_shopHeader, f.Y - _shopHeader.DesiredSize.Height - 2 * scale);
            shown = true;
        }
        // Level-up hint: next turn, does buying (tier+1) minions beat buying current-tier minions? Proxy for upgrade timing.
        var cur = game.Player.Hero?.GetTag(GameTag.PLAYER_TECH_LEVEL) ?? 0;
        var levelKey = $"{pct}|{turn}|{cur}";
        if (levelKey != _levelApplied)
        {
            var text = (System.Windows.Controls.TextBlock)_levelLabel.Child;
            var up = cur is >= 1 and < 6 ? stats.TierDelta(turn + 1, cur + 1) : null;
            var stay = cur >= 1 ? stats.TierDelta(turn + 1, cur) : null;
            if (up is double u && stay is double s)
            {
                var diff = u - s;
                text.Text = $"레벨업 {(diff >= 0.15 ? "▲" : diff <= -0.15 ? "▼" : "≈")} {diff:+0.0;-0.0}";
                text.Foreground = diff >= 0.15 ? System.Windows.Media.Brushes.LimeGreen : diff <= -0.15 ? System.Windows.Media.Brushes.Orange : System.Windows.Media.Brushes.LightGray;
                _levelLabel.Visibility = Visibility.Visible;
            }
            else _levelLabel.Visibility = Visibility.Collapsed;
            _levelApplied = levelKey;
            Log.Info($"BgFree: level hint ({levelKey}) up={up?.ToString("0.00") ?? "-"} stay={stay?.ToString("0.00") ?? "-"}");
        }
        if (_levelLabel.Visibility == Visibility.Visible)
        {
            ((System.Windows.Controls.TextBlock)_levelLabel.Child).FontSize = 14 * scale;
            _levelLabel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            System.Windows.Controls.Canvas.SetLeft(_levelLabel, overlay.Width * 0.393 - _levelLabel.DesiredSize.Width);   // left of the upgrade button (16:9 layout)
            System.Windows.Controls.Canvas.SetTop(_levelLabel, overlay.Height * 0.176 - _levelLabel.DesiredSize.Height / 2);
            shown = true;
        }
        _shopPanel.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
    }

    static Border MakeLabel() => new()
    {
        Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(235, 15, 15, 15)),
        BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(160, 255, 255, 255)),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(6, 1, 6, 1),
        Child = new System.Windows.Controls.TextBlock { FontWeight = FontWeights.Bold, Foreground = System.Windows.Media.Brushes.White },
    };

    // Example first-place boards into HDT's Inspiration panel. Games/Pages have private setters -> reflection (HDT 1.57.12).
    static readonly System.Reflection.MethodInfo? SetGames = typeof(BattlegroundsInspirationViewModel).GetProperty("Games")?.GetSetMethod(true);
    static readonly System.Reflection.MethodInfo? SetPages = typeof(BattlegroundsInspirationViewModel).GetProperty("Pages", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetSetMethod(true);
    static readonly System.Reflection.FieldInfo? BtnInspiration = typeof(Hearthstone_Deck_Tracker.Windows.OverlayWindow).GetField("BtnTier7Inspiration", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
    string _inspApplied = "";
    bool _inspShownThisGame;

    void InspirationTick(GameV2 game, List<int> tribes, bool duos)
    {
        if (SetGames == null || SetPages == null) return;   // HDT changed; feature silently off
        var stats = _comps.Get();
        if (stats == null) return;
        var heroId = game.Player.Hero?.CardId is string id ? BattlegroundsUtils.GetOriginalHeroId(id) : null;
        var vm = Core.OverlayWindow.BattlegroundsInspirationViewModel;
        var key = $"{heroId}|{string.Join("/", tribes)}";
        if (key != _inspApplied)
        {
            var games = stats.BoardsFor(heroId, tribes).Select(x => new BattlegroundsInspirationGameViewModel(ToGame(x.board))).ToList();
            SetGames.Invoke(vm, new object?[] { games });
            SetPages.Invoke(vm, new object?[] { Enumerable.Range(1, Math.Max(1, (games.Count + 3) / 4)).ToList() });
            vm.Page = 1;
            vm.IsLoadingData = false;
            vm.MmrPercentile = 100;
            var heroName = heroId != null && HearthDb.Cards.All.TryGetValue(heroId, out var hc) ? hc.GetLocName(Locale.koKR) : "";
            vm.TitleText = $"{heroName} 1등 최종 보드 예시 (Firestone, 최근 7일{(duos ? ", 솔로 데이터" : "")})";
            _inspApplied = key;
            Log.Info($"BgFree: inspiration boards applied ({key}) games={games.Count}");
        }
        if (!_inspShownThisGame && !game.IsBattlegroundsCombatPhase && heroId != null)
        {
            Core.OverlayWindow.ShowBgsInspiration();   // once per game, at the first shopping turn; any click closes it
            _inspShownThisGame = true;
        }
        if (BtnInspiration?.GetValue(Core.OverlayWindow) is FrameworkElement btn && !btn.IsEnabled)
            btn.IsEnabled = true;   // HDT disables it (no Tier7 request made); it just re-opens the panel
    }

    static InspirationApiResponse.ResponseData.Game ToGame(CompStats.Board b)
    {
        HearthDb.Cards.All.TryGetValue(b.HeroCardId, out var hero);
        return new InspirationApiResponse.ResponseData.Game
        {
            HeroDbfId = hero?.DbfId ?? 0,
            HeroPower = hero?.Entity?.GetTag(GameTag.HERO_POWER) ?? 0,
            FinalMinions = b.Minions.OrderBy(m => m.ZonePos).Select(m => new InspirationApiResponse.ResponseData.Game.Minion
            {
                MinionDbfId = HearthDb.Cards.All.TryGetValue(m.CardId, out var c) ? c.DbfId : 0,
                ZonePosition = (long)m.ZonePos,   // HDT keeps only entries whose ZonePosition is a boxed long
                Attack = m.Attack, Health = m.Health, Premium = m.Premium,
                DivineShield = m.DivineShield, Taunt = m.Taunt, Poisonous = m.Poisonous, Venomous = m.Venomous,
                Reborn = m.Reborn, Deathrattle = m.Deathrattle, Windfury = m.Windfury,
            }).Where(m => m.MinionDbfId != 0).ToArray(),
        };
    }

    void HeroTick(GameV2 game, bool duos, HeroStats all, int pct, List<int> tribes)
    {
        var offered = game.BattlegroundsHeroPickState.OfferedHeroDbfIds;
        if (offered == null || offered.Length == 0) return;
        var set = pct == 100 ? all : Heroes(duos, pct) ?? all;   // bucket still loading -> show all-MMR meanwhile

        var vm = Core.OverlayWindow.BattlegroundsHeroPickingViewModel;
        var key = $"{duos}|{set.Percentile}|{string.Join("/", tribes)}|{string.Join(",", offered)}";
        if (key != _heroApplied || vm.HeroStats == null)   // new offer / reroll / tribes or bucket arrived, or HDT Reset() wiped it
        {
            vm.SetHeroStats(offered.Select(id => PickHero(set, all, id, tribes)!), null, null, false);
            var floor = all.MmrPercentiles.FirstOrDefault(p => p.Percentile == set.Percentile)?.Mmr;
            _heroMessage = $"Firestone · {(duos ? "듀오" : "솔로")} · "
                         + (set.Percentile == 100 ? "전체 MMR" : $"상위 {set.Percentile}% (MMR {floor}+)")
                         + (tribes.Count > 0 ? " · 종족 반영(추정)" : " · 종족 미반영")
                         + $" · 최근 7일 · {set.DataPoints / 1000}k판 · {set.LastUpdate.ToLocalTime():MM-dd HH:mm} 갱신"
                         + "\n" + MyRecord(offered, duos);
            _heroApplied = key;
            Log.Info($"BgFree: hero stats applied ({key})");
        }
        if (vm.Message.Text != _heroMessage)   // HDT's own Tier7 path overwrites this with "disabled" once
            vm.Message.Text = _heroMessage;
    }

    // My own placements per offered hero, from HDT's free game history (all games since HDT was installed; all accounts on this PC).
    static string MyRecord(int[] offered, bool duos)
    {
        var games = Hearthstone_Deck_Tracker.Utility.Battlegrounds.BattlegroundsLastGames.Instance.Games
            .Where(g => !g.FriendlyGame && g.Duos == duos && g.Hero != null && g.Placement > 0).ToList();
        var parts = offered.Select(id =>
        {
            var baseId = HeroStats.BaseHeroId(id);
            var name = baseId != null && HearthDb.Cards.All.TryGetValue(baseId, out var c) ? c.GetLocName(Locale.koKR) : "?";
            var mine = games.Where(g => BattlegroundsUtils.GetOriginalHeroId(g.Hero!) == baseId).ToList();
            return mine.Count > 0 ? $"{name} {mine.Average(g => g.Placement):0.0}등({mine.Count}판)" : $"{name} –";
        });
        return $"내 성적({games.Count}판): " + string.Join(" · ", parts);
    }

    static BattlegroundsHeroPickStats.BattlegroundsSingleHeroPickStats? PickHero(
        HeroStats set, HeroStats all, int dbfId, IReadOnlyCollection<int> tribes)
    {
        var thin = set != all && (set.Find(dbfId)?.DataPoints ?? 0) < ThinDataPoints;
        return (thin ? all : set).ForHdt(dbfId, tribes);
    }

    // Trinket offer = the player's current choice whose entities are all trinkets (HDT keeps Player.OfferedEntityIds per choice).
    void TrinketTick(GameV2 game, int pct, bool duos)
    {
        var offered = game.Player.OfferedEntityIds?.ToList();
        if (offered == null || offered.Count == 0) { _trinketApplied = ""; return; }
        var entities = offered.Select(id => game.Entities.TryGetValue(id, out var e) ? e : null).ToList();
        if (entities.Any(e => e == null || !e.IsBattlegroundsTrinket)) { _trinketApplied = ""; return; }
        var stats = _trinkets.Get();
        if (stats == null) return;

        var vm = Core.OverlayWindow.BattlegroundsTrinketPickingViewModel;
        var key = $"{pct}|{string.Join(",", offered)}";
        if (key != _trinketApplied || vm.TrinketStats == null)
        {
            vm.SetTrinketStats(entities.Select(e =>
                stats.ForHdt(e!.Card.DbfId, pct) ?? new BattlegroundsTrinketPickStats.BattlegroundsSingleTrinketPickStats { TrinketDbfId = e.Card.DbfId }));
            vm.ChoicesVisible = true;   // HDT's memory watcher keeps this in sync afterwards
            _trinketMessage = $"Firestone{(duos ? " · 솔로 데이터" : "")} · {(pct == 100 ? "전체 MMR" : $"상위 {pct}%")} · 최근 7일 · {stats.DataPoints / 1000}k판 · {stats.LastUpdate.ToLocalTime():MM-dd HH:mm} 갱신";
            _trinketApplied = key;
            Log.Info($"BgFree: trinket stats applied ({key})");
        }
        if (vm.Message.Text != _trinketMessage)
            vm.Message.Text = _trinketMessage;
    }

    // Comp rows go into HDT's session panel; HDT's own Update() collapses the section (Tier7 off), so re-assert each tick.
    void CompTick(int pct, List<int> tribes)
    {
        var stats = _comps.Get();
        if (stats == null) return;
        var vm = Core.OverlayWindow.BattlegroundsSessionViewModelVM;
        var key = $"{pct}|{string.Join("/", tribes)}";
        if (key != _compApplied || vm.CompositionStats == null)
        {
            var rows = stats.ForHdt(pct, tribes);
            vm.SetBattlegroundsCompositionStatsViewModel(rows);
            _compApplied = key;
            Log.Info($"BgFree: comp stats applied ({key}) rows={rows.Count} of {stats.Comps.Count}");
        }
        if (vm.AvailableCompStatsSectionVisibility != Visibility.Visible) vm.AvailableCompStatsSectionVisibility = Visibility.Visible;
        if (vm.CompStatsBodyVisibility != Visibility.Visible) vm.CompStatsBodyVisibility = Visibility.Visible;
        if (vm.CompStatsWaitingMsgVisibility != Visibility.Collapsed) vm.CompStatsWaitingMsgVisibility = Visibility.Collapsed;
        if (vm.CompStatsErrorVisibility != Visibility.Hidden) vm.CompStatsErrorVisibility = Visibility.Hidden;
    }

    // Lobby tribes from HDT's memory reader; empty until it has read the lobby (or if it never does).
    static List<int> Tribes()
    {
        try
        {
            var races = BattlegroundsUtils.GetAvailableRaces();
            return races == null ? new List<int>() : races.Select(r => (int)r).OrderBy(r => r).ToList();
        }
        catch { return new List<int>(); }
    }

    HeroStats? Heroes(bool duos, int pct)
    {
        if (!_heroes.TryGetValue((duos, pct), out var loader))
            _heroes[(duos, pct)] = loader = new Loader<HeroStats>($"hero-stats {(duos ? "duo" : "solo")} mmr-{pct}", TimeSpan.FromHours(1), () => HeroStats.LoadAsync(duos, pct));
        return loader.Get();
    }

    // One runnable check: every hero / trinket offered in the fixture log resolves to a Firestone row. Rerun after each HS patch.
    public void OnButtonPress()
    {
        // Optional regression fixture: a saved Power.log with full BG games (dev machines only; BGFREE_FIXTURE overrides).
        var fixture = Environment.GetEnvironmentVariable("BGFREE_FIXTURE")
                      ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HearthstoneDeckTracker", "BgFree", "Power.log");
        Task.Run(async () =>
        {
            string report;
            try
            {
                var heroes = await HeroStats.LoadAsync(false, 100);
                var trinkets = await TrinketStats.LoadAsync();
                var comps = await CompStats.LoadAsync();
                var cards = await CardStats.LoadAsync(100);
                var hasFixture = File.Exists(fixture);
                var blocks = hasFixture ? Offers(fixture) : new List<(string type, List<string> cardIds)>();
                var heroOffers = blocks.Where(b => b.type == "MULLIGAN").Select(b => b.cardIds).ToList();
                var trinketOffers = blocks.Where(b => b.type == "GENERAL" && b.cardIds.Count > 0 && b.cardIds.All(id => id.Contains("_MagicItem_"))).Select(b => b.cardIds).ToList();

                var missing = new List<string>();
                int hTotal = 0, hOk = 0, tTotal = 0, tOk = 0;
                foreach (var cardId in heroOffers.SelectMany(o => o))
                {
                    hTotal++;
                    var hero = HearthDb.Cards.All.TryGetValue(cardId, out var card) ? heroes.Find(card.DbfId) : null;
                    if (hero?.AveragePosition != null) hOk++; else missing.Add($"{cardId} ({card?.GetLocName(Locale.koKR) ?? "?"})");
                }
                foreach (var cardId in trinketOffers.SelectMany(o => o))
                {
                    tTotal++;
                    if (trinkets.ByCardId.TryGetValue(cardId, out var t) && t.AveragePlacement != null) tOk++;
                    else missing.Add($"{cardId} ({(HearthDb.Cards.All.TryGetValue(cardId, out var c) ? c.GetLocName(Locale.koKR) : "?")})");
                }
                var rating = Core.Game.CurrentBattlegroundsRating;
                var pct = HeroStats.Bucket(rating, heroes.MmrPercentiles);
                var tribes = Tribes();
                var topComps = comps.ForHdt(pct, tribes, 5).Select(c => $"{c.Name} {c.Popularity:0.0}% {c.AvgFinalPlacement:0.00}");
                var boards = comps.Comps.Sum(c => c.Boards.Count);
                var minionIds = comps.Comps.SelectMany(c => c.Boards).SelectMany(b => b.Minions).Select(m => m.CardId).Distinct().ToList();
                var unknownMinions = minionIds.Where(id => !HearthDb.Cards.All.ContainsKey(id)).ToList();
                report = $"heroes: {heroes.ByCardId.Count}, {heroes.DataPoints} games, updated {heroes.LastUpdate:u}\n"
                       + $"trinkets: {trinkets.ByCardId.Count}, {trinkets.DataPoints} games; cards: {cards.ByCardId.Count}, {cards.DataPoints} games; shop slot hook: {(OppBoardItems != null ? "ok" : "MISSING")}\n"
                       + $"comps: {comps.Comps.Count}, {comps.DataPoints} games, key minions resolved: {comps.Comps.Count(c => c.KeyMinionDbfIds.Count == 3)}/{comps.Comps.Count}\n"
                       + $"boards: {boards}, distinct minions {minionIds.Count}, unknown to HearthDb: {unknownMinions.Count}{(unknownMinions.Count > 0 ? " (" + string.Join(", ", unknownMinions.Take(5)) + ")" : "")}; inspiration hooks: {(SetGames != null && SetPages != null ? "ok" : "MISSING")}\n"
                       + $"buckets: {string.Join(", ", heroes.MmrPercentiles.Select(p => $"{p.Percentile}%>={p.Mmr}"))}; rating {rating?.ToString() ?? "?"} -> mmr-{pct}\n"
                       + $"tribes: {(tribes.Count > 0 ? string.Join(", ", tribes.Select(t => (Race)t)) : "none")}\n"
                       + (hasFixture ? $"fixture: {heroOffers.Count} hero offers {hOk}/{hTotal}; {trinketOffers.Count} trinket offers {tOk}/{tTotal}\n" : "fixture: none (optional)\n")
                       + (missing.Count > 0 ? $"missing: {string.Join(", ", missing)}\n" : "")
                       + $"top comps now: {string.Join(" | ", topComps)}";
                if (hasFixture && (heroOffers.Count == 0 || hOk != hTotal || tOk != tTotal)) report = "FAIL\n" + report;
            }
            catch (Exception e) { report = "FAIL\n" + e; }
            Application.Current.Dispatcher.Invoke(() => MessageBox.Show(report, "BgFree self-check"));
        });
    }

    static readonly Regex CardIdRx = new(@"cardId=([A-Za-z0-9_]+)", RegexOptions.Compiled);
    static readonly Regex ChoiceTypeRx = new(@"ChoiceType=(\w+)", RegexOptions.Compiled);

    // Player choice blocks from a Power.log: (ChoiceType, offered card ids) per block.
    static List<(string type, List<string> cardIds)> Offers(string path)
    {
        var blocks = new List<(string, List<string>)>();
        List<string>? current = null;
        foreach (var line in File.ReadLines(path))
        {
            if (!line.Contains("GameState.DebugPrintEntityChoices()")) { current = null; continue; }
            if (ChoiceTypeRx.Match(line) is { Success: true } t) { current = new List<string>(); blocks.Add((t.Groups[1].Value, current)); continue; }
            if (current != null && line.Contains("Entities[") && CardIdRx.Match(line) is { Success: true } m)
                current.Add(m.Groups[1].Value);
        }
        return blocks;
    }
}

// Background-loaded, TTL-refreshed value. Get() never blocks: returns the current (possibly stale) value or null.
sealed class Loader<T> where T : class
{
    readonly string _name;
    readonly TimeSpan _ttl;
    readonly Func<Task<T>> _load;
    volatile T? _value;
    volatile bool _busy;
    DateTime _at, _retryAt;

    public Loader(string name, TimeSpan ttl, Func<Task<T>> load) { _name = name; _ttl = ttl; _load = load; }

    public T? Get()
    {
        if (!_busy && DateTime.UtcNow >= _retryAt && (_value == null || DateTime.UtcNow - _at > _ttl))
        {
            _busy = true;
            Task.Run(async () =>
            {
                try
                {
                    _value = await _load();
                    _at = DateTime.UtcNow;
                    Log.Info($"BgFree: {_name} loaded");
                }
                catch (Exception e)
                {
                    Log.Error($"BgFree: {_name} load failed: {e.Message}");
                    _retryAt = DateTime.UtcNow.AddMinutes(1);
                }
                finally { _busy = false; }
            });
        }
        return _value;
    }
}
