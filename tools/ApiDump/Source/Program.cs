// BetterAscension ApiDump
// 在真实 sts2.dll 上做只读探测：查签名 / 查 IsLiteral / 读方法 IL / 扫调用者。
//
// 用法（命令在 args[0]，第二个参数起都是命令参数）：
//   dotnet run -- type   <类型全名或短名>             列成员
//   dotnet run -- il     <类型名> <方法名> [重载序号]   反汇编方法（自动跟进 async 状态机）
//   dotnet run -- callers <类型名> <成员名>            全程序集扫"谁引用了它"
//   dotnet run -- const  <类型名> <字段名>             查 IsLiteral / IsInitOnly / 值
//   dotnet run -- find   <子串>                       按名字搜类型
//   dotnet run -- str    <字面量>                     扫哪个方法里出现该字符串
using System.Reflection;
using System.Reflection.Emit;

// ---------- 定位游戏 ----------
static string FindGame() {
    foreach (var d in new[] {
        @"C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2",
        @"D:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2",
        @"D:\Steam\steamapps\common\Slay the Spire 2",
        @"E:\Steam\steamapps\common\Slay the Spire 2",
    }) if (Directory.Exists(d)) return d;
    throw new Exception("找不到游戏安装目录。请在 Program.cs 的 FindGame() 里显式写路径。");
}

var gameRoot = FindGame();
var dataDir = Path.Combine(gameRoot, "data_sts2_windows_x86_64");

AppDomain.CurrentDomain.AssemblyResolve += (_, e) => {
    var simple = new AssemblyName(e.Name).Name + ".dll";
    var p = Path.Combine(dataDir, simple);
    if (File.Exists(p)) return Assembly.LoadFrom(p);
    // 模组 DLL（BaseLib 等）也从 mods 下找
    var mods = Path.Combine(gameRoot, "mods");
    if (Directory.Exists(mods)) {
        foreach (var f in Directory.EnumerateFiles(mods, simple, SearchOption.AllDirectories))
            return Assembly.LoadFrom(f);
    }
    return null;
};

var asm = Assembly.LoadFrom(Path.Combine(dataDir, "sts2.dll"));
Type[] types;
try { types = asm.GetTypes(); }
catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t is not null).ToArray()!; }

Console.WriteLine($"[env] game  = {gameRoot}");
Console.WriteLine($"[env] types = {types.Length}");

static string Short(Type t) {
    if (t.IsGenericType) return t.Name.Split('`')[0] + "<" + string.Join(",", t.GetGenericArguments().Select(Short)) + ">";
    return t.Name;
}
static string Sig(MethodBase mb) {
    var ps = string.Join(", ", mb.GetParameters().Select(p => $"{Short(p.ParameterType)} {p.Name}"));
    var ret = mb is MethodInfo mi ? " -> " + Short(mi.ReturnType) : "";
    var st = mb.IsStatic ? " [static]" : "";
    var np = mb.IsPublic ? "" : " [nonpublic]";
    return $"{mb.Name}({ps}){ret}{st}{np}";
}
static Type? Resolve(Type[] ts, string name) =>
    ts.FirstOrDefault(t => t.FullName == name) ?? ts.FirstOrDefault(t => t.Name == name);

// ---------- 反汇编 ----------
var opMap = new Dictionary<ushort, OpCode>();
foreach (var f in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)) {
    var op = (OpCode)f.GetValue(null)!;
    opMap[(ushort)op.Value] = op;
}

List<(int Offset, OpCode Op, object Operand)> Disasm(MethodBase m) {
    var body = m.GetMethodBody();
    var list = new List<(int, OpCode, object)>();
    if (body is null) return list;
    var il = body.GetILAsByteArray();
    if (il is null) return list;
    int i = 0;
    while (i < il.Length) {
        int off = i;
        ushort code = il[i++];
        if (code == 0xFE) code = (ushort)(0xFE00 | il[i++]);
        if (!opMap.TryGetValue(code, out var op)) break;
        object? operand = null; int size = 0;
        switch (op.OperandType) {
            case OperandType.InlineNone: break;
            case OperandType.ShortInlineI: operand = (sbyte)il[i]; size = 1; break;
            case OperandType.InlineI: operand = BitConverter.ToInt32(il, i); size = 4; break;
            case OperandType.InlineI8: operand = BitConverter.ToInt64(il, i); size = 8; break;
            case OperandType.ShortInlineVar: operand = (int)il[i]; size = 1; break;
            case OperandType.InlineVar: operand = (int)BitConverter.ToUInt16(il, i); size = 2; break;
            case OperandType.InlineString:
                { int t = BitConverter.ToInt32(il, i); size = 4; try { operand = m.Module.ResolveString(t); } catch { } break; }
            case OperandType.InlineMethod: case OperandType.InlineField:
            case OperandType.InlineType: case OperandType.InlineTok:
                { int t = BitConverter.ToInt32(il, i); size = 4; try { operand = m.Module.ResolveMember(t); } catch { } break; }
            case OperandType.ShortInlineBrTarget: operand = "->" + (i + 1 + (sbyte)il[i]); size = 1; break;
            case OperandType.InlineBrTarget: operand = "->" + (i + 4 + BitConverter.ToInt32(il, i)); size = 4; break;
            case OperandType.InlineSwitch:
                { int n = BitConverter.ToInt32(il, i); size = 4 + n * 4; operand = $"switch[{n}]"; break; }
            case OperandType.ShortInlineR: operand = BitConverter.ToSingle(il, i); size = 4; break;
            case OperandType.InlineR: operand = BitConverter.ToDouble(il, i); size = 8; break;
            default: size = 4; break;
        }
        i += size;
        list.Add((off, op, operand));
    }
    return list;
}

// async 方法体全在嵌套状态机的 MoveNext 里 —— 这是读游戏逻辑的关键。
// 注意：状态机类型名是 <方法名>d__<序号>，序号是**编译器的全类方法编号**，
// 不是该名字重载的序号，所以不能用 GetMethods()[i] 的下标去猜。按名字前缀找。
static MethodBase? Unwrap(MethodBase m) {
    if (m.GetMethodBody() is not null) return m;
    var nested = m.DeclaringType?.GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public)
        .Where(t => t.Name.StartsWith("<" + m.Name + ">d", StringComparison.Ordinal))
        .OrderBy(t => t.Name, StringComparer.Ordinal)
        .ToList();
    if (nested is null || nested.Count == 0) return m;
    // 有多个同名状态机时，选参数个数匹配的那个（迭代器/异步各一）
    var mv = nested
        .Select(t => t.GetMethod("MoveNext", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance))
        .FirstOrDefault(x => x is not null);
    return mv ?? m;
}

void PrintIl(MethodBase m) {
    var real = Unwrap(m);
    var isAsync = real != m;
    Console.WriteLine($"\n### {m.DeclaringType?.FullName}.{Sig(m)}");
    if (isAsync) Console.WriteLine($"    [async] 实际逻辑在 {real.DeclaringType?.Name}.MoveNext");
    var ins = Disasm(real);
    if (ins.Count == 0) { Console.WriteLine("    (无方法体)"); return; }
    foreach (var (off, opcode, operand) in ins) {
        var op = operand switch {
            MethodBase mb => "  " + mb.DeclaringType?.Name + "::" + Sig(mb),
            FieldInfo fi => "  " + fi.DeclaringType?.Name + "::" + fi.Name + " : " + Short(fi.FieldType),
            Type t => "  " + t.FullName,
            string s => "  \"" + s + "\"",
            null => "",
            _ => "  " + operand,
        };
        Console.WriteLine($"    IL_{off:X4}: {opcode.Name,-12}{op}");
    }
}

// ---------- 命令 ----------
var argv = args;
if (argv.Length == 0) { Console.WriteLine("用法: type|il|callers|const|find|str ..."); return; }
var cmd = argv[0].ToLowerInvariant();

switch (cmd) {
    case "find": {
        var sub = argv[1];
        foreach (var t in types.Where(t => (t.FullName ?? "").Contains(sub, StringComparison.OrdinalIgnoreCase))
                               .OrderBy(t => t.FullName))
            Console.WriteLine("  " + t.FullName);
        break;
    }
    case "type": {
        var t = Resolve(types, argv[1]);
        if (t is null) { Console.WriteLine("TYPE NOT FOUND: " + argv[1]); return; }
        Console.WriteLine($"\n### {t.FullName}   abstract={t.IsAbstract} sealed={t.IsSealed} enum={t.IsEnum}");
        Console.WriteLine($"    base: {t.BaseType?.FullName}");
        if (t.IsEnum) {
            foreach (var v in Enum.GetNames(t)) Console.WriteLine($"    {v} = {Convert.ToInt64(Enum.Parse(t, v))}");
            return;
        }
        const BindingFlags BF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                              | BindingFlags.Static | BindingFlags.DeclaredOnly;
        foreach (var c in t.GetConstructors(BF)) Console.WriteLine($"    ctor({string.Join(", ", c.GetParameters().Select(p => $"{Short(p.ParameterType)} {p.Name}"))})");
        foreach (var f in t.GetFields(BF).OrderBy(f => f.Name))
            Console.WriteLine($"    field {f.Name} : {Short(f.FieldType)}{(f.IsStatic ? " [static]" : "")}{(f.IsLiteral ? " [CONST!]" : "")}{(f.IsInitOnly ? " [readonly]" : "")}");
        foreach (var p in t.GetProperties(BF).OrderBy(p => p.Name))
            Console.WriteLine($"    prop  {p.Name} : {Short(p.PropertyType)} {{{(p.CanRead ? "get;" : "")}{(p.CanWrite ? "set;" : "")}}}");
        foreach (var m in t.GetMethods(BF).Where(m => !m.IsSpecialName).OrderBy(m => m.Name))
            Console.WriteLine($"    meth  {Sig(m)}");
        break;
    }
    case "const": {
        var t = Resolve(types, argv[1]);
        var f = t?.GetField(argv[2], BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
        if (f is null) { Console.WriteLine("FIELD NOT FOUND"); return; }
        Console.WriteLine($"{t!.FullName}.{f.Name}: IsLiteral={f.IsLiteral} IsInitOnly={f.IsInitOnly} " +
                          $"IsStatic={f.IsStatic} Type={Short(f.FieldType)}");
        if (f.IsLiteral) Console.WriteLine($"  → 编译期常量，反射 SetValue 会抛 FieldAccessException，只能改 IL");
        else Console.WriteLine($"  → 普通字段，反射可写（但注意读取方可能已内联）");
        break;
    }
    case "nested": {
        // 列嵌套类型（async 状态机 / 迭代器），用来精确定位要读 IL 的 MoveNext
        var t = Resolve(types, argv[1]);
        if (t is null) { Console.WriteLine("TYPE NOT FOUND: " + argv[1]); return; }
        foreach (var n in t.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic).OrderBy(n => n.Name))
            Console.WriteLine("  " + n.Name);
        break;
    }
    case "mv": {
        // 直接读某个嵌套状态机的 MoveNext（nested 列出的名字）
        var t = Resolve(types, argv[1]);
        var n = t?.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault(x => x.Name == argv[2]);
        var mv = n?.GetMethod("MoveNext", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
        if (mv is null) { Console.WriteLine("NOT FOUND: " + argv[1] + "+" + argv[2]); return; }
        PrintIl(mv);
        break;
    }
    case "il": {
        var t = Resolve(types, argv[1]);
        if (t is null) { Console.WriteLine("TYPE NOT FOUND: " + argv[1]); return; }
        int idx = argv.Length > 3 ? int.Parse(argv[3]) : 0;
        var ms = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                  .Where(m => m.Name == argv[2]).ToArray();
        if (ms.Length == 0) { Console.WriteLine($"METHOD NOT FOUND: {argv[2]}"); return; }
        if (idx >= ms.Length) { Console.WriteLine($"只有 {ms.Length} 个重载"); return; }
        PrintIl(ms[idx]);
        break;
    }
    case "callers": {
        var t = Resolve(types, argv[1]);
        if (t is null) { Console.WriteLine("TYPE NOT FOUND"); return; }
        var name = argv[2];
        // 用 (模块, MetadataToken) 匹配 —— MemberInfo.Equals 跨 ResolveMember 不可靠，
        // 这是第一版扫出 0 处的原因。
        var wanted = new HashSet<(Module, int)>();
        void Add(MemberInfo? mi) {
            if (mi is null) return;
            try { wanted.Add((mi.Module, mi.MetadataToken)); } catch { }
        }
        foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static).Where(m => m.Name == name)) { Add(m); }
        foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static).Where(p => p.Name == name)) { Add(p); Add(p.GetGetMethod(true)); Add(p.GetSetMethod(true)); }
        foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static).Where(f => f.Name == name)) { Add(f); }
        if (wanted.Count == 0) { Console.WriteLine("MEMBER NOT FOUND: " + name); return; }
        Console.WriteLine($"\n### 扫描 {t.FullName}.{name} 的引用者（{wanted.Count} 个元数据目标）");
        int hits = 0;
        foreach (var tt in types) {
            IEnumerable<MethodBase> ms;
            try {
                ms = tt.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                                 | BindingFlags.Static | BindingFlags.DeclaredOnly).Cast<MethodBase>()
                         .Concat(tt.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static));
            } catch { continue; }
            foreach (var m in ms) {
                var body = m.GetMethodBody(); if (body is null) continue;
                var il = body.GetILAsByteArray(); if (il is null) continue;
                for (int i = 0; i < il.Length - 4; i++) {
                    var op = il[i];
                    if (op != 0x28 && op != 0x6F && op != 0x7B && op != 0x80 && op != 0x7C && op != 0x7D
                        && op != 0x73 && op != 0xFE) continue;
                    var at = i;
                    if (op == 0xFE) { if (il.Length - i < 5) break; at = i + 1; }
                    try {
                        // 只接受"在指令边界上"的解析：粗略但足够，误报极少
                        var mem = m.Module.ResolveMember(BitConverter.ToInt32(il, at + 1));
                        if (mem is not null && wanted.Contains((mem.Module, mem.MetadataToken))) {
                            Console.WriteLine($"    {tt.FullName}.{m.Name}");
                            hits++; break;
                        }
                    } catch { }
                }
                if (hits > 400) { Console.WriteLine("    …（截断）"); return; }
            }
        }
        Console.WriteLine($"    共 {hits} 处");
        break;
    }
    case "str": {
        var needle = argv[1];
        Console.WriteLine($"\n### 含有字面量 \"{needle}\" 的方法");
        foreach (var tt in types) {
            IEnumerable<MethodBase> ms;
            try {
                ms = tt.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                                 | BindingFlags.Static | BindingFlags.DeclaredOnly).Cast<MethodBase>()
                         .Concat(tt.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static));
            } catch { continue; }
            foreach (var m in ms) {
                var body = m.GetMethodBody(); if (body is null) continue;
                var il = body.GetILAsByteArray(); if (il is null) continue;
                for (int i = 0; i < il.Length - 4; i++) {
                    if (il[i] != 0x72) continue;
                    try {
                        if (m.Module.ResolveString(BitConverter.ToInt32(il, i + 1)) is string s && s.Contains(needle, StringComparison.Ordinal)) {
                            Console.WriteLine($"    {tt.FullName}.{m.Name}   → \"{s}\"");
                            break;
                        }
                    } catch { }
                }
            }
        }
        break;
    }
    default:
        Console.WriteLine("未知命令: " + cmd);
        break;
}
