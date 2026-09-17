using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Gold;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;

namespace BetterAscension.Patches;

/// <summary>
/// A13 · ？富有？ —— 在商人处**购买卡牌后**扣除 15 金币小费（不会使金币降到 0 以下）。
///
/// 落点依据
/// --------
/// <c>MerchantEntry.InvokePurchaseCompleted(MerchantEntry entry) -> Void</c> 是
/// **购买成功的同步收口点**：本体自己也在 <c>MerchantInventory</c> 和各个
/// <c>NMerchantCard</c> / <c>NMerchantPotion</c> / <c>NMerchantRelic</c> 节点上订阅它。
///
/// 为什么不挂在购买流程上：<c>MerchantEntry.OnTryPurchaseWrapper</c> 返回 <c>Task&lt;bool&gt;</c>，
/// 它的 Postfix 会在任务**还没跑完**时就执行，那时既不知道最终结果、也不该扣钱。
/// 而这个方法是纯同步的，且只在成功时被调用 —— 正是我们要的时机。
///
/// 扣款用 <c>PlayerCmd.LoseGold</c>
/// ------------------------------
/// 实测它的源码是：
/// <code>
/// player.Gold = int.Max(0, player.Gold - (int)amount);   // ← 立即执行
/// return Task.CompletedTask;
/// </code>
/// 也就是说它**虽然返回 Task，实际是同步的**，而且那个 <c>int.Max(0, …)</c>
/// **天然实现了设计要求的"金币不会降到 0 以下"** —— 不用自己夹取。
/// 传 <c>GoldLossType.Spent</c>（=1）让它在跑局历史里正确记为"消费"而非"损失"。
/// </summary>
[HarmonyPatch(typeof(MerchantEntry), nameof(MerchantEntry.InvokePurchaseCompleted))]
internal static class MerchantTipPatch
{
    /// <summary>小费金额（A13 的数值）。</summary>
    private const int TipAmount = 15;

    /// <summary>
    /// 店主玩家的取值器。
    ///
    /// <c>MerchantEntry._player</c> 是 <c>protected readonly Player</c> 字段 ——
    /// 实测 Publicizer 并没有把它公开（它只公开了类型/方法级成员），
    /// 而 <c>MerchantEntry</c> 上也没有公开的 <c>Player</c> 属性。
    /// 所以用反射读一次字段。购买是低频事件，这点开销可忽略。
    /// </summary>
    private static readonly FieldInfo? PlayerField =
        AccessTools.Field(typeof(MerchantEntry), "_player");

    private static Player? GetPlayer(MerchantEntry entry)
    {
        if (PlayerField is null)
        {
            return null;
        }

        try
        {
            return PlayerField.GetValue(entry) as Player;
        }
        catch
        {
            return null;
        }
    }

    [HarmonyPostfix]
    internal static void Postfix(MerchantEntry entry)
    {
        try
        {
            if (!AscensionLevelRegistry.IsActiveRaw(AscensionLevelRegistry.Greed))
            {
                return;
            }

            // 只对卡牌收小费 —— 遗物与药水不受影响（设计已确认）。
            // 注意：删牌服务是 MerchantCardRemovalEntry，不是 MerchantCardEntry，所以不会被收。
            if (entry is not MerchantCardEntry)
            {
                return;
            }

            var player = GetPlayer(entry);
            if (player is null)
            {
                ModLog.Warn("A13 富有：读不到店主玩家（MerchantEntry._player），跳过小费。");
                return;
            }

            var before = player.Gold;

            // 返回的 Task 已完成，无需 await；这里不取结果也能保证扣款已发生。
            _ = PlayerCmd.LoseGold(TipAmount, player, GoldLossType.Spent);

            var after = player.Gold;
            var actual = before - after;

            // ★ 回读实际扣除额 —— 金币本来就不足时 actual 会小于 TipAmount，
            //   把这行打出来才能确认"不降到 0 以下"真的生效了。
            ModLog.Info($"A13 富有：购买卡牌扣小费 {actual} 枚金币（{before} → {after}，"
                      + $"应收 {TipAmount}）。");
        }
        catch (Exception ex)
        {
            ModLog.Error("A13 小费补丁异常（已忽略）。", ex);
        }
    }
}
