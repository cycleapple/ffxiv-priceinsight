using System;
using Dalamud.Bindings.ImGui;

namespace PriceInsight;

internal class ConfigUI(PriceInsightPlugin plugin) : IDisposable {
    private bool settingsVisible = false;

    public bool SettingsVisible {
        get => settingsVisible;
        set => settingsVisible = value;
    }

    public void Dispose() {
    }

    public void Draw() {
        if (!SettingsVisible) {
            return;
        }

        var conf = plugin.Configuration;
        if (ImGui.Begin("Price Insight 設定", ref settingsVisible,
                ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.AlwaysAutoResize)) {
            var configValue = conf.RefreshWithAlt;
            if (ImGui.Checkbox("按下 Alt 重新整理價格", ref configValue)) {
                conf.RefreshWithAlt = configValue;
                conf.Save();
            }

            configValue = conf.PrefetchInventory;
            if (ImGui.Checkbox("預先取得持有物品的價格", ref configValue)) {
                conf.PrefetchInventory = configValue;
                conf.Save();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("登入時預先取得物品欄、陸行鳥鞍囊及雇員所持全部物品的價格。");

            configValue = conf.UseCurrentWorld;
            if (ImGui.Checkbox("將目前世界視為所屬世界", ref configValue)) {
                conf.UseCurrentWorld = configValue;
                conf.Save();
                plugin.ClearCache();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("將你目前所在的世界視為「所屬世界」。\n跨資料中心旅行時，可用來查看當地價格。");

            ImGui.Separator();
            ImGui.PushID(0);

            ImGui.Text("顯示下列範圍的最低價格：");

            configValue = conf.ShowRegion;
            if (ImGui.Checkbox("地區", ref configValue)) {
                conf.ShowRegion = configValue;
                conf.Save();
            }
            TooltipRegion();

            configValue = conf.ShowDatacenter;
            if (ImGui.Checkbox("資料中心", ref configValue)) {
                conf.ShowDatacenter = configValue;
                conf.Save();
            }

            configValue = conf.ShowWorld;
            if (ImGui.Checkbox("所屬世界", ref configValue)) {
                conf.ShowWorld = configValue;
                conf.Save();
            }

            ImGui.PopID();
            ImGui.Separator();
            ImGui.PushID(1);

            ImGui.Text("顯示下列範圍的最近成交：");

            configValue = conf.ShowMostRecentPurchaseRegion;
            if (ImGui.Checkbox("地區", ref configValue)) {
                conf.ShowMostRecentPurchaseRegion = configValue;
                conf.Save();
            }
            TooltipRegion();

            configValue = conf.ShowMostRecentPurchase;
            if (ImGui.Checkbox("資料中心", ref configValue)) {
                conf.ShowMostRecentPurchase = configValue;
                conf.Save();
            }

            configValue = conf.ShowMostRecentPurchaseWorld;
            if (ImGui.Checkbox("所屬世界", ref configValue)) {
                conf.ShowMostRecentPurchaseWorld = configValue;
                conf.Save();
            }

            ImGui.PopID();
            ImGui.Separator();

            var selectValue = conf.ShowDailySaleVelocityIn;
            if (ImGui.Combo("顯示每日成交量", ref selectValue, ["不顯示", "世界", "資料中心", "地區"])) {
                conf.ShowDailySaleVelocityIn = selectValue;
                conf.Save();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("依最近 4 天的成交紀錄顯示平均每日成交量。");

            selectValue = conf.ShowAverageSalePriceIn;
            if (ImGui.Combo("顯示平均成交價格", ref selectValue, ["不顯示", "世界", "資料中心", "地區"])) {
                conf.ShowAverageSalePriceIn = selectValue;
                conf.Save();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("依最近 4 天的成交紀錄顯示平均成交價格。");

            configValue = conf.ShowStackSalePrice;
            if (ImGui.Checkbox("顯示整組售價", ref configValue)) {
                conf.ShowStackSalePrice = configValue;
                conf.Save();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("依顯示的單價計算滑鼠所指整組物品的售價。");

            configValue = conf.ShowAge;
            if (ImGui.Checkbox("顯示資料更新時間", ref configValue)) {
                conf.ShowAge = configValue;
                conf.Save();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("顯示價格資訊最後更新至今的時間。\n可停用以縮短物品說明。");

            configValue = conf.ShowDatacenterOnCrossWorlds;
            if (ImGui.Checkbox("其他世界顯示資料中心", ref configValue)) {
                conf.ShowDatacenterOnCrossWorlds = configValue;
                conf.Save();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("顯示整個地區的價格時，為其他資料中心的世界標示其資料中心。\n可停用以縮短物品說明。");

            configValue = conf.ShowBothNqAndHq;
            if (ImGui.Checkbox("永遠同時顯示 NQ 與 HQ 價格", ref configValue)) {
                conf.ShowBothNqAndHq = configValue;
                conf.Save();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("同時顯示物品的 NQ 與 HQ 價格。\n停用時僅顯示目前品質的價格（按 Ctrl 可切換 NQ 與 HQ）。");
        }

        ImGui.End();
    }

    private static void TooltipRegion() {
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("包含可透過資料中心旅行前往的所有資料中心。");
    }
}
