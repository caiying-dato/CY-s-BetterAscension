using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;

namespace BetterAscension;

/// <summary>
/// 模组入口。
///
/// 两条来自实战的硬性约定
/// ----------------------
/// 1. **逐个补丁类打补丁，不用 <c>PatchAll()</c>。**
///    <c>PatchAll()</c> 遇到第一个失败的补丁类就整体中止，后面全部静默不生效，
///    而且会扫到其它程序集的补丁类。逐个打 + 分别 catch，一个不兼容不会拖垮整个模组，
///    日志还直接告诉你是哪个类。
///
/// 2. **初始化里的任何代码都不能抛异常。**
///    入口跑在 <c>NGame.GameStartup()</c> 调用链里，异常逃逸会中止启动并弹错误框。
/// </summary>
[ModInitializer(nameof(Initialize))]
public static class BetterAscensionMod
{
    /// <summary>自定义枚举是否已生成完毕。ModelDb.Init 之前一律不做事。</summary>
    internal static bool EnumReady { get; private set; }

    public static void Initialize()
    {
        try
        {
            ModLog.Info($"CY's BetterAscension v{ModLog.Version} 正在初始化……");

            // ★ 必须在 ModelDb.Init 之前拍快照：BaseLib 是在 ModelDb.Init 的 Prefix 里
            //   给 [CustomEnum] 字段赋值的，我们靠"前后是否变化"来判断生成是否成功。
            AscensionLevelRegistry.CaptureBeforeSnapshot();

            // ★ 顺序很重要：先打 ModelDbInitPatch，它负责在正确的时机解析自定义枚举。
            PatchAllIndividually();

            ModLog.Info($"补丁安装结束，共 {_patchedClasses} 个补丁类、{_patchedMethods} 个方法。");
        }
        catch (Exception ex)
        {
            // 只记录，不抛出 —— 绝不能让模组把游戏启动搞崩
            ModLog.Error("初始化失败（已忽略，模组功能将不生效）。", ex);
        }
    }

    private static int _patchedClasses;
    private static int _patchedMethods;

    private static void PatchAllIndividually()
    {
        var harmony = new Harmony(ModLog.ModId);

        var patchClasses = typeof(BetterAscensionMod).Assembly
            .GetTypes()
            .Where(t => t.GetCustomAttributes(typeof(HarmonyPatch), false).Length > 0)
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .ToList();

        // ★ 先把"发现了哪些补丁类"整份打出来。
        //   上一次事故就是入口只报"共装了 3 个"，没说"应该有几个"，
        //   于是一半补丁静默缺失也没人察觉。现在两者都打，差集一眼可见。
        ModLog.Info($"发现 {patchClasses.Count} 个补丁类：" +
                    string.Join("、", patchClasses.Select(t => t.Name)));

        foreach (var type in patchClasses)
        {
            try
            {
                var patched = harmony.CreateClassProcessor(type).Patch();
                _patchedClasses++;
                _patchedMethods += patched.Count;

                if (patched.Count == 0)
                {
                    // Harmony 对"类里没有可识别的补丁方法"会静默返回空列表
                    ModLog.Warn($"补丁类 {type.FullName} 未安装任何方法（返回 0）—— "
                              + "检查它的方法是否带了 [HarmonyPrefix]/[HarmonyPostfix]/[HarmonyTranspiler]。");
                    continue;
                }

                ModLog.Info($"已安装补丁 {type.FullName}（{patched.Count} 个方法）。");
            }
            catch (Exception ex)
            {
                ModLog.Error($"补丁类 {type.FullName} 安装失败（跳过它，继续装其它补丁）。", ex);
            }
        }
    }

    /// <summary>
    /// 由 <see cref="Patches.AscensionCapacityPatches"/> 在 <c>ModelDb.Init</c> 之后调用。
    /// BaseLib 的 <c>GenEnumValues</c> 是 <c>ModelDb.Init</c> 的 Prefix，
    /// 所以到这一步 <c>[CustomEnum]</c> 字段一定已经生成完毕。
    /// </summary>
    internal static void OnModelDbInitialized()
    {
        try
        {
            EnumReady = AscensionLevelRegistry.TryResolve();
            if (EnumReady)
            {
                ModLog.Info("自定义进阶枚举就绪，本模组开始生效。");
            }
            AscensionLevelRegistry.LogDiagnostics();
        }
        catch (Exception ex)
        {
            // ModelDb.Init 里的异常会中止游戏启动 —— 全部吞掉
            ModLog.Error("ModelDb 初始化后的处理异常（已忽略）。", ex);
        }
    }
}
