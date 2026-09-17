// BetterAscension PatchCheck
//
// 目的：**在游戏外**检查每个补丁类的目标方法能否被**无歧义地**确定。
//
// 为什么需要它
// ------------
// 第一次实跑时 16 个补丁类里有 3 个安装失败，原因都不是逻辑，而是"目标不确定"：
//   · CombatManager.AfterCreatureAdded 有**两个重载** → Harmony 报 Ambiguous match
//   · PotionRewardOdds 有**两个构造函数**        → Harmony 解析出 null 目标
// 两种错误各自让**整个补丁类**安装失败，在游戏里只能读日志才发现，
// 每轮排查都要开游戏、进对局、退出 —— 太慢。
//
// 为什么**不能**在这里直接跑 Harmony 的 Patch()
// --------------------------------------------
// 补丁类里有 Transpiler，它们通过 ModLog 打日志；而 ModLog 的静态构造会初始化
// 游戏自己的 Logger → 它去问 Godot.OS 要命令行参数 → 在 Godot 进程外直接段错误
// （实测崩在 godotsharp_string_new_with_utf16_chars，0xC0000005）。
// 所以本工具**只做反射解析**，不执行任何补丁方法 ——
// 这恰好也覆盖了上面那两种真实故障。
//
// 退出码：0 = 全部无歧义；1 = 有补丁类的目标存在歧义或找不到。
using System.Reflection;
using HarmonyLib;

static string FindGame()
{
    foreach (var d in new[]
             {
                 @"C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2",
                 @"D:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2",
                 @"D:\Steam\steamapps\common\Slay the Spire 2",
                 @"E:\Steam\steamapps\common\Slay the Spire 2",
             })
    {
        if (Directory.Exists(d)) return d;
    }
    throw new Exception("找不到游戏安装目录。请在 Program.cs 的 FindGame() 里显式写上路径。");
}

var gameRoot = FindGame();
var dataDir = Path.Combine(gameRoot, "data_sts2_windows_x86_64");
var modDll = args.Length > 0
    ? args[0]
    : Path.Combine(AppContext.BaseDirectory, "BetterAscension.dll");

Console.WriteLine("=== BetterAscension PatchCheck（纯解析模式）===");
Console.WriteLine($"游戏 : {gameRoot}");
Console.WriteLine($"模组 : {modDll}");
Console.WriteLine();

if (!File.Exists(modDll))
{
    Console.WriteLine($"找不到模组 DLL：{modDll}");
    Console.WriteLine("用法: PatchCheck [BetterAscension.dll 的路径]");
    return 1;
}

AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
{
    var simple = new AssemblyName(e.Name).Name + ".dll";
    var p = Path.Combine(dataDir, simple);
    if (File.Exists(p)) return Assembly.LoadFrom(p);

    var mods = Path.Combine(gameRoot, "mods");
    if (Directory.Exists(mods))
    {
        foreach (var f in Directory.EnumerateFiles(mods, simple, SearchOption.AllDirectories))
            return Assembly.LoadFrom(f);
    }
    return null;
};

var sts2 = Assembly.LoadFrom(Path.Combine(dataDir, "sts2.dll"));
Console.WriteLine($"sts2.dll 已加载：{sts2.GetTypes().Length} 个类型");

Assembly mod;
try
{
    mod = Assembly.LoadFrom(modDll);
}
catch (ReflectionTypeLoadException ex)
{
    Console.WriteLine("加载模组 DLL 时类型解析失败：");
    foreach (var e in ex.LoaderExceptions.Where(x => x is not null).Take(10))
        Console.WriteLine("  " + e!.Message);
    return 1;
}

const BindingFlags AllMethods =
    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

// ────────────────────────────────────────────────────────────────
//  复刻 Harmony 的"按名字找方法"语义
//
//  ★ 必须尊重 MethodType：getter 与 setter 在反射里是同名的两个方法
//    （get_Xxx / set_Xxx），只看名字会误报歧义。
//    第一次写本工具时就踩了这个假阳性 —— CharacterStatsMaxAscensionPatch
//    带了 MethodType.Getter，Harmony 能正确消歧，而工具却报"2 个重载，歧义"。
// ────────────────────────────────────────────────────────────────
static List<MethodBase> ResolveByName(Type type, string name, bool constructors, MethodType? methodType)
{
    if (constructors)
    {
        return type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                   .Cast<MethodBase>().ToList();
    }

    var all = type.GetMethods(AllMethods).ToList();

    return methodType switch
    {
        // getter / setter 各自只有一个，没有歧义
        HarmonyLib.MethodType.Getter => all.Where(m => m.Name == "get_" + name).Cast<MethodBase>().ToList(),
        HarmonyLib.MethodType.Setter => all.Where(m => m.Name == "set_" + name).Cast<MethodBase>().ToList(),
        HarmonyLib.MethodType.Constructor => type.GetConstructors(AllMethods).Cast<MethodBase>().ToList(),
        _ => all.Where(m => m.Name == name).Cast<MethodBase>().ToList(),
    };
}

var patchClasses = mod.GetTypes()
    .Where(t => t.GetCustomAttributes(typeof(HarmonyPatch), false).Length > 0)
    .OrderBy(t => t.FullName, StringComparer.Ordinal)
    .ToList();

Console.WriteLine($"发现 {patchClasses.Count} 个补丁类");
Console.WriteLine();

var failures = new List<string>();

foreach (var type in patchClasses)
{
    // 优先看有没有 [HarmonyTargetMethod] / [HarmonyTargetMethods]：
    // 有的话说明作者**已经显式消歧**，只需确认那个方法存在。
    var hasTargetMethod = type.GetMethods(AllMethods)
        .Any(m => m.GetCustomAttributes(typeof(HarmonyTargetMethod), false).Length > 0);
    var hasTargetMethods = type.GetMethods(AllMethods)
        .Any(m => m.GetCustomAttributes(typeof(HarmonyTargetMethods), false).Length > 0);

    if (hasTargetMethod || hasTargetMethods)
    {
        var kind = hasTargetMethod ? "TargetMethod" : "TargetMethods";
        Console.WriteLine($"  [显式] {type.Name,-38} 用 [{kind}] 自己消歧 → 由游戏运行时决定");
        continue;
    }

    var attrs = type.GetCustomAttributes(typeof(HarmonyPatch), false).Cast<HarmonyPatch>().ToList();
    if (attrs.Count == 0)
    {
        Console.WriteLine($"  [空]   {type.Name,-38} 没有类级 [HarmonyPatch]");
        failures.Add($"{type.Name} 没有类级 [HarmonyPatch]");
        continue;
    }

    var verdicts = new List<string>();
    var bad = false;

    foreach (var attr in attrs)
    {
        var declaring = attr.info?.declaringType;
        var name = attr.info?.methodName;
        var methodType = attr.info?.methodType;
        var isCtor = methodType == HarmonyLib.MethodType.Constructor;

        if (declaring is null)
        {
            // 只有 [HarmonyPatch] 没有 (Type, name) 信息 —— 靠方法级特性，本工具不深挖
            verdicts.Add("类级特性未带目标类型（可能靠方法级特性）");
            continue;
        }

        if (string.IsNullOrEmpty(name) && !isCtor)
        {
            verdicts.Add($"{declaring.Name}: 未指定方法名");
            bad = true;
            continue;
        }

        var overloads = ResolveByName(declaring, name ?? "", isCtor, methodType);
        var suffix = methodType is null or HarmonyLib.MethodType.Normal ? "" : $" [{methodType}]";

        if (overloads.Count == 0)
        {
            verdicts.Add($"{declaring.Name}.{name}{suffix}: **找不到**");
            bad = true;
        }
        else if (overloads.Count == 1)
        {
            verdicts.Add($"{declaring.Name}.{name}{suffix}: 唯一 ✓");
        }
        else
        {
            // 有多个重载 —— Harmony 会报 Ambiguous match，整个类安装失败
            var sigs = overloads.Select(m =>
                "(" + string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name)) + ")");
            verdicts.Add($"{declaring.Name}.{name}{suffix}: **{overloads.Count} 个重载，歧义！** {string.Join(" / ", sigs)}");
            bad = true;
        }
    }

    if (bad)
    {
        Console.WriteLine($"  [失败] {type.Name,-38} {string.Join("；", verdicts)}");
        failures.Add($"{type.Name}: {string.Join("；", verdicts)}");
    }
    else
    {
        Console.WriteLine($"  [OK]   {type.Name,-38} {string.Join("；", verdicts)}");
    }
}

Console.WriteLine();
if (failures.Count == 0)
{
    Console.WriteLine("全部通过：没有目标歧义。");
    return 0;
}

Console.WriteLine($"发现 {failures.Count} 个问题：");
foreach (var f in failures) Console.WriteLine("  - " + f);
Console.WriteLine();
Console.WriteLine("修法：给该补丁类加 [HarmonyTargetMethod]（单个目标）或 [HarmonyTargetMethods]（多个目标），");
Console.WriteLine("      在里面用参数个数/类型精确挑出目标方法。");
return 1;
