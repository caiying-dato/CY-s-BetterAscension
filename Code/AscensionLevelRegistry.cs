using BaseLib.Patches.Content;
using MegaCrit.Sts2.Core.Entities.Ascension;
using MegaCrit.Sts2.Core.Runs;

namespace BetterAscension;

/// <summary>
/// 进阶等级注册中心 —— 本模组的**单一常量中心**。
///
/// 为什么要集中在一处
/// ------------------
/// 游戏把进阶上限 <c>10</c> 硬编码在 5 个方法的 13 处字面量里（只能改 IL），
/// 又散落在面板上限、存档夹取、解锁上限等多处。
/// 一旦这些地方各写各的数字，A20 → A30 就会变成一场灾难。
/// 所以：**所有补丁、Transpiler、文案 key 都从本类取值，绝不再出现裸数字。**
///
/// BaseLib 的 [CustomEnum] 有个关键陷阱
/// ------------------------------------
/// 它**不按"下一个整数"分配值**，而是生成一个**哈希值**（本机实录过 -648373254）。
/// 所以：**绝对不要把 (int)EmpoweredElites 当等级号用。**
/// 等级号永远只用本类的 <c>Lv*</c> 常量；枚举成员只当**身份标记**。
/// 详见 docs\A11复盘\03-必须避开的坑.md 坑 2。
/// </summary>
internal static class AscensionLevelRegistry
{
    // ────────────────────────────────────────────────────────────────
    //  1. 等级常量 —— 全局唯一真源
    // ────────────────────────────────────────────────────────────────

    /// <summary>原版最高进阶。</summary>
    public const int VanillaMax = 10;

    /// <summary>本模组实现的最高进阶。A11~A20 阶段为 20；将来扩到 A30 时只改这一行。</summary>
    public const int MaxLevel = 20;

    /// <summary>本模组实现的最低进阶（当前阶段不变；将来做"进阶 -1~-10"时才动）。</summary>
    public const int MinLevel = 11;

    public const int EliteStrength = 11;   // A11 精英强敌
    public const int Attrition = 12;   // A12 水土不服
    public const int Greed = 13;   // A13 ？富有？
    public const int ClenchedTeeth = 14;   // A14 咬紧牙关
    public const int AscendersBanePlus = 15;   // A15 进阶之灾+
    public const int InflationPlus = 16;   // A16 通货膨胀+
    public const int Rarity = 17;   // A17 珍稀
    public const int FrailBody = 18;   // A18 柔弱身躯
    public const int PowerlessStrike = 19;   // A19 无力攻击
    public const int FinalTest = 20;   // A20 最终考验

    // ────────────────────────────────────────────────────────────────
    //  2. 枚举成员 —— 身份标记，数值无意义
    // ────────────────────────────────────────────────────────────────

    // ★ 重要：字段名一旦确定就**不要改**。
    //   BaseLib 用 (命名空间, 字段名) 计算哈希 → 改名等于换了一个新的枚举值。
    //   BaseLib 会把 [CustomEnum] 字段按**字段名排序**后依次生成，
    //   所以新增字段不会影响已有字段的值（每个字段的值只取决于自己的名字）。
    //
    // CS0649（"从未赋值"）是**预期内**的：这些字段由 BaseLib 在运行时用反射写入，
    // 编译器看不到赋值点。这里局部抑制，避免 10 条噪音掩盖真正的问题。
#pragma warning disable CS0649
    [CustomEnum] public static AscensionLevel EliteStrengthLevel;
    [CustomEnum] public static AscensionLevel AttritionLevel;
    [CustomEnum] public static AscensionLevel GreedLevel;
    [CustomEnum] public static AscensionLevel ClenchedTeethLevel;
    [CustomEnum] public static AscensionLevel AscendersBanePlusLevel;
    [CustomEnum] public static AscensionLevel InflationPlusLevel;
    [CustomEnum] public static AscensionLevel RarityLevel;
    [CustomEnum] public static AscensionLevel FrailBodyLevel;
    [CustomEnum] public static AscensionLevel PowerlessStrikeLevel;
    [CustomEnum] public static AscensionLevel FinalTestLevel;
#pragma warning restore CS0649

    // ────────────────────────────────────────────────────────────────
    //  3. 等级 → 枚举身份
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// 把"等级号"翻回"枚举身份"。
    ///
    /// 0~10 用游戏原生成员（它们是 0..10 的**顺序枚举**，值是确定的）；
    /// 11~20 用 BaseLib 生成的成员（值是哈希，只能按身份比较）。
    ///
    /// 这里刻意用 <c>switch</c> 常量模式：游戏私有枚举成员的可见性由
    /// csproj 里的 Krafs.Publicizer 打开，且已实测可在常量模式中使用。
    /// </summary>
    public static AscensionLevel ToEnum(int level) => level switch
    {
        <= 0 => AscensionLevel.None,
        1 => AscensionLevel.SwarmingElites,
        2 => AscensionLevel.WearyTraveler,
        3 => AscensionLevel.Poverty,
        4 => AscensionLevel.TightBelt,
        5 => AscensionLevel.AscendersBane,
        6 => AscensionLevel.Inflation,
        7 => AscensionLevel.Scarcity,
        8 => AscensionLevel.ToughEnemies,
        9 => AscensionLevel.DeadlyEnemies,
        10 => AscensionLevel.DoubleBoss,
        EliteStrength => EliteStrengthLevel,
        Attrition => AttritionLevel,
        Greed => GreedLevel,
        ClenchedTeeth => ClenchedTeethLevel,
        AscendersBanePlus => AscendersBanePlusLevel,
        InflationPlus => InflationPlusLevel,
        Rarity => RarityLevel,
        FrailBody => FrailBodyLevel,
        PowerlessStrike => PowerlessStrikeLevel,
        FinalTest => FinalTestLevel,
        _ => AscensionLevel.DoubleBoss,   // 超出范围一律按原版最高处理
    };

    /// <summary>本地化 key（如 11 → <c>"LEVEL_11"</c>）。</summary>
    /// <remarks>
    /// <c>AscensionHelper.GetKey</c> 的真实实现是 <c>level.ToString("D2")</c>，
    /// 所以 11 对应 <c>"LEVEL_11"</c>、20 对应 <c>"LEVEL_20"</c>，与游戏原始表完全一致，不需要改 key。
    ///
    /// ⚠️ 将来做"进阶 -1~-10"时这里会出问题：<c>(-1).ToString("D2")</c> 得到 <c>"-1"</c> 而不是 <c>"-01"</c>，
    /// 会拼出 <c>"LEVEL_-1.title"</c> 查不到。届时需要单独设计 key 方案。
    /// </remarks>
    public static string GetTitleKey(int level) => $"LEVEL_{level:D2}.title";

    /// <summary>本地化 key（描述）。</summary>
    public static string GetDescriptionKey(int level) => $"LEVEL_{level:D2}.description";

    // ────────────────────────────────────────────────────────────────
    //  4. 就绪状态 —— ModelDb.Init 之后才可用
    // ────────────────────────────────────────────────────────────────

    private static bool _resolved;

    /// <summary>
    /// 模组 <c>Initialize()</c> 时拍下的快照（此刻 BaseLib 还没生成值）。
    /// 用来判断"是否已生成"—— 比 <c>== None</c> 可靠得多。
    /// </summary>
    private static AscensionLevel[]? _beforeSnapshot;

    /// <summary>BaseLib 是否已为我们的 [CustomEnum] 字段生成好值。</summary>
    public static bool IsInitialized => _resolved;

    /// <summary>
    /// 在 <c>ModelDb.Init</c> **之前**记录当前字段值。
    ///
    /// 为什么要快照
    /// ------------
    /// 「是否已生成」不能用 <c>!= 0</c> 判断：BaseLib 生成的是哈希值，
    /// **理论上可能碰巧等于 0**（也就是 <c>AscensionLevel.None</c>），见
    /// docs\A11复盘\03-必须避开的坑.md 坑 2。
    /// 也不能只看单个字段：那样等于把正确性押在一次哈希碰撞上。
    /// 快照对比则对任意个字段都成立。
    /// </summary>
    public static void CaptureBeforeSnapshot()
    {
        try
        {
            _beforeSnapshot = new[]
            {
                EliteStrengthLevel, AttritionLevel, GreedLevel, ClenchedTeethLevel,
                AscendersBanePlusLevel, InflationPlusLevel, RarityLevel,
                FrailBodyLevel, PowerlessStrikeLevel, FinalTestLevel,
            };
        }
        catch (Exception ex)
        {
            ModLog.Error("拍摄枚举前置快照失败（已忽略）。", ex);
        }
    }

    /// <summary>
    /// 读取并确认生成结果。必须在 <c>ModelDb.Init</c> **之后**调用
    /// （BaseLib 的 <c>GenEnumValues</c> 是 <c>ModelDb.Init</c> 的 Prefix，所以 Init 后一定已生成）。
    ///
    /// ★ 这个方法**绝不能抛异常**：它跑在 <c>NGame.GameStartup()</c> 的调用链里，
    ///   异常逃逸会中止启动并弹错误框（A11 工程 v0.1.1 真实踩过）。
    /// </summary>
    public static bool TryResolve()
    {
        if (_resolved) return true;

        try
        {
            var after = new[]
            {
                EliteStrengthLevel, AttritionLevel, GreedLevel, ClenchedTeethLevel,
                AscendersBanePlusLevel, InflationPlusLevel, RarityLevel,
                FrailBodyLevel, PowerlessStrikeLevel, FinalTestLevel,
            };

            if (_beforeSnapshot is not null)
            {
                var changed = after.Where((v, i) => v != _beforeSnapshot[i]).Count();
                if (changed == 0)
                {
                    ModLog.Warn("BaseLib 尚未为 [CustomEnum] 字段生成值（ModelDb.Init 之后仍与初始化前一致）。"
                              + "请确认 BaseLib 已安装，且在『设置 → 模组设置』里处于启用状态。");
                    return false;
                }
            }

            _resolved = true;

            for (var level = MinLevel; level <= MaxLevel; level++)
            {
                var id = ToEnum(level);
                ModLog.Info($"已注册进阶 {level} → AscensionLevel.{id}（生成值 {(int)id}）。");
            }

            return true;
        }
        catch (Exception ex)
        {
            // 吞掉，绝不让它冒到 GameStartup
            ModLog.Error("解析自定义进阶枚举时异常（已忽略，模组功能将不生效）。", ex);
            return false;
        }
    }

    /// <summary>诊断输出：把当前真实状态全部打出来，排查"显示对了但没生效"时先看它。</summary>
    public static void LogDiagnostics()
    {
        try
        {
            ModLog.Info($"注册中心：MinLevel={MinLevel} MaxLevel={MaxLevel} "
                      + $"VanillaMax={VanillaMax} 已解析={_resolved}");
            ModLog.Info($"AscensionLevel 现有成员：{string.Join(", ", Enum.GetNames(typeof(AscensionLevel)))}");
        }
        catch (Exception ex)
        {
            ModLog.Error("输出诊断信息时异常。", ex);
        }
    }

    // ────────────────────────────────────────────────────────────────
    //  5. 运行时查询 —— 各补丁统一走这里
    // ────────────────────────────────────────────────────────────────

    private static int ActiveLevel
    {
        get
        {
            try
            {
                return RunManager.Instance?.State?.AscensionLevel ?? 0;
            }
            catch
            {
                return 0;
            }
        }
    }

    /// <summary>
    /// 当前运行局的**原始**进阶等级整数。局外（主菜单、角色选择）返回 0。
    ///
    /// ★ 各补丁请优先用这个，而不是 <see cref="IsAnyActive"/>：
    ///   它不依赖枚举是否已生成，即使 BaseLib 那边出问题，上限相关的补丁仍能工作。
    /// </summary>
    public static int RawLevel => ActiveLevel;

    /// <summary>本模组是否已就绪，且当前运行局的进阶等级在做用范围内。</summary>
    public static bool IsAnyActive => _resolved && ActiveLevel >= MinLevel;

    /// <summary>指定进阶等级是否在当前运行局生效（"高阶包含所有低阶"）。</summary>
    public static bool IsActive(int level) =>
        _resolved && level >= MinLevel && level <= MaxLevel && ActiveLevel >= level;

    /// <summary>
    /// 不依赖枚举就绪状态的版本。**补丁里应优先用这个** —— 见 <see cref="RawLevel"/> 的说明。
    /// </summary>
    public static bool IsActiveRaw(int level) =>
        level >= MinLevel && level <= MaxLevel && ActiveLevel >= level;

    /// <summary>当前运行局是否恰好是这一阶（需要精确区分时用）。</summary>
    public static bool IsExactly(int level) =>
        _resolved && level >= MinLevel && level <= MaxLevel && ActiveLevel == level;
}
