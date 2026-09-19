using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HSReplay.Responses;
using Newtonsoft.Json;

namespace BgFree;

// Trinket pick stats: one file, per-MMR values inside each row.
public sealed class TrinketStats
{
    const int ThinDataPoints = 100;

    public DateTime LastUpdate { get; private set; }
    public long DataPoints { get; private set; }
    public IReadOnlyDictionary<string, Trinket> ByCardId { get; private set; } = new Dictionary<string, Trinket>();

    public sealed class Trinket
    {
        [JsonProperty("trinketCardId")] public string? TrinketCardId { get; set; }
        [JsonProperty("dataPoints")] public long DataPoints { get; set; }
        [JsonProperty("pickRate")] public double? PickRate { get; set; }              // 0-1
        [JsonProperty("averagePlacement")] public double? AveragePlacement { get; set; }
        [JsonProperty("averagePlacementAtMmr")] public List<AtMmr>? AveragePlacementAtMmr { get; set; }
        [JsonProperty("pickRateAtMmr")] public List<AtMmr>? PickRateAtMmr { get; set; }
    }

    public sealed class AtMmr
    {
        [JsonProperty("mmr")] public int Percentile { get; set; }
        [JsonProperty("dataPoints")] public long DataPoints { get; set; }
        [JsonProperty("placement")] public double? Placement { get; set; }
        [JsonProperty("pickRate")] public double? PickRate { get; set; }
    }

    sealed class Root
    {
        [JsonProperty("trinketStats")] public List<Trinket>? TrinketStats { get; set; }
        [JsonProperty("lastUpdateDate")] public DateTime? LastUpdateDate { get; set; }
        [JsonProperty("dataPoints")] public long DataPoints { get; set; }
    }

    public static async Task<TrinketStats> LoadAsync()
    {
        var json = await Cdn.GetAsync("trinket-stats/past-seven/overview-from-hourly.gz.json", TimeSpan.FromHours(1)).ConfigureAwait(false);
        var root = JsonConvert.DeserializeObject<Root>(json) ?? new Root();
        return new TrinketStats
        {
            LastUpdate = root.LastUpdateDate ?? default,
            DataPoints = root.DataPoints,
            ByCardId = (root.TrinketStats ?? new List<Trinket>()).Where(t => t.TrinketCardId != null)
                .GroupBy(t => t.TrinketCardId!).ToDictionary(g => g.Key, g => g.First()),
        };
    }

    // Shape HDT's own trinket-picking overlay consumes. Per-MMR value when the bucket has enough games, else overall.
    public BattlegroundsTrinketPickStats.BattlegroundsSingleTrinketPickStats? ForHdt(int dbfId, int percentile)
    {
        if (!HearthDb.Cards.AllByDbfId.TryGetValue(dbfId, out var card) || !ByCardId.TryGetValue(card.Id, out var t)) return null;
        var place = t.AveragePlacementAtMmr?.FirstOrDefault(a => a.Percentile == percentile && a.DataPoints >= ThinDataPoints)?.Placement ?? t.AveragePlacement;
        var pick = t.PickRateAtMmr?.FirstOrDefault(a => a.Percentile == percentile && a.DataPoints >= ThinDataPoints)?.PickRate ?? t.PickRate;
        return new BattlegroundsTrinketPickStats.BattlegroundsSingleTrinketPickStats
        {
            TrinketDbfId = dbfId,
            Tier = Cdn.Tier(place),
            AvgPlacement = place,
            PickRate = pick * 100,
        };
    }
}

// Per-card stats: avg placement of games where the card was bought vs not, overall and per BG turn. One file per MMR bucket.
public sealed class CardStats
{
    const int MinTurnSample = 50;

    public DateTime LastUpdate { get; private set; }
    public long DataPoints { get; private set; }
    public IReadOnlyDictionary<string, Card> ByCardId { get; private set; } = new Dictionary<string, Card>();

    public sealed class Card
    {
        [JsonProperty("cardId")] public string? CardId { get; set; }
        [JsonProperty("totalPlayed")] public long TotalPlayed { get; set; }
        [JsonProperty("averagePlacement")] public double? AveragePlacement { get; set; }
        [JsonProperty("averagePlacementOther")] public double? AveragePlacementOther { get; set; }
        [JsonProperty("turnStats")] public List<TurnStat>? TurnStats { get; set; }
    }

    public sealed class TurnStat
    {
        [JsonProperty("turn")] public int? Turn { get; set; }   // null rows exist in the feed
        [JsonProperty("totalPlayed")] public long TotalPlayed { get; set; }
        [JsonProperty("averagePlacement")] public double? AveragePlacement { get; set; }
        [JsonProperty("averagePlacementOther")] public double? AveragePlacementOther { get; set; }
    }

    sealed class Root
    {
        [JsonProperty("cardStats")] public List<Card>? CardStats { get; set; }
        [JsonProperty("lastUpdateDate")] public DateTime? LastUpdateDate { get; set; }
        [JsonProperty("dataPoints")] public long DataPoints { get; set; }
    }

    public static async Task<CardStats> LoadAsync(int percentile)
    {
        var json = await Cdn.GetAsync($"card-stats/mmr-{percentile}/past-seven/overview-from-hourly.gz.json", TimeSpan.FromHours(1)).ConfigureAwait(false);
        var root = JsonConvert.DeserializeObject<Root>(json) ?? new Root();
        return new CardStats
        {
            LastUpdate = root.LastUpdateDate ?? default,
            DataPoints = root.DataPoints,
            ByCardId = (root.CardStats ?? new List<Card>()).Where(c => c.CardId != null).GroupBy(c => c.CardId!).ToDictionary(g => g.Key, g => g.First()),
        };
    }

    // Placement gain, on this turn, of buying minions of a whole tavern tier (weighted by games). Proxy for "is being at
    // that tier now worth it". Null when the tier has too few games on that turn.
    readonly Dictionary<(int turn, int tier), double?> _tierCache = new();
    public double? TierDelta(int turn, int tier)
    {
        lock (_tierCache)
        {
            if (_tierCache.TryGetValue((turn, tier), out var cached)) return cached;
            double num = 0; long den = 0;
            foreach (var c in ByCardId.Values)
            {
                if (c.CardId == null || !HearthDb.Cards.All.TryGetValue(c.CardId, out var card) || card.TechLevel != tier || card.Type != HearthDb.Enums.CardType.MINION) continue;
                var t = c.TurnStats?.FirstOrDefault(x => x.Turn == turn);
                if (t is { TotalPlayed: >= 30, AveragePlacement: double p, AveragePlacementOther: double o }) { num += (o - p) * t.TotalPlayed; den += t.TotalPlayed; }
            }
            var result = den >= 300 ? num / den : (double?)null;
            _tierCache[(turn, tier)] = result;
            return result;
        }
    }

    // Placement gain from buying this card on this turn: (avg when not bought) - (avg when bought). Positive = good.
    // Uses the turn row when it has enough games, else the overall row. Null when the card is unknown.
    public (double delta, long sample, bool turnSpecific)? Delta(string cardId, int turn)
    {
        if (!ByCardId.TryGetValue(cardId, out var c)) return null;
        var t = c.TurnStats?.FirstOrDefault(x => x.Turn == turn);
        if (t is { TotalPlayed: >= MinTurnSample, AveragePlacement: double tp, AveragePlacementOther: double to })
            return (to - tp, t.TotalPlayed, true);
        if (c is { AveragePlacement: double p, AveragePlacementOther: double o })
            return (o - p, c.TotalPlayed, false);
        return null;
    }
}

// Composition stats: 18 archetypes, share of games, avg placement (per MMR), and example first-place boards.
public sealed class CompStats
{
    public DateTime LastUpdate { get; private set; }
    public long DataPoints { get; private set; }
    public IReadOnlyList<Comp> Comps { get; private set; } = Array.Empty<Comp>();

    public sealed class Comp
    {
        public string Archetype = "";
        public long DataPoints;
        public double? AveragePlacement;
        public Dictionary<int, (long dataPoints, double placement)> AtMmr = new();
        public List<int> KeyMinionDbfIds = new();   // most frequent minions on first-place boards
        public int? Tribe;                          // HearthDb Race value from the slug prefix; null = any lobby
        public List<Board> Boards = new();          // real first-place boards, highest MMR first
        public Dictionary<string, double> CardFreq = new();   // cardId -> share of this comp's first-place boards containing it
        public double Popularity(long total) => total > 0 ? 100.0 * DataPoints / total : 0;
        public double Fit(string cardId) => CardFreq.TryGetValue(cardId, out var f) ? f : 0;
    }

    // Which comps does my current board look like? Score = mean, over my minions, of how often each appears in the comp's
    // first-place boards. Returns comps within 60% of the best score, best first; empty when nothing fits.
    public List<(Comp comp, double score)> Infer(IReadOnlyCollection<string> myCardIds, IReadOnlyCollection<int> tribes)
    {
        if (myCardIds.Count == 0) return new();
        var scored = Comps
            .Where(c => c.Tribe == null || tribes.Count == 0 || tribes.Contains(c.Tribe.Value))
            .Select(c => (comp: c, score: myCardIds.Average(id => c.Fit(id))))
            .Where(x => x.score >= 0.12)
            .OrderByDescending(x => x.score)
            .ToList();
        return scored.Count == 0 ? scored : scored.Where(x => x.score >= scored[0].score * 0.6).Take(2).ToList();
    }

    public sealed class Board
    {
        public string HeroCardId = "";
        public int Mmr;
        public int Turn;
        public List<Minion> Minions = new();
    }

    public sealed class Minion
    {
        public string CardId = "";   // normal (non-golden) id
        public int Attack, Health, ZonePos;
        public bool Premium, DivineShield, Taunt, Poisonous, Venomous, Reborn, Deathrattle, Windfury;
    }

    // Raw shape (only what we read; the 47 MB file also carries every tag of every minion).
    sealed class Root
    {
        [JsonProperty("compStats")] public List<RawComp>? CompStats { get; set; }
        [JsonProperty("lastUpdateDate")] public DateTime? LastUpdateDate { get; set; }
        [JsonProperty("dataPoints")] public long DataPoints { get; set; }
    }
    sealed class RawComp
    {
        [JsonProperty("archetype")] public string? Archetype { get; set; }
        [JsonProperty("dataPoints")] public long DataPoints { get; set; }
        [JsonProperty("averagePlacement")] public double? AveragePlacement { get; set; }
        [JsonProperty("averagePlacementAtMmr")] public List<TrinketStats.AtMmr>? AveragePlacementAtMmr { get; set; }
        [JsonProperty("heroStats")] public List<RawHero>? HeroStats { get; set; }
    }
    sealed class RawHero
    {
        [JsonProperty("heroCardId")] public string? HeroCardId { get; set; }
        [JsonProperty("finalBoards")] public List<RawBoard>? FinalBoards { get; set; }
    }
    sealed class RawBoard
    {
        [JsonProperty("mmr")] public int Mmr { get; set; }
        [JsonProperty("finalComp")] public RawFinalComp? FinalComp { get; set; }
    }
    sealed class RawFinalComp
    {
        [JsonProperty("turn")] public int Turn { get; set; }
        [JsonProperty("board")] public List<RawMinion>? Board { get; set; }
    }
    sealed class RawMinion
    {
        [JsonProperty("cardID")] public string? CardId { get; set; }
        [JsonProperty("tags")] public Dictionary<string, long>? Tags { get; set; }
    }

    static Minion ToMinion(RawMinion raw, string baseId)
    {
        var t = raw.Tags ?? new Dictionary<string, long>();
        long Tag(string k) => t.TryGetValue(k, out var v) ? v : 0;
        return new Minion
        {
            CardId = baseId,
            Attack = (int)Tag("ATK"), Health = (int)Tag("HEALTH"), ZonePos = (int)Tag("ZONE_POSITION"),
            Premium = Tag("PREMIUM") > 0 || raw.CardId!.EndsWith("_G"),
            DivineShield = Tag("DIVINE_SHIELD") > 0, Taunt = Tag("TAUNT") > 0, Poisonous = Tag("POISONOUS") > 0,
            Venomous = Tag("VENOMOUS") > 0, Reborn = Tag("REBORN") > 0, Deathrattle = Tag("DEATHRATTLE") > 0, Windfury = Tag("WINDFURY") > 0,
        };
    }

    // ponytail: hand-written slug -> Korean label + tribe. Unknown slugs fall back to the slug itself.
    static readonly Dictionary<string, string> Names = new()
    {
        ["pirate_discover"] = "해적 발견", ["murloc_handbuff"] = "멀록 핸드버프", ["dragon_kalecgos"] = "용 칼렉고스",
        ["dragon_evoker"] = "용 기원사", ["beast_lobster"] = "야수 바닷가재", ["elemental_cycle"] = "정령 순환",
        ["demon_self_damage"] = "악마 자해", ["demon_boost_shop"] = "악마 상점 강화", ["undead_butcher"] = "언데드 도살자",
        ["beast_beetle"] = "야수 딱정벌레", ["mech_magnet"] = "기계 자석", ["naga_groundbreaker"] = "나가 그라운드브레이커",
        ["beast_leviathan"] = "야수 리바이어던", ["naga_end_of_turn"] = "나가 턴 종료", ["quilboar_choose_one"] = "가시멧돼지 선택",
        ["murloc_mrrglton"] = "멀록 므르글튼", ["neutral_tea_set"] = "중립 티 세트", ["mech_automaton"] = "기계 오토마톤",
    };
    static readonly Dictionary<string, int> Tribes = new()
    {
        ["pirate"] = 23, ["murloc"] = 14, ["dragon"] = 24, ["beast"] = 20, ["elemental"] = 18,
        ["demon"] = 15, ["undead"] = 11, ["mech"] = 17, ["naga"] = 92, ["quilboar"] = 43,
    };

    public static string Label(string slug) => Names.TryGetValue(slug, out var n) ? n : slug.Replace('_', ' ');

    public static async Task<CompStats> LoadAsync()
    {
        var json = await Cdn.GetAsync("comp-stats/past-seven/overview-from-hourly.gz.json", TimeSpan.FromHours(6)).ConfigureAwait(false);
        var root = JsonConvert.DeserializeObject<Root>(json) ?? new Root();
        var comps = new List<Comp>();
        foreach (var raw in root.CompStats ?? new List<RawComp>())
        {
            if (raw.Archetype == null) continue;
            var counts = new Dictionary<string, int>();
            var boards = new List<Board>();
            foreach (var hero in raw.HeroStats ?? new())
            {
                if (hero.HeroCardId == null) continue;
                foreach (var rb in hero.FinalBoards ?? new())
                {
                    var board = new Board { HeroCardId = hero.HeroCardId, Mmr = rb.Mmr, Turn = rb.FinalComp?.Turn ?? 0 };
                    foreach (var m in rb.FinalComp?.Board ?? new())
                    {
                        if (m.CardId == null) continue;
                        var baseId = m.CardId.EndsWith("_G") ? m.CardId.Substring(0, m.CardId.Length - 2) : m.CardId;   // golden -> normal
                        counts[baseId] = counts.TryGetValue(baseId, out var c) ? c + 1 : 1;
                        board.Minions.Add(ToMinion(m, baseId));
                    }
                    if (board.Minions.Count > 0) boards.Add(board);
                }
            }
            var prefix = raw.Archetype.Split('_')[0];
            comps.Add(new Comp
            {
                Archetype = raw.Archetype,
                DataPoints = raw.DataPoints,
                AveragePlacement = raw.AveragePlacement,
                AtMmr = (raw.AveragePlacementAtMmr ?? new()).Where(a => a.Placement != null).GroupBy(a => a.Percentile)
                    .ToDictionary(g => g.Key, g => (g.First().DataPoints, g.First().Placement!.Value)),
                KeyMinionDbfIds = counts.OrderByDescending(kv => kv.Value).Take(3)
                    .Select(kv => HearthDb.Cards.All.TryGetValue(kv.Key, out var card) ? card.DbfId : 0).Where(d => d != 0).ToList(),
                Tribe = Tribes.TryGetValue(prefix, out var t) ? t : null,
                Boards = boards.OrderByDescending(b => b.Mmr).ToList(),
                CardFreq = boards.Count == 0 ? new() : boards
                    .SelectMany(b => b.Minions.Select(m => m.CardId).Distinct())
                    .GroupBy(id => id).ToDictionary(g => g.Key, g => (double)g.Count() / boards.Count),
            });
        }
        return new CompStats { LastUpdate = root.LastUpdateDate ?? default, DataPoints = root.DataPoints, Comps = comps };
    }

    // Example first-place boards for the lobby: this hero's boards first (highest MMR first), then other heroes' from the
    // most popular playable comps, until `max`.
    public List<(Comp comp, Board board)> BoardsFor(string? heroCardId, IReadOnlyCollection<int> tribes, int max = 20)
    {
        var playable = Comps.Where(c => c.Tribe == null || tribes.Count == 0 || tribes.Contains(c.Tribe.Value)).OrderByDescending(c => c.DataPoints).ToList();
        var mine = playable.SelectMany(c => c.Boards.Where(b => b.HeroCardId == heroCardId).Select(b => (c, b))).OrderByDescending(x => x.b.Mmr).Take(max).ToList();
        if (mine.Count < max)
            mine.AddRange(playable.SelectMany(c => c.Boards.Where(b => b.HeroCardId != heroCardId).Take(3).Select(b => (c, b))).Take(max - mine.Count));
        return mine;
    }

    // Rows for HDT's session panel: comps playable with the lobby's tribes, most popular first.
    public List<BattlegroundsCompStats.LobbyComp> ForHdt(int percentile, IReadOnlyCollection<int> tribes, int max = 8)
    {
        var total = Comps.Sum(c => c.DataPoints);
        return Comps
            .Where(c => c.Tribe == null || tribes.Count == 0 || tribes.Contains(c.Tribe.Value))
            .OrderByDescending(c => c.DataPoints)
            .Take(max)
            .Select((c, i) =>
            {
                var avg = c.AtMmr.TryGetValue(percentile, out var a) && a.dataPoints >= 100 ? a.placement : c.AveragePlacement ?? 0;
                return new BattlegroundsCompStats.LobbyComp
                {
                    Id = i + 1,
                    Name = $"[{Cdn.Tier(avg)?.ToUpperInvariant() ?? "?"}] {Label(c.Archetype)}",   // HDT's row has no tier slot; prefix the name
                    Popularity = c.Popularity(total),
                    KeyMinionsTop3 = c.KeyMinionDbfIds,
                    AvgFinalPlacement = avg,
                };
            })
            .ToList();
    }
}
