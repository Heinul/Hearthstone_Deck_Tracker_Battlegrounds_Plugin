using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using HearthDb.Enums;
using Hearthstone_Deck_Tracker.Hearthstone;
using HSReplay.Responses;
using Newtonsoft.Json;

namespace BgFree;

// Firestone public CDN. Unlicensed: personal use only, one fetch per TTL per file, cached on disk, stale copy when offline.
public static class Cdn
{
    public const string Base = "https://static.zerotoheroes.com/api/bgs/";
    static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HearthstoneDeckTracker", "BgFree", "cache");
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    static Cdn() => Http.DefaultRequestHeaders.UserAgent.ParseAdd("BgFree/0.3 (personal)");

    public static async Task<string> GetAsync(string path, TimeSpan ttl)
    {
        Directory.CreateDirectory(CacheDir);
        var cacheFile = Path.Combine(CacheDir, path.Replace('/', '-'));
        if (File.Exists(cacheFile) && DateTime.UtcNow - File.GetLastWriteTimeUtc(cacheFile) < ttl)
            return File.ReadAllText(cacheFile);
        try
        {
            var json = await Http.GetStringAsync(Base + path).ConfigureAwait(false);
            File.WriteAllText(cacheFile, json);
            return json;
        }
        catch when (File.Exists(cacheFile))
        {
            return File.ReadAllText(cacheFile);
        }
    }

    // ponytail: own tier bands on average placement (1-8 scale); not HSReplay's or Firestone's tiers.
    public static string? Tier(double? avg) => avg switch
    {
        null => null,
        <= 3.8 => "s",
        <= 4.1 => "a",
        <= 4.4 => "b",
        <= 4.7 => "c",
        <= 5.0 => "d",
        _ => "f",
    };
}

// Hero pick stats, one file per (solo|duo, MMR percentile bucket).
public sealed class HeroStats
{
    public static readonly int[] Percentiles = { 100, 50, 25, 10, 1 };

    public bool Duos { get; private set; }
    public int Percentile { get; private set; }
    public DateTime LastUpdate { get; private set; }
    public long DataPoints { get; private set; }
    public IReadOnlyList<MmrPercentile> MmrPercentiles { get; private set; } = Array.Empty<MmrPercentile>();
    public IReadOnlyDictionary<string, Hero> ByCardId { get; private set; } = new Dictionary<string, Hero>();

    public sealed class Hero
    {
        [JsonProperty("heroCardId")] public string? HeroCardId { get; set; }
        [JsonProperty("dataPoints")] public long DataPoints { get; set; }
        [JsonProperty("totalOffered")] public long TotalOffered { get; set; }
        [JsonProperty("totalPicked")] public long TotalPicked { get; set; }
        [JsonProperty("averagePosition")] public double? AveragePosition { get; set; }
        [JsonProperty("conservativePositionEstimate")] public double? ConservativePositionEstimate { get; set; }
        [JsonProperty("placementDistribution")] public List<Placement>? PlacementDistribution { get; set; }
        [JsonProperty("tribeStats")] public List<TribeStat>? TribeStats { get; set; }
        public double? PickRate => TotalOffered > 0 ? 100.0 * TotalPicked / TotalOffered : null;

        // ponytail: lobby adjustment = avg + sum of Firestone's per-tribe impact for tribes present. Heuristic, labelled as such.
        public double? AdjustedAverage(IReadOnlyCollection<int>? tribes)
        {
            if (AveragePosition is not double avg) return null;
            if (tribes == null || tribes.Count == 0 || TribeStats == null) return avg;
            return avg + TribeStats.Where(t => tribes.Contains(t.Tribe)).Sum(t => t.ImpactAveragePosition ?? 0);
        }
    }

    public sealed class Placement
    {
        [JsonProperty("rank")] public int Rank { get; set; }
        [JsonProperty("percentage")] public double Percentage { get; set; }
    }

    public sealed class TribeStat
    {
        [JsonProperty("tribe")] public int Tribe { get; set; }   // HearthDb.Enums.Race value
        [JsonProperty("dataPoints")] public long DataPoints { get; set; }
        [JsonProperty("impactAveragePosition")] public double? ImpactAveragePosition { get; set; }
    }

    public sealed class MmrPercentile
    {
        [JsonProperty("percentile")] public int Percentile { get; set; }
        [JsonProperty("mmr")] public int Mmr { get; set; }
    }

    sealed class Root
    {
        [JsonProperty("heroStats")] public List<Hero>? HeroStats { get; set; }
        [JsonProperty("lastUpdateDate")] public DateTime? LastUpdateDate { get; set; }
        [JsonProperty("dataPoints")] public long DataPoints { get; set; }
        [JsonProperty("mmrPercentiles")] public List<MmrPercentile>? MmrPercentiles { get; set; }
    }

    public static string Path(bool duos, int percentile) =>
        $"{(duos ? "duo/" : "")}hero-stats/mmr-{percentile}/past-seven/overview-from-hourly.gz.json";

    // Smallest percentile bucket whose MMR floor the rating clears. Unknown rating -> 100 (everyone).
    public static int Bucket(int? rating, IReadOnlyList<MmrPercentile> percentiles)
    {
        if (rating is not int r || percentiles.Count == 0) return 100;
        var eligible = percentiles.Where(p => p.Mmr <= r && Percentiles.Contains(p.Percentile)).ToList();
        return eligible.Count == 0 ? 100 : eligible.Min(p => p.Percentile);
    }

    public static async Task<HeroStats> LoadAsync(bool duos, int percentile)
    {
        var stats = Parse(await Cdn.GetAsync(Path(duos, percentile), TimeSpan.FromHours(1)).ConfigureAwait(false));
        stats.Duos = duos;
        stats.Percentile = percentile;
        return stats;
    }

    public static HeroStats Parse(string json)
    {
        var root = JsonConvert.DeserializeObject<Root>(json) ?? new Root();
        var heroes = (root.HeroStats ?? new List<Hero>()).Where(h => h.HeroCardId != null);
        return new HeroStats
        {
            LastUpdate = root.LastUpdateDate ?? default,
            DataPoints = root.DataPoints,
            MmrPercentiles = root.MmrPercentiles ?? new List<MmrPercentile>(),
            ByCardId = heroes.GroupBy(h => h.HeroCardId!).ToDictionary(g => g.Key, g => g.First()),
        };
    }

    // HDT dbfId -> Firestone key (base hero card id, skins collapsed to parent).
    public static string? BaseHeroId(int dbfId) =>
        HearthDb.Cards.AllByDbfId.TryGetValue(dbfId, out var card) ? BattlegroundsUtils.GetOriginalHeroId(card.Id) : null;

    public Hero? Find(int dbfId) =>
        BaseHeroId(dbfId) is string key && ByCardId.TryGetValue(key, out var hero) ? hero : null;

    // Shape HDT's own hero-picking overlay consumes. Null -> HDT renders an empty header for that hero.
    public BattlegroundsHeroPickStats.BattlegroundsSingleHeroPickStats? ForHdt(int dbfId, IReadOnlyCollection<int>? tribes)
    {
        var hero = Find(dbfId);
        if (hero == null) return null;
        var avg = hero.AdjustedAverage(tribes);
        var dist = new double[8];
        foreach (var p in hero.PlacementDistribution ?? new List<Placement>())
            if (p.Rank is >= 1 and <= 8) dist[p.Rank - 1] = p.Percentage;
        return new BattlegroundsHeroPickStats.BattlegroundsSingleHeroPickStats
        {
            HeroDbfId = dbfId,
            Tier = Cdn.Tier(Duos && avg is double a ? a * 2 - 0.5 : avg),   // duos places 1-4: map onto the 1-8 scale
            AvgPlacement = avg,
            PickRate = hero.PickRate,
            PlacementDistribution = dist,
        };
    }
}
