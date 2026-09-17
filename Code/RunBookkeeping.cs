using System.Runtime.CompilerServices;
using MegaCrit.Sts2.Core.Runs;

namespace BetterAscension;

/// <summary>
/// 按**运行局实例**挂载的簿记状态，用来做幂等保护。
///
/// ★ 为什么必须是 per-run，而不是 <c>static</c> 集合
/// ==============================================
/// 最初 A12 与 A20 都用 <c>static readonly HashSet&lt;int&gt;</c> 记录"已经处理过的
/// 幕号 / 房间号"。它的生命周期是**整个游戏进程** —— 跨存档、跨新局都不重置。
/// 结果就是 CY 实测到的两个现象：
///
/// · **A20 每次打开游戏只能触发一次**：首次进 Boss 时 <c>RunState.NextRoomId</c> 读到 0，
///   <c>CursedRooms</c> 记下 0；而新开一局的 <c>NextRoomId</c> 又是从 0 开始
///   → 永远被判定为"已经给过"，此后所有局都不再触发。
/// · **A12 第二局连第 2 幕都不扣**：幕号 1、2 每局都一样，
///   <c>AppliedActs</c> 在上一局就记下了，新局直接被拦。
///
/// 修法不是"记得在某处清空集合"（那要求找全所有需要重置的时机，很容易漏），
/// 而是**把状态挂到 <c>RunState</c> 实例上**：
/// <list type="bullet">
///   <item><c>ConditionalWeakTable</c> 的键是弱引用 → 一局的 <c>RunState</c> 被回收时，
///         对应簿记**自动消失**，不泄漏、也不会残留到下一局。</item>
///   <item>读档会创建新的 <c>RunState</c> → 自然拿到一份干净的簿记。</item>
///   <item>新开局同理。**无需任何显式重置逻辑。**</item>
/// </list>
///
/// 键用 <c>RunState</c> 而不是"局 id"，是因为我们只需要"同一局内共享"，
/// 而实例本身就是最准确的局身份。
/// </summary>
internal static class RunBookkeeping
{
    internal sealed class Book
    {
        /// <summary>A12：已经扣过最大生命的幕号。</summary>
        public readonly HashSet<int> AttritionAppliedActs = [];

        /// <summary>A20：已经给过诅咒的房间（用 幕号 与 当前房间序号 定位，见调用处说明）。</summary>
        public readonly HashSet<(int Act, int Room)> CursedRooms = [];
    }

    private static readonly ConditionalWeakTable<RunState, Book> Table = new();

    /// <summary>取当前局的簿记；<paramref name="state"/> 为 null 时返回 null。</summary>
    public static Book? For(RunState? state) =>
        state is null ? null : Table.GetValue(state, _ => new Book());
}
