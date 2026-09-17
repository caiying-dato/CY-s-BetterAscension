using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;

namespace BetterAscension.Patches;

/// <summary>
/// A14 · 咬紧牙关 —— 手牌上限 -1（原版固定 10 → 9）。
///
/// 落点依据
/// --------
/// <c>CardPile.MaxCardsInHand</c> 是**唯一收口点**。实测：
/// <code>
/// prop  MaxCardsInHand : Int32 {get;}          ← 静态属性
/// get_MaxCardsInHand() IL:  ldc.i4.s 10 ; ret  ← 属性体就一条常量
/// </code>
/// <c>tools\ApiDump callers CardPile MaxCardsInHand</c> 列出 **11 处**读取者：
/// <c>CardPileCmd.DrawInternal</c> / <c>CardPileCmd.Add</c> /
/// <c>CheckIfDrawIsPossibleAndShowThoughtBubbleIfNot</c> / <c>CombatManager.SetupPlayerTurn</c>
/// + 6 张卡的 OnPlay + 控制台命令。
///
/// 所以补 getter 一处即可全覆盖 —— 不必逐个调用点打补丁，也不必改 IL。
///
/// ⚠️ 反编译源码在这里是错的
/// ------------------------
/// 源码写的是 <c>public const int maxCardsInHand = 10;</c>，但**真实 DLL 里是属性**。
/// 又一次印证："动手前实测，不要照抄源码"。若真按 const 去改 IL，会白折腾。
/// </summary>
[HarmonyPatch(typeof(CardPile), nameof(CardPile.MaxCardsInHand), MethodType.Getter)]
internal static class HandSizePatch
{
    /// <summary>A14 的手牌上限减值。</summary>
    private const int Reduction = 1;

    /// <summary>手牌下限，防止减到 0 或负数导致抽牌逻辑异常。</summary>
    private const int Minimum = 1;

    /// <summary>
    /// 日志只打一次。
    ///
    /// <c>MaxCardsInHand</c> 是**超热 getter** —— 实测一局下来被调用几百次
    /// （第一次实跑时这个补丁刷了 **166 条**同样的日志）。
    /// 补丁本身必须每次都改返回值，但日志没有理由每次都打。
    /// </summary>
    private static bool _logged;

    [HarmonyPostfix]
    internal static void Postfix(ref int __result)
    {
        try
        {
            // 先判进阶是否生效（最便宜的一步），不生效就原样放行。
            if (!AscensionLevelRegistry.IsActiveRaw(AscensionLevelRegistry.ClenchedTeeth))
            {
                return;
            }

            var reduced = __result - Reduction;
            if (reduced < Minimum)
            {
                reduced = Minimum;
            }

            if (reduced == __result)
            {
                return;
            }

            if (!_logged)
            {
                _logged = true;
                ModLog.Info($"A14 咬紧牙关：手牌上限 {__result} → {reduced}（本局只提示一次）。");
            }

            __result = reduced;
        }
        catch (Exception ex)
        {
            ModLog.Error("A14 手牌上限补丁异常（已忽略）。", ex);
        }
    }
}
