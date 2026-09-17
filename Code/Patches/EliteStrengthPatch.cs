using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace BetterAscension.Patches;

/// <summary>
/// A11 · 精英强敌 —— 精英敌人在战斗开始时获得 1 点力量。
///
/// 落点依据
/// --------
/// <c>MonsterModel.AfterAddedToRoom()</c> 正是本体给怪物挂先天能力
/// （Artifact、Slippery…）的钩子：生物已入场、玩家还没开始第一回合。
/// 实测（tools\ApiDump callers）调用链是
/// <c>CombatManager.StartCombatInternal</c> → <c>CombatManager.AfterCreatureAdded</c>
/// → <c>Creature.AfterAddedToRoom</c> → <c>MonsterModel.AfterAddedToRoom</c>。
///
/// 力量因此在敌人身上可见，也会被伤害预览正确计算，不需要额外 tick。
/// </summary>
[HarmonyPatch(typeof(MonsterModel), nameof(MonsterModel.AfterAddedToRoom))]
internal static class EliteStrengthPatch
{
    /// <summary>精英开局获得的力量层数（A11 的数值，想调就改这里）。</summary>
    private const int StrengthAmount = 1;

    /// <summary>诊断日志上限：确认钩子活着，又不会刷屏。</summary>
    private static int _diagBudget = 12;

    [HarmonyPostfix]
    internal static void Postfix(MonsterModel __instance)
    {
        try
        {
            if (!AscensionLevelRegistry.IsActiveRaw(AscensionLevelRegistry.EliteStrength))
            {
                return;
            }

            var creature = __instance?.Creature;
            if (creature is null)
            {
                return;
            }

            var isElite = IsEliteRoom();
            if (!isElite)
            {
                LogSkip(__instance!, creature);
                return;
            }

            var now = PowerGiver.Give<StrengthPower>(creature, StrengthAmount, "A11 精英强化");
            if (now >= 0)
            {
                // ★ 回读并打出来 —— 这行本身就是断言。
                //   只写"called Apply"只能说明没报错，写"now 1"才叫验证过。
                ModLog.Info($"A11 精英强化：{__instance!.Title} 获得 {StrengthAmount} 点力量（现在 {now}）。");
            }
        }
        catch (Exception ex)
        {
            // 钩子在战斗主循环里，异常绝不能让出去
            ModLog.Error("A11 精英强化补丁异常（已忽略）。", ex);
        }
    }

    /// <summary>
    /// 判定当前是不是精英战。用地图点类型为主、房间类型兜底 —— 两条都实测可用。
    /// </summary>
    private static bool IsEliteRoom()
    {
        var state = RunManager.Instance?.State;
        if (state is null)
        {
            return false;
        }

        try
        {
            if (state.CurrentMapPoint?.PointType == MapPointType.Elite)
            {
                return true;
            }
        }
        catch
        {
            // CurrentMapPoint 在某些房间可能不可用，落到下面的兜底
        }

        return state.CurrentRoom?.RoomType == RoomType.Elite;
    }

    /// <summary>
    /// 限量诊断：把"钩子确实触发了、但判定不是精英房"这件事打出来。
    /// A11 工程正是靠这类日志把问题范围从"补丁没装上"缩小到"赋值那一步"。
    /// </summary>
    private static void LogSkip(MonsterModel monster, Creature creature)
    {
        if (_diagBudget <= 0)
        {
            return;
        }

        _diagBudget--;
        var state = RunManager.Instance?.State;
        var point = state?.CurrentMapPoint?.PointType.ToString() ?? "?";
        var room = state?.CurrentRoom?.RoomType.ToString() ?? "?";

        ModLog.Info($"A11 精英判定：'{monster.Title}' 跳过"
                  + $"（不是精英房；mapPoint={point}, room={room}, isEnemy={creature.IsEnemy}）。"
                  + (_diagBudget == 0 ? " 后续同类日志已省略。" : ""));
    }
}
