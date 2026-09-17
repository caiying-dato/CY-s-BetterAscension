using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Rooms;

namespace BetterAscension.Patches;

/// <summary>
/// 玩家侧的"战斗开始"效果：A18 脆弱、A19 虚弱。
///
/// 为什么是这个挂载点（两次失败的教训）
/// -----------------------------------
/// **第一版**挂在 <c>CombatManager.AfterCreatureAdded(Creature)</c> 的 Postfix 上，
/// 实测**完全不触发**。读它的状态机 IL 才看清原因：
/// <code>
/// IL_0070: callvirt  Creature::get_IsEnemy()
/// IL_0075: brfalse.s -&gt;196          ← 不是敌人就直接跳到收尾
/// IL_00E5: SetResult() / ret
/// </code>
/// 它**只处理敌人**（给怪物 RollMove），玩家生物根本走不进来。
/// 我当初只看到"有 IsEnemy 判定"就推断"玩家也会走"，没看那个分支跳到哪 —— 是错的。
///
/// **为什么 Prefix 可行**：<c>Hook.BeforeCombatStart</c> 返回 <c>Task</c>，
/// 但它的**同步部分**（方法体开头到第一个 await）会在 Harmony 的 Prefix 之后才执行，
/// 而 Prefix 本身在**调用方线程上同步运行** —— 也就是战斗刚开始、玩家第一回合之前。
/// 实测参数 <c>ICombatState</c> 上就有 <c>Players</c> 与 <c>PlayerCreatures</c>，
/// 我们需要的生物直接拿得到。
///
/// ⚠️ Prefix 里**绝不能** await 或同步等任何 <c>Task</c> —— 那会死锁主线程
///    （A11 工程 v0.1.3 的真实事故）。这里只调用 <c>PowerGiver.Give</c>（纯同步内核）。
///
/// ⚠️ A20（Boss 战加诅咒）**不在这个补丁里** —— 它必须早于存档，
///    所以挂在 <see cref="BossCurseOnRoomEnterPatch"/> 的 <c>Hook.BeforeRoomEntered</c> 上。
/// </summary>
[HarmonyPatch(typeof(Hook), nameof(Hook.BeforeCombatStart))]
internal static class PlayerCombatStartPatch
{
    private const int FrailAmount = 1;
    private const int WeakAmount = 1;

    private static int _diagBudget = 10;

    [HarmonyPrefix]
    internal static void Prefix(ICombatState combatState)
    {
        try
        {
            if (combatState is null)
            {
                Diag("Triggered but combatState is null.");
                return;
            }

            var creatures = combatState.PlayerCreatures;
            Diag($"Triggered: playerCreatures={creatures?.Count ?? -1} "
               + $"level={AscensionLevelRegistry.RawLevel}");

            if (creatures is null || creatures.Count == 0)
            {
                return;
            }

            foreach (var creature in creatures)
            {
                if (creature is null)
                {
                    continue;
                }

                ApplyFrail(creature);
                ApplyWeak(creature);
            }
        }
        catch (Exception ex)
        {
            ModLog.Error("玩家战斗开始补丁异常（已忽略）。", ex);
        }
    }

    // ────────────────────────────────────────────────────────────────
    //  A18 · 柔弱身躯
    // ────────────────────────────────────────────────────────────────
    private static void ApplyFrail(Creature creature)
    {
        if (!AscensionLevelRegistry.IsActiveRaw(AscensionLevelRegistry.FrailBody))
        {
            return;
        }

        var now = PowerGiver.Give<MegaCrit.Sts2.Core.Models.Powers.FrailPower>(creature, FrailAmount, "A18 柔弱身躯");
        if (now >= 0)
        {
            ModLog.Info($"A18 柔弱身躯：{creature.Player?.NetId} 获得 {FrailAmount} 层脆弱（现在 {now}）。");
        }
    }

    // ────────────────────────────────────────────────────────────────
    //  A19 · 无力攻击
    // ────────────────────────────────────────────────────────────────
    private static void ApplyWeak(Creature creature)
    {
        if (!AscensionLevelRegistry.IsActiveRaw(AscensionLevelRegistry.PowerlessStrike))
        {
            return;
        }

        var now = PowerGiver.Give<MegaCrit.Sts2.Core.Models.Powers.WeakPower>(creature, WeakAmount, "A19 无力攻击");
        if (now >= 0)
        {
            ModLog.Info($"A19 无力攻击：{creature.Player?.NetId} 获得 {WeakAmount} 层虚弱（现在 {now}）。");
        }
    }

    private static void Diag(string message)
    {
        if (_diagBudget <= 0)
        {
            return;
        }

        _diagBudget--;
        ModLog.Info($"A18/A19 诊断：{message}" + (_diagBudget == 0 ? "（后续同类日志已省略）" : ""));
    }
}

/// <summary>
/// A20 · 最终考验 —— **进入房间时**给 Boss 房的玩家加随机诅咒。
///
/// ★ 为什么是 <c>Hook.BeforeRoomEntered</c>
/// ======================================
/// 时机问题：游戏**进入房间时**存档。实测链条：
/// <code>
/// RunManager.EnterMapPointInternal
///   → RunManager.EnterRoom
///     → RunManager.EnterRoomInternal
///         RunState.PushRoom(room)                    ← 房间已就位
///         Hook::BeforeRoomEntered(runState, room)    ← ★ 挂这里
///   → SaveManager.SaveRun(...)                       ← 存档
///
/// （之后才）CombatManager.StartCombatInternal
///     → Hook::BeforeCombatStart                      ← 第一版挂这里，太晚了
/// </code>
///
/// 第一版挂在 <c>BeforeCombatStart</c>，那已在存档**之后**：
/// 诅咒进不了那一次存档 → SL 之后牌组里没有它 → A20 效果丢失。
/// 改挂 <c>BeforeRoomEntered</c> 后，加诅咒**早于存档**，两者天然一致。
///
/// 它是同步的 <c>void</c> 返回，所以 Prefix 里做同步操作完全安全。
/// </summary>
[HarmonyPatch(typeof(Hook), nameof(Hook.BeforeRoomEntered))]
internal static class BossCurseOnRoomEnterPatch
{
    [HarmonyPrefix]
    internal static void Prefix()
    {
        try
        {
            if (!AscensionLevelRegistry.IsActiveRaw(AscensionLevelRegistry.FinalTest))
            {
                return;
            }

            AscensionCombatStart.OnRoomEntered();
        }
        catch (Exception ex)
        {
            ModLog.Error("A20 进房间补丁异常（已忽略）。", ex);
        }
    }
}
