using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;

namespace BetterAscension;

/// <summary>
/// 给生物挂能力的**同步**助手。
///
/// 为什么必须有这个助手
/// --------------------
/// 补丁跑在**主线程的游戏循环内**。给生物加能力的常规入口 <c>PowerCmd.Apply</c> 返回
/// <c>Task</c>，它内部会 await 多个钩子；在主线程里用 <c>.GetResult()</c> / <c>.Wait()</c>
/// 同步等它，会把主循环自己卡住 —— 命令永远完不成，**整个游戏冻结**。
/// （A11 工程 v0.1.3 真实踩过：进精英战直接死机。）
///
/// 正解是走**同步内部路径**，与 <c>PowerCmd.Apply</c> 内部的顺序等价：
/// <code>
/// var p = ModelDb.Power&lt;T&gt;().MutableClone() as T;   // 克隆：数量 0、无 Owner
/// p.ApplyInternal(creature, amount, silent);        // 设 Owner → 应用数量 → 挂到生物身上
/// </code>
///
/// ⚠️ 绝对不要用 <c>PowerModel.ToMutable(int)</c>
/// ------------------------------------------
/// 实测（tools\ApiDump il PowerModel ToMutable）它的 IL 是：
/// <code>
/// IL_0007: call      AbstractModel::MutableClone()
/// IL_0014: callvirt  PowerModel::set_CanonicalInstance(...)
/// IL_001B: callvirt  PowerModel::set_Amount(Int32)      ← ★ 此刻还没有 Owner → 空引用
/// </code>
/// 也就是说它只是 <c>MutableClone()</c> 外面套了一层"设数量"，
/// 而克隆体此刻还没有 Owner，<c>set_Amount</c> 一解引用就炸。
/// 并且实测 <c>PowerModel</c> 上**根本没有无参的 <c>ToMutable()</c>** ——
/// 无参克隆的真实入口是基类的 <c>AbstractModel.MutableClone()</c>。
///
/// 副作用：跳过 <c>BeforeApplied</c> / <c>AfterApplied</c> 这对钩子。
/// 对"进阶在开局直接赋予一个先天能力"这个语义，这正是想要的 ——
/// 它算先天能力，不算一次出牌。
/// </summary>
internal static class PowerGiver
{
    /// <summary>
    /// 给 <paramref name="creature"/> 挂上 <typeparamref name="T"/> 能力。
    /// 全程同步，不 await。全过程包在 try/catch 里 —— 补丁绝不能把异常抛回游戏循环。
    /// </summary>
    /// <param name="creature">目标生物（敌人或玩家）。</param>
    /// <param name="amount">层数。</param>
    /// <param name="context">日志用的上下文短语，例如 "A11 精英强化"。</param>
    /// <returns>挂载后该能力的实际层数；失败返回 -1。</returns>
    public static int Give<T>(Creature creature, int amount, string context) where T : PowerModel
    {
        try
        {
            if (creature is null)
            {
                ModLog.Warn($"{context}：目标生物为 null，跳过。");
                return -1;
            }

            if (!creature.CanReceivePowers)
            {
                // 不是错误：某些生物（或已死亡/离场的生物）本来就不接收能力
                ModLog.Warn($"{context}：目标不接受能力，跳过。");
                return -1;
            }

            if (ModelDb.Power<T>() is not { } canonical)
            {
                ModLog.Error($"{context}：ModelDb 里找不到能力 {typeof(T).Name}。");
                return -1;
            }

            if (canonical.MutableClone() is not T mutable)
            {
                ModLog.Error($"{context}：{typeof(T).Name} 克隆失败。");
                return -1;
            }

            mutable.ApplyInternal(creature, amount, silent: true);
            return creature.GetPowerAmount<T>();
        }
        catch (Exception ex)
        {
            ModLog.Error($"{context}：挂载 {typeof(T).Name} 时异常。", ex);
            return -1;
        }
    }
}
