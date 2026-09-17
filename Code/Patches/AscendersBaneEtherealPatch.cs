using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models.Cards;

namespace BetterAscension.Patches;

/// <summary>
/// A15 · 进阶之灾+ —— 进阶之灾失去虚无（Ethereal）。
///
/// 落点依据
/// --------
/// 实测（tools\ApiDump type AscendersBane）这张诅咒的
/// <c>CanonicalKeywords</c> 是 <c>{ Eternal, Unplayable, Ethereal }</c>，
/// getter 的 IL 是：
/// <code>
/// ldc.i4.3 ; newarr CardKeyword
/// ldtoken &lt;PrivateImplementationDetails&gt;::8CD4...  : __StaticArrayInitTypeSize=12
/// call RuntimeHelpers::InitializeArray
/// newobj &lt;&gt;z__ReadOnlyArray`1::.ctor
/// ret
/// </code>
///
/// ⚠️ 关键字数组来自 <c>&lt;PrivateImplementationDetails&gt;</c> 里的**共享静态初始化数组**。
///    所以**绝不能**在 Prefix 里原地改那个数组 —— 那会污染所有实例（含已经生成过的牌）。
///    正确做法是 Postfix **替换返回的序列**。
///
/// 顺带确认：A15 激活时 A5（进阶之灾）必定激活，因为 STS2 的高阶包含所有低阶效果，
/// 所以不必处理"A15 单独开、场上没有这张诅咒"的情况。
/// </summary>
[HarmonyPatch(typeof(AscendersBane), nameof(AscendersBane.CanonicalKeywords), MethodType.Getter)]
internal static class AscendersBaneEtherealPatch
{
    private static int _diagBudget = 4;

    [HarmonyPostfix]
    internal static void Postfix(ref IEnumerable<CardKeyword> __result)
    {
        try
        {
            if (!AscensionLevelRegistry.IsActiveRaw(AscensionLevelRegistry.AscendersBanePlus))
            {
                return;
            }

            var original = __result;
            if (original is null)
            {
                return;
            }

            var filtered = original.Where(k => k != CardKeyword.Ethereal).ToArray();
            if (filtered.Length == original.Count())
            {
                // 没有虚无可去 —— 说明游戏已经改过这张牌的关键字
                if (_diagBudget-- > 0)
                {
                    ModLog.Warn("A15 进阶之灾+：这张牌本来就没有 [虚无] 关键字，无事可做。"
                              + "游戏版本可能已改动 AscendersBane。");
                }
                return;
            }

            __result = filtered;

            if (_diagBudget-- > 0)
            {
                ModLog.Info($"A15 进阶之灾+：已移除 [虚无]，剩余关键字 "
                          + $"[{string.Join(", ", filtered)}]。");
            }
        }
        catch (Exception ex)
        {
            ModLog.Error("A15 虚无移除补丁异常（已忽略）。", ex);
        }
    }
}
