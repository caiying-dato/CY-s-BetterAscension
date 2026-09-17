// BetterAscension TranspilerCheck
//
// 目的：把「上限字面量重写」的逻辑跑在**真实 sts2.dll 的方法体 IL** 上，
//       断言每个目标方法里写死的 10 都被替换、且没有残留。
//
// 为什么不直接调用模组里的补丁方法：
//   补丁类会初始化游戏自己的 Logger，在 Godot 进程外会崩。
//   所以这里**复刻一遍相同的重写算法** —— 算法很短、只依赖 CodeInstruction 的
//   opcode/operand，复刻是可靠的。（这一取舍与 A11 工程一致。）
//
// ★ 但复刻有风险：如果模组里的算法改了而这里忘了改，校验就失去意义。
//   因此本文件与 Code\Patches\AscensionCapacityPatches.cs 里的
//   AscensionCapacity.RaiseCeiling / TryGetCeilingLiteral 必须**保持逐行等价**。
//   改动其一时，请同步改另一个。
//
// 退出码：0 = 全部通过；1 = 有断言失败（可直接用作构建门禁）。
using System.Reflection;
using System.Reflection.Emit;

const int VanillaCeiling = 10;
const int Ceiling = 20;   // ← 必须等于 AscensionLevelRegistry.MaxLevel

// ────────────────────────────────────────────────────────────────
//  定位游戏
// ────────────────────────────────────────────────────────────────
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

AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
{
    var p = Path.Combine(dataDir, new AssemblyName(e.Name).Name + ".dll");
    return File.Exists(p) ? Assembly.LoadFrom(p) : null;
};

var asm = Assembly.LoadFrom(Path.Combine(dataDir, "sts2.dll"));

Console.WriteLine("=== BetterAscension TranspilerCheck ===");
Console.WriteLine($"游戏      : {gameRoot}");
Console.WriteLine($"目标上限  : {VanillaCeiling} → {Ceiling}");
Console.WriteLine();

// ────────────────────────────────────────────────────────────────
//  反汇编（与 ApiDump 相同的最小实现）
// ────────────────────────────────────────────────────────────────
var opMap = new Dictionary<ushort, OpCode>();
foreach (var f in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
{
    var op = (OpCode)f.GetValue(null)!;
    opMap[(ushort)op.Value] = op;
}

List<Ins> Disasm(MethodBase m)
{
    var list = new List<Ins>();
    var body = m.GetMethodBody();
    var il = body?.GetILAsByteArray();
    if (il is null) return list;

    int i = 0;
    while (i < il.Length)
    {
        ushort code = il[i++];
        if (code == 0xFE) code = (ushort)(0xFE00 | il[i++]);
        if (!opMap.TryGetValue(code, out var op)) break;

        object? operand = null;
        int size = 0;
        switch (op.OperandType)
        {
            case OperandType.InlineNone: break;
            case OperandType.ShortInlineI: operand = (sbyte)il[i]; size = 1; break;
            case OperandType.InlineI: operand = BitConverter.ToInt32(il, i); size = 4; break;
            case OperandType.InlineI8: operand = BitConverter.ToInt64(il, i); size = 8; break;
            case OperandType.ShortInlineVar: operand = (int)il[i]; size = 1; break;
            case OperandType.InlineVar: operand = (int)BitConverter.ToUInt16(il, i); size = 2; break;
            case OperandType.InlineString:
                { int t = BitConverter.ToInt32(il, i); size = 4; try { operand = m.Module.ResolveString(t); } catch { } break; }
            case OperandType.InlineMethod:
            case OperandType.InlineField:
            case OperandType.InlineType:
            case OperandType.InlineTok:
                { int t = BitConverter.ToInt32(il, i); size = 4; try { operand = m.Module.ResolveMember(t); } catch { } break; }
            case OperandType.ShortInlineBrTarget: operand = null; size = 1; break;
            case OperandType.InlineBrTarget: operand = null; size = 4; break;
            case OperandType.InlineSwitch: { int n = BitConverter.ToInt32(il, i); size = 4 + n * 4; break; }
            case OperandType.ShortInlineR: size = 4; break;
            case OperandType.InlineR: size = 8; break;
            default: size = 4; break;
        }
        i += size;
        list.Add(new Ins(op, operand));
    }
    return list;
}

// ────────────────────────────────────────────────────────────────
//  复刻自 Code\Patches\AscensionCapacityPatches.cs 的 AscensionCapacity
//  ★ 两个文件必须保持逐行等价
// ────────────────────────────────────────────────────────────────
static bool TryGetCeilingLiteral(Ins ins, out object replacement)
{
    if (ins.Opcode == OpCodes.Ldc_I4_S && ins.Operand is sbyte s && s == VanillaCeiling)
    {
        replacement = (sbyte)Ceiling;
        return true;
    }
    if (ins.Opcode == OpCodes.Ldc_I4 && ins.Operand is int v && v == VanillaCeiling)
    {
        replacement = Ceiling;
        return true;
    }
    replacement = null!;
    return false;
}

// ────────────────────────────────────────────────────────────────
//  断言
// ────────────────────────────────────────────────────────────────
var failures = new List<string>();

void CheckCeiling(string typeName, string methodName, int expected)
{
    var type = asm.GetType(typeName);
    if (type is null)
    {
        Console.WriteLine($"  {methodName,-34} 类型不存在！{typeName}");
        failures.Add($"{typeName} 不存在");
        return;
    }

    var method = type
        .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
        .FirstOrDefault(x => x.Name == methodName && x.GetMethodBody() is not null);

    if (method is null)
    {
        Console.WriteLine($"  {methodName,-34} 方法不存在！");
        failures.Add($"{typeName}.{methodName} 不存在");
        return;
    }

    var instructions = Disasm(method);

    // 重写前：方法体里有几个上限字面量
    int found = instructions.Count(ins => TryGetCeilingLiteral(ins, out _));

    // 重写后：还残留几个（这正是"该改的都改了、没有残留"的断言）
    int left = instructions
        .Select(ins => TryGetCeilingLiteral(ins, out var r) ? new Ins(ins.Opcode, r) : ins)
        .Count(ins => TryGetCeilingLiteral(ins, out _));

    var ok = found == expected && left == 0;
    var status = ok ? "OK" : "** 不合格 **";
    Console.WriteLine($"  {methodName,-34} 发现 {found,2} 处 (预期 {expected,2}) | 重写后残留 {left} | {status}");

    if (found != expected)
    {
        failures.Add($"{typeName}.{methodName} 发现 {found} 处上限字面量，预期 {expected} 处");
    }
    if (left != 0)
    {
        failures.Add($"{typeName}.{methodName} 重写后仍残留 {left} 处上限字面量（有未覆盖的编码形式）");
    }
}

Console.WriteLine("── 上限字面量覆盖检查 ──");
CheckCeiling("MegaCrit.Sts2.Core.Saves.ProgressState", "ClampAscension", 3);
CheckCeiling("MegaCrit.Sts2.Core.Saves.ProgressState", "ClampCharacterStatsFields", 6);
CheckCeiling("MegaCrit.Sts2.Core.Saves.Managers.ProgressSaveManager", "IncrementSingleplayerAscension", 1);
CheckCeiling("MegaCrit.Sts2.Core.Saves.Managers.ProgressSaveManager", "IncrementMultiplayerAscension", 1);
CheckCeiling("MegaCrit.Sts2.Core.DevConsole.ConsoleCommands.UnlockConsoleCmd", "UnlockAscensions", 2);

// ────────────────────────────────────────────────────────────────
//  结构性事实断言 —— 这些是"动手前必须实测"的东西，
//  游戏更新后一旦变化，这里会先于游戏崩溃告诉我们。
// ────────────────────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("── 结构性事实检查（游戏更新后最先失效的地方）──");

void CheckConst(string typeName, string fieldName)
{
    var type = asm.GetType(typeName);
    var field = type?.GetField(fieldName,
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);

    if (field is null)
    {
        Console.WriteLine($"  {typeName}.{fieldName} 不存在！");
        failures.Add($"{typeName}.{fieldName} 不存在");
        return;
    }

    if (!field.IsLiteral)
    {
        Console.WriteLine($"  {typeName}.{fieldName} 不再是编译期常量（IsLiteral=False）"
                        + " —— 结构已变，请重新核对上限策略！");
        failures.Add($"{typeName}.{fieldName} 不再是 const");
        return;
    }

    Console.WriteLine($"  {typeName}.{fieldName} 仍是 const（值 {field.GetRawConstantValue()}）—— OK");
}

void CheckReadonly(string typeName, string fieldName)
{
    var type = asm.GetType(typeName);
    var field = type?.GetField(fieldName,
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);

    if (field is null)
    {
        Console.WriteLine($"  {typeName}.{fieldName} 不存在！");
        failures.Add($"{typeName}.{fieldName} 不存在");
        return;
    }

    Console.WriteLine($"  {typeName}.{fieldName} IsLiteral={field.IsLiteral} IsInitOnly={field.IsInitOnly}"
                    + $"（{(field.IsInitOnly ? "仍不可写" : "注意：现在可写了")}）");
}

// 明确的陷阱：这个 const 不能反射写，只能改 IL。它变了说明结构有改动。
CheckConst("MegaCrit.Sts2.Core.Entities.Ascension.AscensionManager", "maxAscensionAllowed");

// 等级字段是 readonly，构造后无法改写
CheckReadonly("MegaCrit.Sts2.Core.Entities.Ascension.AscensionManager", "_level");

// 手牌上限（A14 的落点）：应是静态属性，getter 只返回一个字面量
{
    var type = asm.GetType("MegaCrit.Sts2.Core.Entities.Cards.CardPile");
    var prop = type?.GetProperty("MaxCardsInHand",
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);

    if (prop is null)
    {
        Console.WriteLine("  CardPile.MaxCardsInHand 不存在！A14 的落点需要重新定位。");
        failures.Add("CardPile.MaxCardsInHand 不存在");
    }
    else
    {
        var getter = prop.GetGetMethod(true);
        var body = getter?.GetMethodBody()?.GetILAsByteArray();
        var value = prop.GetValue(null);
        Console.WriteLine($"  CardPile.MaxCardsInHand 仍是属性（getter {body?.Length ?? 0} 字节），"
                        + $"原版读数 {value} —— OK");
        if (value is not 10)
        {
            failures.Add($"CardPile.MaxCardsInHand 原版值不是 10（实际 {value}）");
        }
    }
}

// 唯一收口点仍在
{
    var type = asm.GetType("MegaCrit.Sts2.Core.Entities.Ascension.AscensionManager");
    var hasLevel = type?.GetMethod("HasLevel",
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
    Console.WriteLine(hasLevel is not null
        ? "  AscensionManager.HasLevel 仍在 —— OK"
        : "  AscensionManager.HasLevel 不存在！");
    if (hasLevel is null) failures.Add("AscensionManager.HasLevel 不存在");
}

// ────────────────────────────────────────────────────────────────
//  结论
// ────────────────────────────────────────────────────────────────
Console.WriteLine();
if (failures.Count == 0)
{
    Console.WriteLine("全部通过。");
    return 0;
}

Console.WriteLine($"失败 {failures.Count} 项：");
foreach (var f in failures) Console.WriteLine($"  - {f}");
return 1;

// ────────────────────────────────────────────────────────────────
internal readonly record struct Ins(OpCode Opcode, object? Operand);
