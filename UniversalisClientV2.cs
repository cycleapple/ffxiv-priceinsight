using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Networking.Http;
using Dalamud.Utility;
using Lumina.Excel.Sheets;

namespace PriceInsight;

public class UniversalisClientV2 : IDisposable {
    private static readonly Dictionary<byte, string> Regions = new() { { 1, "Japan" }, { 2, "North-America" }, { 3, "Europe" }, { 4, "Oceania" } };
    internal static readonly Dictionary<uint, (string Name, string DcName, string Region)> WorldLookup = Service.DataManager.GetExcelSheet<World>()
        .ToDictionary(w => w.RowId, w => (w.Name.ExtractText(), w.DataCenter.Value.Name.ExtractText(), GetRegionName(w)));

    private readonly HappyEyeballsCallback happyEyeballsCallback;
    private readonly HttpClient httpClient;

    public UniversalisClientV2() {
        happyEyeballsCallback = new HappyEyeballsCallback();
        httpClient = new HttpClient(new SocketsHttpHandler {
            AutomaticDecompression = DecompressionMethods.All, ConnectCallback = happyEyeballsCallback.ConnectCallback
        });
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd($"PriceInsight/{Assembly.GetExecutingAssembly().GetName().Version} ({Environment.OSVersion}) Dalamud/{Util.AssemblyVersion}");
    }

    public async Task<Dictionary<uint, MarketBoardData>?> GetMarketBoardDataList(
        uint homeWorldId, ICollection<uint> itemId, CancellationToken cancellationToken) {
        try {
            var requestUri =
                $"https://universalis.app/api/v2/aggregated/{homeWorldId}/{string.Join(',', itemId.Select(i => i.ToString()))}";
            using var result = await GetWithRetryAsync(requestUri, cancellationToken);

            if (result.StatusCode != HttpStatusCode.OK && result.StatusCode != HttpStatusCode.BadRequest) {
                throw new HttpRequestException("Invalid status code " + result.StatusCode, null, result.StatusCode);
            }

            var items = new Dictionary<uint, MarketBoardData>();
            var unresolvedItems = new HashSet<uint>();
            if (result.StatusCode == HttpStatusCode.OK) {
                await using var responseStream = await result.Content.ReadAsStreamAsync(cancellationToken);
                var json = await JsonSerializer.DeserializeAsync<AggregatedMarketBoardData>(responseStream, cancellationToken: cancellationToken);
                if (json == null) {
                    throw new HttpRequestException("Universalis returned null response");
                }

                if (json.results != null) {
                    foreach (var item in json.results) {
                        items[item.itemId] = item.ToMarketBoardData(homeWorldId);
                    }
                }

                unresolvedItems.UnionWith(json.failedItems ?? []);
            } else {
                // The aggregated endpoint returns 400 when every requested item is absent from
                // Universalis' current marketable-item list. This happens for dyes that remain
                // marketable on the Traditional Chinese client but were consolidated in global 7.5.
                unresolvedItems.UnionWith(itemId);
            }

            if (unresolvedItems.Count > 0) {
                Service.PluginLog.Debug(
                    "Universalis aggregated data did not resolve itemIds {ItemIds}; trying the v3 overview endpoint.",
                    unresolvedItems);
                foreach (var unresolvedItem in unresolvedItems) {
                    try {
                        var overviewData = await GetMarketBoardOverview(homeWorldId, unresolvedItem, cancellationToken);
                        if (overviewData != null)
                            items[unresolvedItem] = overviewData;
                    } catch (Exception ex) when (!cancellationToken.IsCancellationRequested) {
                        Service.PluginLog.Warning(
                            ex, "Universalis overview fallback failed for itemId {ItemId}.", unresolvedItem);
                    }
                }
            }

            return items.Count > 0 ? items : null;
        } catch (Exception ex) {
            Service.PluginLog.Error(ex, "Failed to retrieve data from Universalis for itemIds {0}.", itemId);
            return null;
        }
    }

    private async Task<MarketBoardData?> GetMarketBoardOverview(
        uint homeWorldId, uint itemId, CancellationToken cancellationToken) {
        if (!WorldLookup.TryGetValue(homeWorldId, out var homeWorld))
            return null;

        // The v3 endpoint accepts explicit world IDs and does not reject items based on the
        // global marketable-item list. Keep the fallback bounded to the current data center;
        // on the Traditional Chinese service this also covers the entire region.
        var worldIds = WorldLookup
            .Where(w => w.Value.DcName == homeWorld.DcName)
            .Select(w => w.Key)
            .OrderBy(w => w)
            .ToArray();
        if (worldIds.Length == 0)
            return null;

        var requestUri =
            $"https://universalis.app/api/v3/market/overview/{string.Join(',', worldIds)}/{itemId}";
        using var result = await GetWithRetryAsync(requestUri, cancellationToken);
        if (result.StatusCode != HttpStatusCode.OK)
            throw new HttpRequestException("Invalid fallback status code " + result.StatusCode, null, result.StatusCode);

        await using var responseStream = await result.Content.ReadAsStreamAsync(cancellationToken);
        var overview = await JsonSerializer.DeserializeAsync<MarketOverview>(responseStream, cancellationToken: cancellationToken);
        return overview?.ToMarketBoardData(homeWorldId, worldIds);
    }

    private static string GetRegionName(World world) {
        var dcName = world.DataCenter.Value.Name.ExtractText();
        return dcName switch {
            "陸行鳥" => "繁中服",
            "陆行鸟" or "莫古力" or "猫小胖" or "豆豆柴" => "中国",
            "한국" => "한국",
            _ => Regions.GetValueOrDefault(world.DataCenter.Value.Region) ?? "unknown",
        };
    }

    private async Task<HttpResponseMessage> GetWithRetryAsync(string requestUri, CancellationToken cancellationToken) {
        const int maxAttempts = 3;

        for (var attempt = 1; ; attempt++) {
            try {
                var response = await httpClient.GetAsync(requestUri, cancellationToken);
                if (!IsTransient(response.StatusCode) || attempt >= maxAttempts)
                    return response;

                Service.PluginLog.Debug(
                    "Universalis returned {StatusCode}; retrying request ({Attempt}/{MaxAttempts}).",
                    response.StatusCode, attempt, maxAttempts);
                response.Dispose();
            } catch (HttpRequestException ex) when (attempt < maxAttempts && !cancellationToken.IsCancellationRequested) {
                Service.PluginLog.Debug(
                    ex, "Temporary Universalis request failure; retrying ({Attempt}/{MaxAttempts}).",
                    attempt, maxAttempts);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(attempt == 1 ? 400 : 1200), cancellationToken);
        }
    }

    private static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode == HttpStatusCode.RequestTimeout ||
        statusCode == HttpStatusCode.TooManyRequests ||
        statusCode == HttpStatusCode.InternalServerError ||
        statusCode == HttpStatusCode.BadGateway ||
        statusCode == HttpStatusCode.ServiceUnavailable ||
        statusCode == HttpStatusCode.GatewayTimeout;

    public void Dispose() {
        httpClient.Dispose();
        happyEyeballsCallback.Dispose();
    }
}

// ReSharper disable all
file class AggregatedMarketBoardData {
    public List<Result>? results { get; set; }
    public List<uint>? failedItems { get; set; }
}

file class MarketOverview {
    public uint item { get; set; }
    public Dictionary<uint, long?>? updatedAt { get; set; }
    public List<OverviewListing>? listings { get; set; }
    public List<OverviewSale>? sales { get; set; }

    public MarketBoardData ToMarketBoardData(uint homeWorldId, IReadOnlyCollection<uint> dcWorldIds) {
        var (homeWorld, dcName, region) = UniversalisClientV2.WorldLookup[homeWorldId];
        var homeWorldIds = new HashSet<uint> { homeWorldId };
        var dcWorldIdSet = dcWorldIds.ToHashSet();

        return new MarketBoardData {
            HomeWorld = homeWorld,
            Datacenter = dcName,
            Region = region,
            MinimumPrice = new() {
                World = GetMinimumPrice(homeWorldIds),
                Datacenter = GetMinimumPrice(dcWorldIdSet),
                // The fallback intentionally queries one data center. The Traditional Chinese
                // region currently contains that single data center, so both scopes are equal.
                Region = GetMinimumPrice(dcWorldIdSet),
            },
            MostRecentPurchase = new() {
                World = GetMostRecentPurchase(homeWorldIds),
                Datacenter = GetMostRecentPurchase(dcWorldIdSet),
                Region = GetMostRecentPurchase(dcWorldIdSet),
            },
            AverageSalePrice = new() {
                World = GetAverageSalePrice(homeWorldIds),
                Datacenter = GetAverageSalePrice(dcWorldIdSet),
                Region = GetAverageSalePrice(dcWorldIdSet),
            },
            DailySaleVelocity = new() {
                World = GetDailySaleVelocity(homeWorldIds),
                Datacenter = GetDailySaleVelocity(dcWorldIdSet),
                Region = GetDailySaleVelocity(dcWorldIdSet),
            },
        };
    }

    private Quality<PriceInsight.Listing> GetMinimumPrice(IReadOnlySet<uint> worldIds) => new() {
        Nq = GetMinimumPrice(worldIds, false),
        Hq = GetMinimumPrice(worldIds, true),
    };

    private PriceInsight.Listing? GetMinimumPrice(IReadOnlySet<uint> worldIds, bool hq) {
        var listing = listings?
            .Where(l => worldIds.Contains(l.world) && l.hq == hq && l.quantity > 0)
            .MinBy(l => l.UntaxedPrice);
        return listing == null ? null : ToListing(listing.UntaxedPrice, listing.world, listing.reviewedAt);
    }

    private Quality<PriceInsight.Listing> GetMostRecentPurchase(IReadOnlySet<uint> worldIds) => new() {
        Nq = GetMostRecentPurchase(worldIds, false),
        Hq = GetMostRecentPurchase(worldIds, true),
    };

    private PriceInsight.Listing? GetMostRecentPurchase(IReadOnlySet<uint> worldIds, bool hq) {
        var sale = sales?
            .Where(s => worldIds.Contains(s.world) && s.hq == hq)
            .MaxBy(s => s.saleTime);
        return sale == null ? null : ToListing(sale.price, sale.world, sale.saleTime);
    }

    private Quality<double?> GetAverageSalePrice(IReadOnlySet<uint> worldIds) => new() {
        Nq = GetAverageSalePrice(worldIds, false),
        Hq = GetAverageSalePrice(worldIds, true),
    };

    private double? GetAverageSalePrice(IReadOnlySet<uint> worldIds, bool hq) {
        var recentSales = GetRecentSales(worldIds, hq).ToList();
        var quantity = recentSales.Sum(s => (long)s.quantity);
        return quantity > 0 ? recentSales.Sum(s => (double)s.price * s.quantity) / quantity : null;
    }

    private Quality<double?> GetDailySaleVelocity(IReadOnlySet<uint> worldIds) => new() {
        Nq = GetDailySaleVelocity(worldIds, false),
        Hq = GetDailySaleVelocity(worldIds, true),
    };

    private double? GetDailySaleVelocity(IReadOnlySet<uint> worldIds, bool hq) {
        var quantity = GetRecentSales(worldIds, hq).Sum(s => (long)s.quantity);
        return quantity > 0 ? quantity / 4d : null;
    }

    private IEnumerable<OverviewSale> GetRecentSales(IReadOnlySet<uint> worldIds, bool hq) {
        var startOfWindow = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(-3), TimeSpan.Zero).ToUnixTimeMilliseconds();
        return sales?.Where(s => worldIds.Contains(s.world) && s.hq == hq && s.saleTime >= startOfWindow)
               ?? Enumerable.Empty<OverviewSale>();
    }

    private static PriceInsight.Listing ToListing(long price, uint worldId, long timestamp) {
        UniversalisClientV2.WorldLookup.TryGetValue(worldId, out var world);
        return new PriceInsight.Listing {
            Price = price,
            World = world.Name,
            Datacenter = world.DcName,
            Time = timestamp > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(timestamp).LocalDateTime : null,
        };
    }
}

file class OverviewListing {
    public uint world { get; set; }
    public long reviewedAt { get; set; }
    public decimal price { get; set; }
    public int quantity { get; set; }
    public long total { get; set; }
    public bool hq { get; set; }

    // V3 overview returns a tax-inclusive total. Recover the original integer
    // per-unit listing price used by the aggregated endpoint and the game UI.
    public long UntaxedPrice => quantity > 0 && total > 0
        ? total * 20 / (21L * quantity)
        : (long)Math.Floor(price / 1.05m);
}

file class OverviewSale {
    public uint world { get; set; }
    public bool hq { get; set; }
    public long price { get; set; }
    public int quantity { get; set; }
    public long saleTime { get; set; }
}

file class Result {
    public uint itemId { get; set; }
    public Aggregate? nq { get; set; }
    public Aggregate? hq { get; set; }
    public List<WorldUploadTime>? worldUploadTimes { get; set; }

    public MarketBoardData ToMarketBoardData(uint worldId) {
        var (worldName, dcName, region) = UniversalisClientV2.WorldLookup[worldId];
        var worldUploadTimes = this.worldUploadTimes != null
            ? this.worldUploadTimes.ToDictionary(w => w.worldId, w => (DateTime)w.timestamp)
            : new Dictionary<uint, DateTime>();
        var worldUploadTime = worldUploadTimes.GetValueOrDefault(worldId, DateTime.Now);
        var marketBoardData = new MarketBoardData {
            HomeWorld = worldName,
            Datacenter = dcName,
            Region = region,
            MinimumPrice = new() {
                World = new() {
                    Nq = this.nq?.minListing?.world?.ToListing(worldUploadTime, worldUploadTimes),
                    Hq = this.hq?.minListing?.world?.ToListing(worldUploadTime, worldUploadTimes),
                },
                Datacenter = new() {
                    Nq = this.nq?.minListing?.dc?.ToListing(worldUploadTime, worldUploadTimes),
                    Hq = this.hq?.minListing?.dc?.ToListing(worldUploadTime, worldUploadTimes),
                },
                Region = new() {
                    Nq = this.nq?.minListing?.region?.ToListing(worldUploadTime, worldUploadTimes),
                    Hq = this.hq?.minListing?.region?.ToListing(worldUploadTime, worldUploadTimes),
                }
            },
            MostRecentPurchase = new() {
                World = new() {
                    Nq = this.nq?.recentPurchase?.world,
                    Hq = this.hq?.recentPurchase?.world,
                },
                Datacenter = new() {
                    Nq = this.nq?.recentPurchase?.dc,
                    Hq = this.hq?.recentPurchase?.dc,
                },
                Region = new() {
                    Nq = this.nq?.recentPurchase?.region,
                    Hq = this.hq?.recentPurchase?.region,
                }
            },
            AverageSalePrice = new() {
                World = new() {
                    Nq = this.nq?.averageSalePrice?.world?.price > 0 ? this.nq?.averageSalePrice?.world?.price : null,
                    Hq = this.hq?.averageSalePrice?.world?.price > 0 ? this.hq?.averageSalePrice?.world?.price : null,
                },
                Datacenter = new() {
                    Nq = this.nq?.averageSalePrice?.dc?.price > 0 ? this.nq?.averageSalePrice?.dc?.price : null,
                    Hq = this.hq?.averageSalePrice?.dc?.price > 0 ? this.hq?.averageSalePrice?.dc?.price : null,
                },
                Region = new() {
                    Nq = this.nq?.averageSalePrice?.region?.price > 0 ? this.nq?.averageSalePrice?.region?.price : null,
                    Hq = this.hq?.averageSalePrice?.region?.price > 0 ? this.hq?.averageSalePrice?.region?.price : null,
                }
            },
            DailySaleVelocity = new() {
                World = new() {
                    Nq = this.nq?.dailySaleVelocity?.world?.quantity > 0 ? this.nq?.dailySaleVelocity?.world?.quantity : null,
                    Hq = this.hq?.dailySaleVelocity?.world?.quantity > 0 ? this.hq?.dailySaleVelocity?.world?.quantity : null,
                },
                Datacenter = new() {
                    Nq = this.nq?.dailySaleVelocity?.dc?.quantity > 0 ? this.nq?.dailySaleVelocity?.dc?.quantity : null,
                    Hq = this.hq?.dailySaleVelocity?.dc?.quantity > 0 ? this.hq?.dailySaleVelocity?.dc?.quantity : null,
                },
                Region = new() {
                    Nq = this.nq?.dailySaleVelocity?.region?.quantity > 0 ? this.nq?.dailySaleVelocity?.region?.quantity : null,
                    Hq = this.hq?.dailySaleVelocity?.region?.quantity > 0 ? this.hq?.dailySaleVelocity?.region?.quantity : null,
                },
            }
        };
        return marketBoardData;
    }
}

file class Aggregate {
    public Value? minListing { get; set; }
    public Value? recentPurchase { get; set; }
    public SaleValue? averageSalePrice { get; set; }
    public Value? dailySaleVelocity { get; set; }
}

file class Value {
    public Entry? world { get; set; }
    public Entry? dc { get; set; }
    public Entry? region { get; set; }
}

file class Entry {
    public int? price { get; set; }
    public uint? worldId { get; set; }
    public UnixMilliDateTime? timestamp { get; set; }
    public double? quantity { get; set; }

    public static implicit operator Listing?(Entry? e) {
        if (e == null || e.price == null)
            return null;
        string? world = null, datacenter = null;
        if (e.worldId != null) {
            (world, datacenter, _) = UniversalisClientV2.WorldLookup[e.worldId.Value];
        }

        return new Listing { Price = e.price.Value, Time = e.timestamp, World = world, Datacenter = datacenter };
    }

    public Listing? ToListing(DateTime defaultTime, Dictionary<uint, DateTime> worldUploadTimes) {
        var listing = (Listing?)this;
        if (listing == null)
            return null;
        var time = worldId != null ? worldUploadTimes.GetValueOrDefault(worldId.Value, defaultTime) : defaultTime;
        return listing with { Time = time };
    }
}

file class SaleValue {
    public SaleEntry? world { get; set; }
    public SaleEntry? dc { get; set; }
    public SaleEntry? region { get; set; }
}

file class SaleEntry {
    public double? price { get; set; }
}

file class WorldUploadTime {
    public required uint worldId { get; set; }
    public required UnixMilliDateTime timestamp { get; set; }
}
// ReSharper restore all
