# 02 · 游戏 API 实证清单

本文所有内容都来自**本机 `sts2.dll`（v0.111.0）的反射 / IL dump**，不是推测。
命名空间与签名可直接照抄；标注 **UNVERIFIED** 的是我没能实测到的。

> 游戏处于抢先体验阶段，版本更新会改动这些内部结构。
> **改代码前请用 [04](04-调试与验证工作流.md) 里的 `tools/ApiDump` 重新 dump 一次确认。**

---

## 1. 进阶（Ascension）系统

### 1.1 枚举与命名空间

```csharp
// MegaCrit.Sts2.Core.Entities.Ascension
public enum AscensionLevel {
    None = 0, SwarmingElites = 1, WearyTraveler = 2, Poverty = 3, TightBelt = 4,
    AscendersBane = 5, Inflation = 6, Scarcity = 7, ToughEnemies = 8,
    DeadlyEnemies = 9, DoubleBoss = 10,
}
```

**等级数字 = 枚举值**，0..10。要加"进阶 11"，等级号就是 **11**（枚举值由 BaseLib 生成，见 1.4）。

### 1.2 `AscensionHelper`（工具类，注意命名空间是 `Helpers` 不是 `Entities.Ascension`）

```csharp
// MegaCrit.Sts2.Core.Helpers
public static class AscensionHelper {
    public  static LocString GetTitle(int level);         // LocString("ascension", "LEVEL_" + level.ToString("D2") + ".title")
    public  static LocString GetDescription(int level);   // …".description"
    internal static string   GetKey(int level);            // 就是 "LEVEL_" + level.ToString("D2")
    public  static bool      HasAscension(AscensionLevel level);   // → RunManager.Instance.HasAscension(level)
    public  static int       GetValueIfAscension(AscensionLevel level, int ascensionValue, int fallbackValue);
    public  static float     GetValueIfAscension(AscensionLevel level, float ascensionValue, float fallbackValue);
    public  static decimal   GetValueIfAscension(AscensionLevel level, decimal ascensionValue, decimal fallbackValue);
}
```

实测（在 Godot 进程外调用）：

```
GetTitle(1)       = LocString table ascension entry LEVEL_01.title
GetDescription(1) = LocString table ascension entry LEVEL_01.description
```

**结论：本地化表名固定为 `ascension`，key 是 `LEVEL_11.title` / `LEVEL_11.description`。**
不需要改 key，也不需要补丁 `GetKey`——只要把你的 `ascension.json` 送到游戏能读到的位置（见第 4 节）。

### 1.3 `AscensionManager`

```csharp
// MegaCrit.Sts2.Core.Entities.Ascension
public class AscensionManager {
    public AscensionManager(int level);            // _level = level
    public AscensionManager(AscensionLevel level); // _level = (int)level  ← 危险，见坑 2
    private int _level;                            // 实例字段（非 const）
    public const int maxAscensionAllowed = 10;     // ★ const！反射 IsLiteral=True，改不了
    public bool HasLevel(AscensionLevel level);    // IL: !(_level < (int)level)
    public void ApplyEffectsTo(Player player);
}
```

**`maxAscensionAllowed` 是死代码**：全程序集扫描显示**没有任何方法引用它**。
真正的上限是散落在各处方法体里的字面量 `10`（见 1.5）。

### 1.4 自定义枚举值（BaseLib）

```csharp
using BaseLib.Patches.Content;

[CustomEnum("EmpoweredElites")]
public static AscensionLevel EmpoweredElites;
```

- BaseLib **3.4.7** 的 `CustomEnumAttribute` 构造函数需要一个 `string name`（旧版可能无参）。
- 生成时机是 `ModelDb.Init` 期间。日志证据：

```
[BaseLib] Generated KeyGenerator for enum …AscensionLevel with starting value 11 | IsFlag: False
```

- **★★ 生成的值不是 11，而是一个哈希值**（本机实测 `-648373254`）。
  所以**绝对不要**用 `(int)EmpoweredElites` 当等级号。正确做法是把"枚举值"只当身份标记：

```csharp
public const int Level = 11;   // 等级号永远用这个常量

public static bool IsOurLevel(AscensionLevel level) =>
    _resolved && level == EmpoweredElites;      // 按身份比较，不看数值
```

### 1.5 上限 `10` 到底写在哪（IL 实录，共 13 处）

这些是**字面量**（`ldc.i4.s 10`），只能改 IL：

| 方法 | 处数 | 作用 |
| --- | --- | --- |
| `ProgressState.ClampAscension` | 3 | 读档时夹取运行局等级（检查 / 报错文案 / `return 10`） |
| `ProgressState.ClampCharacterStatsFields` | **6** | 夹取 `MaxAscension` 与 `PreferredAscension` 各 2 处 + 2 处报错文案 |
| `ProgressSaveManager.IncrementSingleplayerAscension` | 1 | 胜利后解锁下一阶的上限 |
| `ProgressSaveManager.IncrementMultiplayerAscension` | 1 | 同上（多人） |
| `UnlockConsoleCmd.UnlockAscensions` | 2 | 控制台"解锁全部"写入 10 |

> ⚠️ `ClampCharacterStatsFields` 有 6 处，很容易只改到 4 处。
> 我第一版按"getter 后跟 10"的模式匹配，就漏了两处——**漏掉会让 A11 在读档时被夹回 10**。
> 用 [04](04-调试与验证工作流.md#2-il-校验器) 的校验器可以几秒内发现。

**这些方法里除了"上限"没有别的含义为 10 的常量**（其余常量是 0 / 1 / -1 和字符串长度 37/44/…），
所以可以安全地"整体重写该方法中所有 `10`"。

### 1.6 进阶等级在游戏里的**四个**存放位置

```
NAscensionPanel.Ascension    (int, 有 get/set)   —— 角色选择面板显示、玩家按箭头改的
StartRunLobby.Ascension      (int, private set)  —— 大厅；选定角色时被夹取
RunState.AscensionLevel      (int, 只在构造时设置) —— 真正进入运行局的值
AscensionManager._level      (int, private)      —— 运行局里 HasLevel 的依据
```

**读取运行局等级请用 `RunManager.Instance.State.AscensionLevel`。**

`RunState.AscensionLevel` 的**写入者只有 `RunState.CreateShared`（构造函数）**，
全局扫描确认构造后没有任何地方再写它。

### 1.7 大厅夹取：为什么"面板显示 11、进局是 10"

```csharp
// StartRunLobby.SetSingleplayerAscensionAfterCharacterChanged(ModelId characterId)
MaxAscension = GetMaxAscensionAcrossAllCharacters();          // = Max(所有角色.CharacterStats.MaxAscension)
Ascension    = Math.Min(stats.PreferredAscension, MaxAscension);

// StartRunLobby.GetMaxAscensionAcrossAllCharacters()
int max = 0;
foreach (var s in SaveManager.Instance.Progress.CharacterStats.Values)
    max = Math.Max(max, s.MaxAscension);   // ← 存档里是 10！
return max;
```

所以即使面板给了 11，选定角色的瞬间就被 `Min(11, 10) = 10` 冲掉。

**收口点：`CharacterStats.MaxAscension` 的 getter。** 它同时被大厅算上限和角色选择面板读取：

```csharp
[HarmonyPatch(typeof(CharacterStats), nameof(CharacterStats.MaxAscension), MethodType.Getter)]
[HarmonyPostfix]
private static void MaxAscensionGetterPostfix(ref int __result) {
    if (__result < 11) __result = 11;   // 只改返回值，不写回存档
}
```

> 好处：**不修改存档**。玩家存档里的 `MaxAscension` 仍是 10，卸载模组后完全兼容。

`CharacterStats.MaxAscension` 的读者（全局扫描）：
`ProgressState.ClampCharacterStatsFields` · `MergeCharacterStats` · `ProgressSaveManager.IncrementSingleplayerAscension` ·
`StartRunLobby.BeginRunLocally` / `SetSingleplayerAscensionAfterCharacterChanged` / `GetMaxAscensionAcrossAllCharacters` / `UpdatePreferredAscension` ·
`NCharacterStats.LoadStats`（角色属性界面显示）。

---

## 2. 补丁落点：进阶相关

| 目标 | 补丁类型 | 说明 |
| --- | --- | --- |
| `ModelDb.Init` | Postfix | 最早的、`[CustomEnum]` 已生成完成的时机 |
| `NAscensionPanel.SetMaxAscension(int)` | **Prefix** | 把上限抬到 11，否则箭头按不到 |
| `AscensionManager.HasLevel(AscensionLevel)` | Postfix | **唯一收口点**：`RunManager.HasAscension` 与 `AscensionHelper.HasAscension` 都走它 |
| `CharacterStats.MaxAscension` getter | Postfix | 见 1.7 |
| 1.5 表中 5 个方法 | Transpiler | 抬上限 |

`RunManager.HasAscension` 的 IL 只有三行：

```
call RunManager::get_IsInProgress ; brtrue ; ldc.i4.0 ; ret
call RunManager::get_AscensionManager ; ldarg.1 ; callvirt AscensionManager::HasLevel ; ret
```

所以**只补 `HasLevel` 就够了**，不必单独补 `HasAscension`。

---

## 3. 战斗 / 怪物 / 能力

### 3.1 房间与地图

```csharp
// MegaCrit.Sts2.Core.Rooms
public enum RoomType { Unassigned=0, Monster=1, Elite=2, Boss=3, Treasure=4, Shop=5, Event=6, RestSite=7, Map=8 }

// MegaCrit.Sts2.Core.Map
public enum MapPointType { Unassigned=0, Unknown=1, Shop=2, Treasure=3, RestSite=4, Monster=5, Elite=6, Boss=7, Ancient=8 }
```

**判定"是不是精英战"**：

```csharp
var runState = RunManager.Instance?.State;
bool isElite = runState?.CurrentMapPoint?.PointType == MapPointType.Elite
            || runState?.CurrentRoom?.RoomType   == RoomType.Elite;
```

实测日志确认可用（普通战斗 `mapPoint=Monster, room=Monster`，精英战触发时为 `Elite`）。

### 3.2 `MonsterModel`

```csharp
// MegaCrit.Sts2.Core.Models
public abstract class MonsterModel : AbstractModel {
    public virtual Task AfterAddedToRoom();     // ★ 基类只是 Task.CompletedTask
    public virtual void BeforeRemovedFromRoom();
    public void SetUpForCombat();               // 同步
    public virtual Task PerformMove();
    public Creature Creature { get; }
    public LocString Title { get; }
    public ICombatState CombatState { get; }
    public bool IsPerformingMove { get; }
}
```

- 65 个具体怪物**覆写**了 `AfterAddedToRoom`，里面用 `await PowerCmd.Apply<T>(...)` 挂先天能力。
  例：`MechaKnight` 的 IL 里 `ldc.i4.3` → `PowerCmd.Apply<ArtifactPower>(ctx, creature, 3, creature, null, 0)`。
- 调用链：`CombatManager.StartCombatInternal` → `CombatManager.AfterCreatureAdded` →
  `Creature.AfterAddedToRoom` → `MonsterModel.AfterAddedToRoom`。

### 3.3 能力（Power）

```csharp
// 命令侧：MegaCrit.Sts2.Core.Commands
public static class PowerCmd {
    public static Task<T?> Apply<T>(PlayerChoiceContext choiceContext, Creature target, decimal amount,
                                    Creature applier, CardModel? cardSource, bool silent) where T : PowerModel;
    public static Task<IReadOnlyList<T>> Apply<T>(PlayerChoiceContext choiceContext, IEnumerable<Creature> targets,
                                    decimal amount, Creature applier, CardModel? cardSource, bool silent);
    public static Task Apply(PlayerChoiceContext ctx, PowerModel power, Creature target, decimal amount,
                             Creature applier, CardModel? cardSource, bool silent);
    public static Task<int> ModifyAmount(PlayerChoiceContext ctx, PowerModel power, decimal offset,
                             Creature applier, CardModel? cardSource, bool silent);
}
```

```csharp
// 模型侧：MegaCrit.Sts2.Core.Models.PowerModel
public PowerModel ToMutable();                          // ★ 无参：克隆，数量为 0
public PowerModel ToMutable(int initialAmount);         // ★ 有参：会在 Owner 为空时设数量 → 空引用！
public void ApplyInternal(Creature owner, decimal amount, bool silent);   // 纯同步
public void SetAmount(int amount, bool silent);
public int Amount { get; }
public Creature? Owner { get; }
```

`PowerModel.ApplyInternal` 的 IL：

```
SetAmount 检查 amount == 0 → 直接 return
AssertMutable()
set_Owner(owner)
SetAmount((int)amount, silent)
Owner.ApplyPowerInternal(this)     // ← 挂到生物身上并触发"数值变化"通知
```

```csharp
// 生物侧：MegaCrit.Sts2.Core.Entities.Creatures.Creature
public void ApplyPowerInternal(PowerModel power);   // 同步；非 PowerModel.ApplyInternal 调用会抛
public T? GetPower<T>() where T : PowerModel;
public int GetPowerAmount<T>();
public IReadOnlyList<PowerModel> Powers { get; }
public bool CanReceivePowers { get; }
public bool IsEnemy { get; }        // 也有 IsPlayer / IsMonster / IsPet / IsPrimaryEnemy
```

**在 `AfterAddedToRoom` 里同步加能力（推荐给"先天能力"类效果）：**

```csharp
var power = ModelDb.Power<StrengthPower>().ToMutable();   // ★ 不要用 ToMutable(amount)
power.ApplyInternal(creature, 1, silent: true);
```

### 3.4 ★ 主线程异步陷阱

`PowerCmd.Apply` 返回 `Task`，**必须 await**。它内部会 await 多个钩子：

```
CombatManager.IsEnding 检查 → Creature.CanReceivePowers 检查 → FindExistingInstanceForStacking
→ ToMutable → [await BeforeApplied] → [await ModifyAmount / Hook.BeforePowerAmountChanged …]
```

在 Harmony 补丁（跑在主线程游戏循环内）里用 `awaiter.GetResult()` 阻塞等待它会**整个游戏冻结**。
详见 [03 坑 4](03-必须避开的坑.md#坑-4进精英战直接卡死)。

---

## 4. 本地化装载

### 4.1 基础表

`LocManager.LoadTablesFromPath(language, allowOverride)`：

```
path = LocalizationAssetDir + "/" + language       // LocalizationAssetDir = "res://localization"
files = DirAccess.Open(path).GetFiles()            // 该目录下所有文件
表名 = 文件名（不含 .json）
```

即基础表是 `res://localization/<lang>/<table>.json`。

### 4.2 模组表

`ModManager.GetModdedLocTables(string language, string file)` 的 IL（字符串拼接实录）：

```
"res://" + <mod.manifest.id> + "/localization/" + <language> + "/" + <file>
→ ResourceLoader.Exists(路径) 才返回
```

**所以模组本地化必须是 `<ModId>/localization/<lang>/<table>.json`**，且目录名等于模组 id。
见 [01 §2.1](01-开发环境与新建模组.md#21--资源目录名是有约束的不是随便起的)。

### 4.3 进阶文本

| 项 | 值 |
| --- | --- |
| 表名 | `ascension` |
| 文件 | `MyMod/localization/{eng,zhs}/ascension.json` |
| key | `LEVEL_11.title` / `LEVEL_11.description` |
| 富文本 | `[gold]…[/gold]`、`[blue]…[/blue]`、`[red]…[/red]`（BBCode） |

```json
{
  "LEVEL_11.title": "精英强化",
  "LEVEL_11.description": "[gold]精英[/gold]敌人在战斗开始时获得[blue]1[/blue]点[gold]力量[/gold]。"
}
```

**基表与模组表是"合并"而不是"替换"**（日志：`Found loc table from mod: zhs xxx.json. Merging with base loc table`），
所以只写你新增的 key 即可，不会覆盖掉 0..10 的原文。

### 4.4 文本没生效时如何自查

1. 日志里搜 `Found loc table from mod:`，看有没有你的表名；**没有就是路径/目录名错了**。
2. 日志里搜 `Loading Godot PCK`，确认 `.pck` 被加载。
3. 界面上直接显示成 key（如 `LEVEL_11.title`）→ 100% 是"找不到该 key"，不是文案写错。

---

## 5. 其它有用的事实

| 事实 | 值 |
| --- | --- |
| 运行局状态 | `RunManager.Instance.State`（**不是** `CurrentRun`） |
| 运行局等级 | `RunState.AscensionLevel`（int） |
| 存档行局等级 | `MegaCrit.Sts2.Core.Saves.SerializableRun.Ascension`（int） |
| 一次性初始化 | `MegaCrit.Sts2.Core.Helpers.OneTimeInitialization.ExecuteEssential_Patch2` → `NGame.GameStartup()` |
| 日志 | `%APPDATA%\SlayTheSpire2\logs\godot.log`（每次启动还会另存 `godot<时间戳>.log`） |
| `DefaultInterpolatedStringHandler.AppendFormatted<T>(T)` | **泛型**，`GetMethod(name, [typeof(int)])` 找不到它 → 抛 `MissingMethodException` |

---

## 6. 明确未验证（UNVERIFIED）

诚实标注，避免别人当成事实用：

- 联机（多人）下同步行为：本模组抬高了多人上限，但**没有实测多人同局**。
- 成就/存档校验是否因 A11 而异常：未测。
- `AscensionLevel 11` 在**读档续玩**（`RunState.FromSerializable`）路径下的表现：未测（只测了新开局）。
- 其它进阶扩展模组同时启用时的冲突：只做了静态分析，未实测。
- `CustomRun`（自定义局）界面的进阶面板：`NCustomRunScreen.MaxAscensionChanged` 也调用 `SetMaxAscension`，
  理论上同样生效，未实测。
