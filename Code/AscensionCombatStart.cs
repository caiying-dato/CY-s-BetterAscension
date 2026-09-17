using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace BetterAscension;

/// <summary>
/// A20 · 最终考验 —— 进入 Boss 战时随机将一张诅咒加入卡组（永久）。
///
/// ★ 为什么必须挂在"进入房间"而不是"战斗开始"
/// ============================================
/// 游戏在**进入房间时**存档。实测调用链是：
/// <code>
/// RunManager.EnterMapPointInternal
///   → RunManager.EnterRoom
///     → RunManager.EnterRoomInternal
///         RunState.PushRoom(room)                    ← 房间已就位
///         Hook.BeforeRoomEntered(runState, room)     ← ★ 我们挂在这里（同步）
///   → SaveManager.SaveRun(...)                       ← 存档发生在这里
///
/// （之后才）CombatManager.StartCombatInternal
///     → Hook.BeforeCombatStart                       ← 原本挂在这里，太晚了
/// </code>
///
/// 第一版挂在 <c>BeforeCombatStart</c>，那已经是**存档之后**了：
/// 诅咒进不了那一次存档 → SL 之后牌组里没有它 → A20 效果丢失。
///
/// 现在改挂 <c>BeforeRoomEntered</c>：
/// · 时机**早于存档** → 诅咒一定被写进存档，SL 后仍在。
/// · <c>PushRoom</c> 已经执行 → <c>RunState.CurrentRoom</c> 就是新房间，Boss 判定可靠。
/// · 它是同步的 <c>void</c> 返回 → 无 async 时机陷阱。
///
/// ★ 为什么不再需要"标记卡"
/// ------------------------
/// 中间试过"加一张自定义标记卡与诅咒同生共死"的办法，**失败了**：
/// 那张自定义卡渲染不出来，导致**查看卡组直接异常**。
/// 根本原因是自定义卡牌需要完整的美术/场景资源才能显示，代价过大。
///
/// 而改挂 <c>BeforeRoomEntered</c> 之后，标记卡就**不必要**了：
/// 因为这一次操作本身就在存档之前，诅咒与存档天然一致。
/// 幂等保护只需防"同一场 Boss 战被重复进入"（例如普通读档重进），
/// 用一个内存态房间号集合就够 —— 而且现在它不会造成不一致，
/// 因为**已经加过的诅咒一定已经在存档里**。
/// </summary>
internal static class AscensionCombatStart
{
    /// <summary>
    /// 随机诅咒池。已排除 <c>AscendersBane</c>（它实测 <c>CanBeGeneratedByModifiers = false</c>）。
    /// 这 17 个类型都已在真实 sts2.dll 上确认存在。
    /// </summary>
    private static readonly Type[] CursePool =
    [
        typeof(BadLuck), typeof(Clumsy), typeof(CurseOfTheBell), typeof(Debt),
        typeof(Decay), typeof(Doubt), typeof(Enthralled), typeof(Folly),
        typeof(Greed), typeof(Guilty), typeof(Injury), typeof(Normality),
        typeof(PoorSleep), typeof(Regret), typeof(Shame), typeof(SporeMind),
        typeof(Writhe),
    ];

    /// <summary>
    /// 幂等保护：记录**本局**已经给过诅咒的房间。
    ///
    /// ★ 这里曾经是 <c>static readonly HashSet&lt;int&gt;</c> 按 <c>RunState.NextRoomId</c> 记录，
    ///   那是**两个错误叠加**：
    ///
    /// 1. **静态集合跨局残留** —— 它的生命周期是整个游戏进程，新开局不重置。
    /// 2. **<c>NextRoomId</c> 不是一个可靠的房间标识** —— 首次进 Boss 时实测读到 **0**；
    ///    而新开一局的 <c>NextRoomId</c> 又是从 0 开始 → 新局必然命中同一个键
    ///    → 被判定为"已经给过"→ **A20 此后永不触发**。
    ///
    /// CY 实测的现象正是"每次打开游戏只能触发一次，之后不关游戏开新档也失效"。
    ///
    /// 现在两者都修好：
    /// · 状态改为按 <c>RunState</c> 实例挂载（<see cref="RunBookkeeping"/>），一局一份。
    /// · 键改为 <c>(幕号, 当前房间序号)</c> —— <c>CurrentRoomCount</c> 实测是
    ///   <c>_currentRooms.Count</c>，每次进房递增且一局内单调上涨，配合幕号足够唯一。
    /// </summary>
    private static int _diagBudget = 8;

    /// <summary>
    /// 进入房间时调用。若是 Boss 房则给所有玩家各加一张随机诅咒。
    /// </summary>
    public static void OnRoomEntered()
    {
        try
        {
            if (!IsBossRoom())
            {
                return;
            }

            var state = RunManager.Instance?.State;
            if (state is null)
            {
                return;
            }

            var book = RunBookkeeping.For(state);
            if (book is null)
            {
                return;
            }

            // 幂等键：幕号 + 当前房间序号。两者都在一局内确定且唯一。
            var key = (state.CurrentActIndex, state.CurrentRoomCount);
            if (!book.CursedRooms.Add(key))
            {
                return;   // 这个房间已经给过了
            }

            Diag($"进入 Boss 房（第 {key.Item1 + 1} 幕 / 第 {key.Item2} 个房间），"
               + "开始给玩家加诅咒。");

            foreach (var player in state.Players)
            {
                try
                {
                    AddRandomCurse(state, player);
                }
                catch (Exception ex)
                {
                    ModLog.Error($"A20：给玩家 {player?.NetId} 加诅咒时异常（继续处理其它玩家）。", ex);
                }
            }
        }
        catch (Exception ex)
        {
            ModLog.Error("A20 Boss 诅咒异常（已忽略）。", ex);
        }
    }

    private static void AddRandomCurse(RunState state, MegaCrit.Sts2.Core.Entities.Players.Player? player)
    {
        if (player?.Deck is null)
        {
            return;
        }

        var pick = CursePool[Random.Shared.Next(CursePool.Length)];
        var card = CreateCard(state, pick, player);
        if (card is null)
        {
            ModLog.Error($"A20：无法创建诅咒卡 {pick.Name}。");
            return;
        }

        var before = player.Deck.Cards.Count;

        // 本体的写法（AscensionManager 加 AscendersBane 时）：
        //   player.RunState.CreateCard<T>(player) → player.Deck.AddInternal(card, -1, silent)
        player.Deck.AddInternal(card, -1, silent: true);

        var after = player.Deck.Cards.Count;

        // ★ 回读牌组数量 —— 这行本身就是断言
        ModLog.Info($"A20 最终考验：进入 Boss 战，随机诅咒 [{pick.Name}] 已加入卡组"
                  + $"（牌组 {before} → {after} 张）。");
    }

    /// <summary>
    /// <c>RunState.CreateCard&lt;T&gt;(Player)</c> 是泛型方法，而池子是 <c>Type[]</c>，
    /// 所以用反射 <c>MakeGenericMethod</c> 调用。只在进 Boss 房时走一次，开销可忽略。
    /// </summary>
    private static CardModel? CreateCard(RunState state, Type cardType,
                                         MegaCrit.Sts2.Core.Entities.Players.Player player)
    {
        var method = AccessTools.Method(typeof(RunState), nameof(RunState.CreateCard),
                                        [typeof(MegaCrit.Sts2.Core.Entities.Players.Player)]);
        if (method is null)
        {
            ModLog.Error("A20：找不到 RunState.CreateCard(Player) —— 游戏 API 可能已改名。");
            return null;
        }

        return method.MakeGenericMethod(cardType).Invoke(state, [player]) as CardModel;
    }

    /// <summary>Boss 战判定：地图点类型为主、房间类型兜底（与 A11 判精英同构）。</summary>
    private static bool IsBossRoom()
    {
        var state = RunManager.Instance?.State;
        if (state is null)
        {
            return false;
        }

        try
        {
            if (state.CurrentMapPoint?.PointType == MapPointType.Boss)
            {
                return true;
            }
        }
        catch
        {
            // 某些房间 CurrentMapPoint 不可用，落到兜底
        }

        return state.CurrentRoom?.RoomType == RoomType.Boss;
    }

    private static void Diag(string message)
    {
        if (_diagBudget <= 0)
        {
            return;
        }

        _diagBudget--;
        ModLog.Info($"A20 诊断：{message}" + (_diagBudget == 0 ? "（后续同类日志已省略）" : ""));
    }
}
