using HarmonyLib;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Runs;

namespace BetterAscension.Patches;

/// <summary>
/// A12 · 水土不服 —— 每进入新的阶段（第 2、3 幕）最大生命值 -6。
///
/// ★ 为什么挂在 <c>Hook.BeforeRoomEntered</c>
/// ========================================
/// 这个挂载点同时解决了**两个**问题：触发时机，以及 SL 一致性。
///
/// **问题 1：第一版挂在 <c>RunManager.EnterNextAct</c> 的 Postfix，完全不触发。**
/// 它返回 <c>Task</c>，而 Harmony 的 Postfix 在方法体执行完（状态机刚启动、还没换幕）
/// 时就运行了，那时 <c>state.CurrentActIndex</c> 还是旧值 0 → 永远停在"第 0 幕，跳过"。
///
/// **问题 2：第二版挂在 <c>RunState.CurrentActIndex</c> 的 setter，能生效，但 SL 后会重复扣血。**
/// 因为内存态 <c>HashSet</c> 不随存档清空，而扣血时机与存档时机不一致 ——
/// 存档里是"未扣血"的状态，读档后集合却认为"这一幕已扣过" → 状态不一致。
///
/// **正解**：改挂 <c>Hook.BeforeRoomEntered</c>，它在**存档之前**执行。实测链条：
/// <code>
/// ActChangeSynchronizer.MoveToNextAct
///   → RunManager.EnterNextAct
///     → RunState.set_CurrentActIndex(新幕号)          ← 幕号已更新，我们能读到
///     → RunManager.EnterRoom
///       → RunManager.EnterRoomInternal
///           RunState.PushRoom(room)
///           Hook::BeforeRoomEntered(runState, room)    ← ★ 我们在这里扣血
///   → ... 随后 SaveManager.SaveRun(...)                 ← 扣血已被算进这次存档
/// </code>
///
/// 这个钩子也正好覆盖设计要求的语义："进入新的阶段时"= 进入第 2/3 幕的第一个房间。
///
/// 减上限走同步内核
/// ----------------
/// 三条常规入口全是 <c>Task</c>：
/// <code>
/// CreatureCmd.LoseMaxHp / SetMaxHp / GainMaxHp   → 全是 Task
/// </code>
/// 但实测 <c>Creature.SetMaxHpInternal(Decimal) -> Void</c> **是纯同步的**，其 IL 是：
/// <code>
/// if (amount &lt; 0) throw new ArgumentException("amount must be non-negative.")
/// MaxHp     = Math.Min((int)amount, 999999999)
/// CurrentHp = Math.Min(CurrentHp, MaxHp)      ← 自动夹取当前生命
/// </code>
/// 本体自己就这么用：<c>Player.cs</c> 里 <c>this.Creature.SetMaxHpInternal(player.MaxHp)</c>。
///
/// ⚠️ 它是"设**绝对值**"而非"减 N"：写成 <c>SetMaxHpInternal(creature.MaxHp - 6)</c>，
///    并且**必须自己保证结果 ≥ 0**，否则它会抛 <c>ArgumentException</c>。
///
/// ⚠️ 剩余限制：幂等标记仍是内存态。它现在与存档一致（扣血早于存档），
///    但若将来发现"换幕后、扣血前"还有别的存档路径，需要重新审视。
/// </summary>
[HarmonyPatch(typeof(Hook), nameof(Hook.BeforeRoomEntered))]
internal static class ActAttritionPatch
{
    /// <summary>每次换幕损失的最大生命（A12 的数值）。</summary>
    private const int MaxHpLoss = 6;

    /// <summary>最大生命下限，保证传给 SetMaxHpInternal 的值非负。</summary>
    private const int MinimumMaxHp = 1;

    /// <summary>
    /// 幂等保护：记录**本局**已经扣过血的幕号。
    ///
    /// ★ 这里曾经是 <c>static readonly HashSet&lt;int&gt;</c>，那是错的
    /// ------------------------------------------------
    /// 静态集合的生命周期是**整个游戏进程**，跨存档、跨新局都不重置；
    /// 而幕号（1、2）每局都一样 → 第二局起全部被判定为"已扣过" → A12 再也不生效。
    /// CY 实测的现象正是"再开新档连第 2 幕也不扣血，要重启游戏才恢复"。
    ///
    /// 现在改为按 <c>RunState</c> 实例挂载（<see cref="RunBookkeeping"/>）：
    /// 一局一个集合，读档/新局自动拿到干净的，无需手动重置。
    /// </summary>
    private static int _diagBudget = 10;

    [HarmonyPrefix]
    internal static void Prefix()
    {
        try
        {
            if (!AscensionLevelRegistry.IsActiveRaw(AscensionLevelRegistry.Attrition))
            {
                return;
            }

            var state = RunManager.Instance?.State;
            if (state is null)
            {
                return;
            }

            var act = state.CurrentActIndex;

            // 第 0 幕是开局，不扣。设计已确认："第 2、3 幕各一次"。
            if (act <= 0)
            {
                return;
            }

            var book = RunBookkeeping.For(state);
            if (book is null)
            {
                return;
            }

            if (!book.AttritionAppliedActs.Add(act))
            {
                return;   // 本局这一幕已经扣过
            }

            Diag($"进入第 {act + 1} 幕的第一个房间，开始扣最大生命。");

            foreach (var player in state.Players)
            {
                try
                {
                    var creature = player?.Creature;
                    if (creature is null)
                    {
                        continue;
                    }

                    var before = creature.MaxHp;
                    var target = before - MaxHpLoss;
                    if (target < MinimumMaxHp)
                    {
                        target = MinimumMaxHp;
                    }

                    if (target == before)
                    {
                        continue;
                    }

                    creature.SetMaxHpInternal(target);

                    // ★ 回读并打印 —— 这行本身就是断言
                    ModLog.Info($"A12 水土不服：进入第 {act + 1} 幕，"
                              + $"最大生命 {before} → {creature.MaxHp}"
                              + $"（当前生命 {creature.CurrentHp}）。");
                }
                catch (Exception ex)
                {
                    ModLog.Error($"A12：对玩家 {player?.NetId} 扣最大生命时异常（继续处理其它玩家）。", ex);
                }
            }
        }
        catch (Exception ex)
        {
            ModLog.Error("A12 换幕扣血补丁异常（已忽略）。", ex);
        }
    }

    private static void Diag(string message)
    {
        if (_diagBudget <= 0)
        {
            return;
        }

        _diagBudget--;
        ModLog.Info($"A12 诊断：{message}" + (_diagBudget == 0 ? "（后续同类日志已省略）" : ""));
    }
}
