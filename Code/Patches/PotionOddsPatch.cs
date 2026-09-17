using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Odds;
using MegaCrit.Sts2.Core.Rooms;

namespace BetterAscension.Patches;

/// <summary>
/// A17 的共用数值与施加逻辑。
///
/// 真实机制（读实测 IL 还原，与反编译源码**不同**）
/// ------------------------------------------------
/// <code>
/// Roll(Player player, RoomType roomType) -> Boolean       ← 实测没有 AscensionManager 参数
///   if (Hook.ShouldForcePotionReward(...)) return true;   ← 强制时提前返回，连 RNG 都不消耗
///   bonus     = (roomType == RoomType.Elite) ? 0.25f : 0f;
///   threshold = CurrentValue + bonus * 0.5f;
///   roll      = _rng.NextFloat(1f);
///   if (roll &lt; threshold) { CurrentValue -= 0.1f; return true;  }
///   else                   { CurrentValue += 0.1f; return false; }
/// </code>
/// 也就是说**没有固定概率** —— <c>CurrentValue</c> 一直在自适应漂移
/// （掉落后 -0.1、不掉则 +0.1），<c>0.4f</c> 只是**起点**。
/// 设计文档说的"特殊机制（不掉落提升、掉落降低）"就是这个 ±0.1，**我们不动它**，
/// 只把起点压下来。
/// </summary>
internal static class PotionOdds
{
    /// <summary>
    /// A17 的药水掉落起点概率：**30%**（原版起点 40%）。
    ///
    /// 验证过程：曾临时改成 <c>1.0f</c>（必定掉落）以确认补丁确实生效 ——
    /// 概率类效果靠肉眼无法判断。实测确认生效后改回设计值 30%。
    /// </summary>
    public const float ReducedStart = 0.3f;

    private static int _diagBudget = 3;

    /// <summary>把某个 <see cref="PlayerOddsSet"/> 的药水概率压到 A17 的起点值。</summary>
    public static void Apply(PlayerOddsSet? odds, string source)
    {
        try
        {
            if (odds?.PotionReward is null)
            {
                return;
            }

            if (!AscensionLevelRegistry.IsActiveRaw(AscensionLevelRegistry.Rarity))
            {
                return;
            }

            var before = odds.PotionReward.CurrentValue;
            odds.PotionReward.OverrideCurrentValue(ReducedStart);

            if (_diagBudget-- > 0)
            {
                ModLog.Info($"A17 珍稀（{source}）：药水掉落起点概率 {before:P0} → {ReducedStart:P0}"
                          + $"（进阶 {AscensionLevelRegistry.RawLevel}）。");
            }
        }
        catch (Exception ex)
        {
            ModLog.Error($"A17 药水掉率补丁异常（来源 {source}，已忽略）。", ex);
        }
    }
}

/// <summary>
/// A17 · 新开局路径：<c>PlayerOddsSet(PlayerRngSet rng)</c> 构造函数。
///
/// ⚠️ 为什么改挂 <c>PlayerOddsSet</c> 而不是 <c>PotionRewardOdds</c> 的构造函数
/// -------------------------------------------------------------------------
/// 第一版补在 <c>PotionRewardOdds</c> 的构造函数上，还带了
/// "只在 <c>CurrentValue</c> 等于 0.4 时才改"的判断 —— 实测**完全没有日志**，
/// 说明要么那条构造路径没走到，要么走到了但不是我们以为的那条。
///
/// <c>PlayerOddsSet</c> 才是**玩家概率的持有者**，新开局与读档都要经过它，
/// 所以改挂这里覆盖面更稳，也不再依赖"恰好等于 0.4"这个脆弱假设。
/// </summary>
[HarmonyPatch]
internal static class PotionOddsCtorPatch
{
    [HarmonyTargetMethod]
    internal static MethodBase? Target() =>
        AccessTools.Constructor(typeof(PlayerOddsSet), [typeof(MegaCrit.Sts2.Core.Random.PlayerRngSet)])
        ?? AccessTools.Constructor(typeof(PlayerOddsSet), Type.EmptyTypes);

    /// <remarks>构造函数用 <c>__instance</c> 取到刚建好的对象。</remarks>
    [HarmonyPostfix]
    internal static void Postfix(PlayerOddsSet __instance) => PotionOdds.Apply(__instance, "新开局");
}

/// <summary>
/// A17 · 读档路径：<c>PlayerOddsSet.FromSerializable(save, rng)</c>。
///
/// 设计上"读档也一并降"，语义是"这一阶本来就该少掉药水"，
/// 而不是"只有新开的局才算数"。
/// </summary>
[HarmonyPatch]
internal static class PotionOddsFromSavePatch
{
    [HarmonyTargetMethod]
    internal static MethodBase? Target() => AccessTools.Method(typeof(PlayerOddsSet), "FromSerializable");

    /// <remarks>静态方法用 <c>__result</c> 取到返回的对象。</remarks>
    [HarmonyPostfix]
    internal static void Postfix(PlayerOddsSet __result) => PotionOdds.Apply(__result, "读档");
}

/// <summary>
/// A17 的**验证用**诊断：每次 <c>Roll</c> 都把当前阈值打出来。
///
/// 保留它的理由：以后如果怀疑 A17 没生效，把 <see cref="Budget"/> 调大就能在日志里
/// 直接看到"当前阈值是 30%"，而不用进游戏靠感觉判断。
/// 平时保持小额度，避免刷屏。
/// </summary>
[HarmonyPatch(typeof(PotionRewardOdds), nameof(PotionRewardOdds.Roll))]
internal static class PotionOddsRollDiagnosticPatch
{
    /// <summary>要打几条。设成 0 即可完全关闭。</summary>
    private const int Budget = 3;

    private static int _remaining = Budget;

    [HarmonyPostfix]
    internal static void Postfix(PotionRewardOdds __instance, RoomType roomType, bool __result)
    {
        try
        {
            if (_remaining <= 0)
            {
                return;
            }

            _remaining--;
            ModLog.Info($"A17 诊断：药水判定 room={roomType} 当前阈值={__instance.CurrentValue:P0} "
                      + $"结果={(__result ? "掉落" : "不掉")}"
                      + (_remaining == 0 ? "（后续同类日志已省略）" : ""));
        }
        catch
        {
            // 诊断本身绝不能影响游戏
        }
    }
}
