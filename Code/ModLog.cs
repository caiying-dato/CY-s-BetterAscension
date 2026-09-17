using System.Diagnostics;
using System.Reflection;
using MegaCrit.Sts2.Core.Logging;

namespace BetterAscension;

/// <summary>
/// 统一日志出口。所有诊断都带 <c>[BetterAscension]</c> 前缀，便于在 godot.log 里过滤。
///
/// 设计原则（来自 docs\A11复盘\04-调试与验证工作流.md）：
///   每个补丁都必须"失败可诊断"——匹配不到要明确 Warn，不能静默不生效。
///
/// 实测（tools\ApiDump type Logger）：游戏的 <c>Logger.Error(string text, int skipFrames = 1)</c>
/// **没有接受 Exception 的重载**，所以异常要自己格式化进消息里。
/// 好处是能把堆栈一起写进日志 —— A11 工程正是靠堆栈里的行号一眼定位到空引用的。
/// </summary>
internal static class ModLog
{
    public const string ModId = "BetterAscension";

    /// <summary>模组版本，取自程序集版本（由 csproj 的 Version 属性决定）。</summary>
    public static string Version { get; } =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    private static readonly Logger Log = new(ModId, LogType.Generic);

    public static void Info(string message) => Log.Info($"[{ModId}] {message}");

    public static void Warn(string message) => Log.Warn($"[{ModId}] {message}");

    public static void Error(string message) => Log.Error($"[{ModId}] {message}");

    /// <summary>把异常的类型、消息与堆栈一并写进日志。</summary>
    public static void Error(string message, Exception exception)
    {
        try
        {
            Log.Error($"[{ModId}] {message}{Environment.NewLine}"
                    + $"    {exception.GetType().FullName}: {exception.Message}{Environment.NewLine}"
                    + $"    {new StackTrace(exception, fNeedFileInfo: true)}");
        }
        catch
        {
            // 日志本身绝不能再抛异常
            Log.Error($"[{ModId}] {message}（附带异常信息格式化失败：{exception.GetType().Name}）");
        }
    }
}
