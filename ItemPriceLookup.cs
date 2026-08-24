using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EasyCaching.InMemory;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace PriceInsight;

public class ItemPriceLookup : IDisposable {
    private static readonly TimeSpan SuccessfulCacheDuration = TimeSpan.FromMinutes(90);
    private static readonly TimeSpan EmptyCacheDuration = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan FailureCooldown = TimeSpan.FromMinutes(5);

    private readonly InMemoryCaching cache = new("prices", new InMemoryCachingOptions { EnableReadDeepClone = false });
    private readonly ConcurrentQueue<uint> requestedItems = new();
    private readonly ConcurrentQueue<uint> priorityRequestedItems = new();
    private readonly ConcurrentDictionary<uint, (Task Task, CancellationTokenSource Token)> activeTasks = new();
    private readonly ConcurrentDictionary<uint, DateTime> failedItems = new();
    private readonly PriceInsightPlugin plugin;
    private readonly CancellationTokenSource cancellationTokenSource = new();
    private uint? homeWorldId;

    public ItemPriceLookup(PriceInsightPlugin plugin) {
        this.plugin = plugin;
        Task.Run(ProcessQueue, cancellationTokenSource.Token)
            // Silently ignore cancel
            .ContinueWith(_ => { }, TaskContinuationOptions.OnlyOnCanceled);
    }

    public bool CheckReady() {
        if (plugin.Configuration.UseCurrentWorld) {
            homeWorldId ??= Service.ClientState.LocalPlayer?.CurrentWorld.RowId;
        } else {
            homeWorldId ??= Service.ClientState.LocalPlayer?.HomeWorld.RowId;
        }

        return homeWorldId != null;
    }

    public (MarketBoardData? MarketBoardData, LookupState State) Get(ulong fullItemId, bool refresh) {
        if (!ToMarketableItemId(fullItemId, out var itemId))
            return (null, LookupState.NonMarketable);

        if (refresh) {
            cache.Remove(itemId.ToString());
            failedItems.TryRemove(itemId, out _);
            if (activeTasks.TryRemove(itemId, out var t))
                t.Token.Cancel();
        } else {
            if (cache.Get<MarketBoardData>(itemId.ToString()) is { IsNull: false, Value: var mbData })
                return (mbData, LookupState.Marketable);
            if (activeTasks.TryGetValue(itemId, out var t))
                return (null, t.Task.IsFaulted ? LookupState.Faulted : LookupState.Marketable);
            if (failedItems.TryGetValue(itemId, out var failedAt)) {
                if (DateTime.UtcNow - failedAt < FailureCooldown)
                    return (null, LookupState.Faulted);
                failedItems.TryRemove(itemId, out _);
            }
        }

        // Direct tooltip requests take priority over background inventory prefetches.
        if (!priorityRequestedItems.Contains(itemId))
            priorityRequestedItems.Enqueue(itemId);

        return (null, LookupState.Marketable);
    }

    private static bool ToMarketableItemId(ulong fullItemId, out uint itemId, ExcelSheet<Item>? sheet = null) {
        itemId = (uint)(fullItemId % 500000);
        if (fullItemId is >= 2000000 or >= 500000 and < 1000000)
            return false;
        sheet ??= Service.DataManager.Excel.GetSheet<Item>();
        return sheet.GetRowOrDefault(itemId) is not null and not { ItemSearchCategory.RowId: 0 };
    }

    public void Fetch(IEnumerable<uint> items) {
        var itemSheet = Service.DataManager.Excel.GetSheet<Item>();
        foreach (var id in items) {
            if (!ToMarketableItemId(id, out var itemId, itemSheet))
                continue;
            if (cache.Get(itemId.ToString()) != null || (activeTasks.TryGetValue(itemId, out var t) && !t.Task.IsFaulted))
                continue;
            if (failedItems.TryGetValue(itemId, out var failedAt) && DateTime.UtcNow - failedAt < FailureCooldown)
                continue;
            if (!requestedItems.Contains(itemId))
                requestedItems.Enqueue(itemId);
        }
    }

    private async Task ProcessQueue() {
        var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(200));
        while (await timer.WaitForNextTickAsync(cancellationTokenSource.Token)) {
            if (priorityRequestedItems.IsEmpty && requestedItems.IsEmpty)
                continue;
            var items = new HashSet<uint>();
            // Do not combine an interactive lookup with a potentially slow prefetch batch.
            var queue = priorityRequestedItems.IsEmpty ? requestedItems : priorityRequestedItems;
            var limit = ReferenceEquals(queue, priorityRequestedItems) ? 10 : 30;
            while (items.Count < limit && queue.TryDequeue(out var item)) {
                if (cache.Get(item.ToString()) == null && !activeTasks.ContainsKey(item))
                    items.Add(item);
            }
            if (items.Count == 0)
                continue;
            await FetchInternal(items);
        }

        timer.Dispose();
    }

    private Task<Dictionary<uint, MarketBoardData>?> FetchInternal(ICollection<uint> itemIds) {
        var token = CancellationTokenSource.CreateLinkedTokenSource(cancellationTokenSource.Token);
        var itemTask = FetchItemTask();

        foreach (var id in itemIds) {
            var task = Task.Run(async () => {
                var items = await itemTask;
                if (items != null && items.TryGetValue(id, out var value)) {
                    var duration = value.HasAnyData() ? SuccessfulCacheDuration : EmptyCacheDuration;
                    cache.Set(id.ToString(), value, duration);
                    failedItems.TryRemove(id, out _);
                } else {
                    failedItems[id] = DateTime.UtcNow;
                }
                activeTasks.TryRemove(id, out _);
            }, token.Token);
            task.ContinueWith(_ => { }, TaskContinuationOptions.OnlyOnCanceled);
            activeTasks[id] = (task, token);
        }

        return itemTask;

        async Task<Dictionary<uint, MarketBoardData>?> FetchItemTask() {
            if (!homeWorldId.HasValue)
                return null;
            var fetchStart = DateTime.Now;
            var result = await plugin.UniversalisClientV2.GetMarketBoardDataList(homeWorldId.Value, itemIds, token.Token);
            if (result != null) {
                plugin.ItemPriceTooltip.Refresh(result);
                var unresolvedItems = itemIds.Where(id => !result.ContainsKey(id)).ToArray();
                if (unresolvedItems.Length > 0)
                    plugin.ItemPriceTooltip.FetchFailed(unresolvedItems);
            } else {
                plugin.ItemPriceTooltip.FetchFailed(itemIds);
            }
            Service.PluginLog.Debug($"Fetching {itemIds.Count} items took {(DateTime.Now - fetchStart).TotalMilliseconds:F0}ms");
            return result;
        }
    }

    public void Dispose() {
        cancellationTokenSource.Cancel();
    }
}
