using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Managers;

namespace BetterAscension.Patches;

/// <summary>
/// 地基补丁：让游戏接受"进阶等级 &gt; 10"。
///
/// 游戏把上限 <c>10</c> 硬编码为**字面量**（<c>ldc.i4.s 10</c>）散落在 5 个方法的 13 处，只能改 IL。
///
/// ⚠️ <c>AscensionManager.maxAscensionAllowed</c> 看起来像上限，但它是 <c>const</c>
/// （实测 <c>IsLiteral=True</c>）**且全程序集无引用（死代码）**。用反射写它会抛
/// <c>FieldAccessException</c> 并从 <c>ModelDb.Init</c> 冒到 <c>NGame.GameStartup()</c>，
/// **中止游戏启动并弹错误框**（A11 工程 v0.1.1 真实踩过）。绝对不要碰它。
///
/// 另外：<c>AscensionManager._level</c> 实测是 <c>readonly</c>，构造后无法反射改写。
///
/// 所有处数都从 <see cref="AscensionLevelRegistry.MaxLevel"/> 取，绝不写裸数字。
/// </summary>
internal static class AscensionCapacity
{
    /// <summary>原版写死的上限。只作为"要被替换掉的目标值"出现。</summary>
    public const int VanillaCeiling = 10;

    public static int Ceiling => AscensionLevelRegistry.MaxLevel;

    /// <summary>
    /// 把方法体里所有 <c>10</c> 字面量换成上限值。
    ///
    /// 这几个方法里除了"上限"没有别的含义为 10 的常量（其余是 0 / 1 / -1 与字符串长度），
    /// 所以整体重写是安全的。
    ///
    /// ★ 刻意**不做**"按模式匹配定位"（例如"getter 后跟 10"）：
    ///   A11 工程第一版就是那么写的，结果 9 处只改对 3 处、另 2 处压根没匹配上，
    ///   而那 2 处会让进阶在**读档时被夹回 10**。整体重写 + 事后断言才可靠。
    /// </summary>
    public static IEnumerable<CodeInstruction> RaiseCeiling(
        IEnumerable<CodeInstruction> instructions, MethodBase original, int expected)
    {
        var output = new List<CodeInstruction>();
        var site = $"{original.DeclaringType?.Name}.{original.Name}";
        var replaced = 0;

        try
        {
            foreach (var instruction in instructions)
            {
                if (TryGetCeilingLiteral(instruction, out var replacement))
                {
                    var rewritten = new CodeInstruction(instruction.opcode, replacement);
                    // ★ 必须搬运标签与异常块，否则分支目标和 try/catch 会失效
                    rewritten.labels.AddRange(instruction.labels);
                    rewritten.blocks.AddRange(instruction.blocks);
                    output.Add(rewritten);
                    replaced++;
                    continue;
                }

                output.Add(instruction);
            }
        }
        catch (Exception ex)
        {
            ModLog.Error($"重写 '{site}' 的上限字面量时异常；该方法保持原样（上限仍是 {VanillaCeiling}）。", ex);
            return instructions;
        }

        // 「失败可诊断」：匹配不到必须明确报警，不能静默不生效
        if (replaced == 0)
        {
            ModLog.Error($"在 '{site}' 里没找到任何上限字面量 {VanillaCeiling} —— "
                       + "这一处仍卡在原版上限。游戏可能改写了该方法，请对照新版 IL 调整。");
            return output;
        }

        if (replaced != expected)
        {
            ModLog.Warn($"'{site}' 里替换了 {replaced} 处上限字面量，但预期 {expected} 处。"
                      + "游戏版本可能已变动，建议重跑 tools\\TranspilerCheck 核对。");
        }

        ModLog.Info($"已把 '{site}' 里 {replaced} 处上限字面量从 {VanillaCeiling} 提升到 {Ceiling}。");
        return output;
    }

    /// <summary>
    /// 判断这条指令是不是"上限 10"，是则给出替换用的操作数。
    /// 同时覆盖 <c>ldc.i4.s 10</c>（sbyte）与 <c>ldc.i4 10</c>（int）两种编码。
    /// </summary>
    public static bool TryGetCeilingLiteral(CodeInstruction instruction, out object replacement)
    {
        if (instruction.opcode == OpCodes.Ldc_I4_S && instruction.operand is sbyte s && s == VanillaCeiling)
        {
            replacement = (sbyte)Ceiling;
            return true;
        }

        if (instruction.opcode == OpCodes.Ldc_I4 && instruction.operand is int i && i == VanillaCeiling)
        {
            replacement = Ceiling;
            return true;
        }

        replacement = null!;
        return false;
    }
}

/// <summary>
/// 时机补丁：BaseLib 的 <c>GenEnumValues</c> 是 <c>ModelDb.Init</c> 的 **Prefix**，
/// 所以 <c>ModelDb.Init</c> 的 **Postfix** 是"自定义枚举一定已生成"的最早时机。
/// </summary>
[HarmonyPatch(typeof(ModelDb), nameof(ModelDb.Init))]
internal static class ModelDbInitPatch
{
    /// <remarks>
    /// ★ 这个方法内部必须吞掉所有异常 —— 它跑在 <c>NGame.GameStartup()</c> 链路上，
    ///   异常逃逸会中止启动。异常处理已在 <c>BetterAscensionMod.OnModelDbInitialized</c> 内完成。
    /// </remarks>
    [HarmonyPostfix]
    private static void Postfix()
    {
        BetterAscensionMod.OnModelDbInitialized();
    }
}

/// <summary>
/// 唯一收口点：<c>CharacterStats.MaxAscension</c> 的 getter。
///
/// 为什么是 getter 而不是逐个调用点
/// --------------------------------
/// 实测（<c>tools\ApiDump callers CharacterStats MaxAscension</c>）有 **13 处**读取者：
/// <c>ProgressState.ClampCharacterStatsFields</c>、<c>ProgressState.MergeCharacterStats</c>、
/// <c>ProgressSaveManager.IncrementSingleplayerAscension</c>、
/// <c>StartRunLobby.{BeginRunLocally, SetSingleplayerAscensionAfterCharacterChanged,
/// GetMaxAscensionAcrossAllCharacters, UpdatePreferredAscension}</c>、
/// <c>Player..ctor</c>、<c>UnlockConsoleCmd.UnlockAscensions</c>、
/// <c>NCharacterStats.LoadStats</c>、序列化上下文 2 处、<c>SaveManager.GetAggregateAscensionProgress</c>。
/// 逐点打补丁不可维护。补 getter 一处即可全覆盖。
///
/// 为什么"只改返回值"是对的
/// ------------------------
/// 大厅选定角色时执行 <c>Ascension = Math.Min(PreferredAscension, MaxAscension)</c>，
/// 而存档里的 <c>MaxAscension</c> 是 10 → 进阶被夹回 10（A11 工程 v0.1.2 的根因）。
/// 我们只抬高**返回值**、**不写回存档**，所以卸载模组后存档完全兼容。
/// </summary>
[HarmonyPatch(typeof(CharacterStats), nameof(CharacterStats.MaxAscension), MethodType.Getter)]
internal static class CharacterStatsMaxAscensionPatch
{
    [HarmonyPostfix]
    private static void Postfix(ref int __result)
    {
        if (__result < AscensionCapacity.Ceiling)
        {
            __result = AscensionCapacity.Ceiling;
        }
    }
}

/// <summary>
/// 读档时夹取运行局等级。实测 3 处（含 <c>return 10</c>）。
/// </summary>
/// <remarks>
/// 为什么一处一个补丁类、且都写成<b>顶层类 + 类级 [HarmonyPatch]</b>
/// --------------------------------------------------------------
/// 第一版把 5 个 Transpiler 放进一个外层类里当**嵌套类**，并在**方法上**挂
/// <c>[HarmonyPatch(typeof(X), "Y")]</c>。结果游戏里实测：那 4 个（含本类）
/// **一个都没装上**，日志里连一条"已把…字面量提升"都没有；
/// 而同一次运行里用<b>类级</b> <c>[HarmonyPatch]</c> 的那个补丁类正常安装。
///
/// 也就是说"嵌套类 + 方法级 HarmonyPatch"这个组合在本机 Harmony 上不生效。
/// 所以现在统一成：<b>顶层类、类级 [HarmonyPatch]、internal static 补丁方法</b>。
/// 这个写法在本工程里已被 <c>ModelDbInitPatch</c> / <c>CharacterStatsMaxAscensionPatch</c>
/// 实测验证过。
/// </remarks>
[HarmonyPatch(typeof(ProgressState), nameof(ProgressState.ClampAscension))]
internal static class ClampAscensionPatch
{
    [HarmonyTranspiler]
    internal static IEnumerable<CodeInstruction> Transpiler(
        IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
        => AscensionCapacity.RaiseCeiling(instructions, __originalMethod, expected: 3);
}

/// <summary>
/// 夹取 <c>MaxAscension</c> 与 <c>PreferredAscension</c> 及其报错文案。实测 **6 处**。
/// </summary>
/// <remarks>
/// ⚠️ 这是最容易漏的一个：A11 工程第一版只改到 4 处，
/// 漏掉的那 2 处会让进阶在**读档时被夹回 10**。
/// </remarks>
[HarmonyPatch(typeof(ProgressState), nameof(ProgressState.ClampCharacterStatsFields))]
internal static class ClampCharacterStatsFieldsPatch
{
    [HarmonyTranspiler]
    internal static IEnumerable<CodeInstruction> Transpiler(
        IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
        => AscensionCapacity.RaiseCeiling(instructions, __originalMethod, expected: 6);
}

/// <summary>胜利后解锁下一阶的上限。实测 1 处。</summary>
[HarmonyPatch(typeof(ProgressSaveManager), nameof(ProgressSaveManager.IncrementSingleplayerAscension))]
internal static class IncrementSingleplayerAscensionPatch
{
    [HarmonyTranspiler]
    internal static IEnumerable<CodeInstruction> Transpiler(
        IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
        => AscensionCapacity.RaiseCeiling(instructions, __originalMethod, expected: 1);
}

/// <summary>同上（多人）。实测 1 处。</summary>
[HarmonyPatch(typeof(ProgressSaveManager), nameof(ProgressSaveManager.IncrementMultiplayerAscension))]
internal static class IncrementMultiplayerAscensionPatch
{
    [HarmonyTranspiler]
    internal static IEnumerable<CodeInstruction> Transpiler(
        IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
        => AscensionCapacity.RaiseCeiling(instructions, __originalMethod, expected: 1);
}

/// <summary>
/// 控制台"解锁全部进阶"指令。实测 2 处。
/// 用 <c>AccessTools</c> 按名字查找，避免对开发用命令产生编译期硬依赖。
/// </summary>
[HarmonyPatch]
internal static class UnlockAscensionsPatch
{
    [HarmonyTargetMethod]
    internal static MethodBase? Target() =>
        AccessTools.TypeByName("MegaCrit.Sts2.Core.DevConsole.ConsoleCommands.UnlockConsoleCmd") is { } t
            ? AccessTools.Method(t, "UnlockAscensions")
            : null;

    [HarmonyTranspiler]
    internal static IEnumerable<CodeInstruction> Transpiler(
        IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
        => AscensionCapacity.RaiseCeiling(instructions, __originalMethod, expected: 2);
}
