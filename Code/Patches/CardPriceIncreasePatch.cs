using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Hooks;

namespace BetterAscension.Patches;

/// <summary>
/// A16 · 通货膨胀+ —— 商店售卖的卡牌价格提升 20%（只影响卡牌，遗物/药水不加价）。
///
/// 为什么用官方钩子而不是改 <c>CalcCost</c>
/// --------------------------------------
/// <c>MerchantEntry.Cost</c> 的 getter 里有本体给模组留的价格入口：
/// <code>
/// if (player.RunState.CurrentRoom is MerchantRoom)
///     value = Hook.ModifyMerchantPrice(runState, player, this, _cost);
/// </code>
/// 实测签名 <c>ModifyMerchantPrice(IRunState, Player, MerchantEntry, Decimal) -> Decimal</c>，**同步**。
///
/// 对比补 <c>MerchantCardEntry.CalcCost</c> 的 Postfix：那里面的顺序是
/// <code>
/// _cost = RoundToInt(GetCost(card) × rng(0.95f, 1.05f));
/// if (IsOnSale) _cost /= 2;          ← 折半在方法内部最后一步
/// </code>
/// 在它上面挂 Postfix 会与打折的**取整时机**、以及与其它改价模组的**叠加顺序**打架 ——
/// 而这些都不是我们能控制的。走官方钩子则什么都不用管。
/// </summary>
[HarmonyPatch(typeof(Hook), nameof(Hook.ModifyMerchantPrice))]
internal static class CardPriceIncreasePatch
{
    /// <summary>卡牌涨价倍率（A16 的数值）。1.2 = +20%。</summary>
    private const decimal Multiplier = 1.2m;

    private static int _diagBudget = 8;

    [HarmonyPostfix]
    internal static void Postfix(MerchantEntry entry, ref decimal __result)
    {
        try
        {
            if (!AscensionLevelRegistry.IsActiveRaw(AscensionLevelRegistry.InflationPlus))
            {
                return;
            }

            // 只对卡牌生效 —— 遗物与药水不受影响（设计已确认）。
            if (entry is not MerchantCardEntry)
            {
                return;
            }

            var before = __result;
            __result = Math.Round(__result * Multiplier, MidpointRounding.AwayFromZero);

            if (_diagBudget-- > 0)
            {
                ModLog.Info($"A16 通货膨胀+：卡牌价格 {before} → {__result}。");
            }
        }
        catch (Exception ex)
        {
            ModLog.Error("A16 商店涨价补丁异常（已忽略）。", ex);
        }
    }
}
