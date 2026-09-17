# 实测记录：Harmony 补丁的四种"静默失败"

> 本模组第一次在游戏里实跑，前后三轮才让 10 条进阶全部生效。
> 四类问题**全都在编译期看不出来**，且**全都不报错**。
> 记录格式沿用 `docs\A11复盘\03-必须避开的坑.md`：**现象 → 日志证据 → 根因 → 正解 → 通用教训**。
>
> **最终结果：A11~A20 全部实测生效。**

---

## 快速索引：四类静默失败

| # | 类别 | 现象 | 一句话根因 |
| --- | --- | --- | --- |
| 1 | **压根没被发现** | 一半补丁类不见踪影 | 嵌套类 + 容器类没有 `[HarmonyPatch]` → 入口的 `GetTypes().Where(...)` 选不中 |
| 2 | **目标有歧义** | 整个补丁类安装失败 | 目标有重载（含属性 getter/setter、构造函数），只给方法名无法消歧 |
| 3 | **钩子不覆盖目标** | 补丁装上了，代码不执行 | 挂错了方法：该方法只处理敌人 / 那是另一个重载 |
| 4 | **异步时机错位** | 代码执行了，但看到的是旧状态 | 给返回 `Task` 的方法打 Postfix = 得到"方法刚开始"而非"方法完成后" |

---

## 第 1 类：嵌套类 + 方法级 `[HarmonyPatch]` 不生效

### 现象
16 个补丁类里只装了 3 个，进阶上限没有真的抬起来。

### 日志证据
```
补丁安装结束，共 3 个补丁类、3 个方法。        ← ★ 只有 3 个
[WARN] Progress parse: ValidationError { Path = CharStats.[0],
        Message = MaxAscension (20) exceeds allowed (10), clamping }
```

### 根因
第一版把 5 个 Transpiler 写成**外层类里的嵌套类**，并在**方法上**挂 `[HarmonyPatch]`：

```csharp
// ❌ 不生效
internal static class AscensionCeilingTranspilers      // 外层容器，自身没有 [HarmonyPatch]
{
    [HarmonyPatch(typeof(ProgressState), nameof(ProgressState.ClampAscension))]   // ← 方法级
    [HarmonyTranspiler]
    internal static IEnumerable<CodeInstruction> ClampAscension(...) { ... }
}
```

外层容器没有 `[HarmonyPatch]`，所以入口的
`GetTypes().Where(t => t 有 [HarmonyPatch])` **根本不会选中那些嵌套类** ——
它们连"被尝试安装"的机会都没有。而 `Patch()` 对它返回 0 且**不抛异常**。

### 正解
**顶层类 + 类级 `[HarmonyPatch]` + `internal static` 补丁方法**，一个目标一个类。

### 配套：让失败可见
入口改成先报"发现了几个"、再报"装上了几个"，差集一眼可见；
并对"返回 0 个方法"的补丁类明确 `Warn`。

### 通用教训
> **Harmony 的"补丁没装上"是完全静默的。**
> 入口必须同时报告"发现了几个"和"装上了几个"，否则一半补丁消失了你看到的仍是一行"顺利完成"。
>
> **不要用嵌套类写补丁。** 一个补丁目标 = 一个顶层类 + 类级 `[HarmonyPatch]`。

---

## 第 2 类：目标方法有重载 → 歧义 → 整个类安装失败

### 现象
A18 / A19 / A20 三条都没生效（它们全挂 `CombatManager.AfterCreatureAdded`），A17 也"无法验证"。

### 日志证据
```
[ERROR] 补丁类 BossCursePatch 安装失败
[ERROR] 补丁类 PlayerCombatStartDebuffPatch 安装失败
[ERROR] 补丁类 PotionOddsPatch 安装失败
补丁安装结束，共 13 个补丁类、13 个方法。

HarmonyLib.HarmonyException: Ambiguous match for HarmonyMethod[(
    class=...CombatManager, methodname=AfterCreatureAdded, type=Normal, args=undefined)]

HarmonyLib.HarmonyException: Patching exception in method null      ← PotionOddsPatch
```

### 根因
`[HarmonyPatch(typeof(X), nameof(X.Y))]` **只给方法名**。目标有重载时 Harmony 无法选择：

| 目标 | 重载 |
| --- | --- |
| `CombatManager.AfterCreatureAdded` | `(Creature)` 与 `(Creature, CombatState)` |
| `PotionRewardOdds` 构造函数 | `(Rng)` 与 `(Single, Rng)` |

### 正解
用 `[HarmonyTargetMethod]`（单目标）或 `[HarmonyTargetMethods]`（多目标）按签名精确挑：

```csharp
[HarmonyPatch(typeof(CombatManager))]
internal static class PlayerCombatStartPatch
{
    [HarmonyTargetMethod]
    internal static MethodBase? Target() =>
        typeof(CombatManager).GetMethods(All)
            .Where(m => m.Name == nameof(CombatManager.AfterCreatureAdded))
            .FirstOrDefault(m => m.GetParameters().Length == 1);   // 按参数个数，不能只按类型
}
```

### 通用教训
> **只要目标有重载（含属性 getter/setter、构造函数），就必须显式指定目标。**
> 属性要加 `MethodType.Getter` / `MethodType.Setter`；
> 有重载的方法/构造函数要用 `[HarmonyTargetMethod]` 按签名挑。
>
> **按参数个数而不是参数类型挑** —— 两个重载的首参可能都是 `Creature`，
> 而 Postfix 只要注入 `Creature` 就两个都能匹配，Harmony 才会报歧义。

---

## 第 3 类：钩子不覆盖目标 —— `AfterCreatureAdded` 只处理敌人

### 现象
补丁类**安装成功**（16/16），但 A18/A19/A20 **一条日志都没有**，连诊断行都没有。

### 日志证据
```
补丁安装结束，共 16 个补丁类、17 个方法。     ← 全装上了
（但 A18/A19/A20 的诊断行一条都没有）
```

### 根因
`CombatManager.AfterCreatureAdded(Creature, CombatState)` 的状态机：

```
IL_006A: ldfld     creature
IL_0070: callvirt  Creature::get_IsEnemy()
IL_0075: brfalse.s ->196          ← ★ 不是敌人就直接跳到收尾
...
IL_00E5: SetResult() / ret
```

**它只处理敌人**（给怪物 `RollMove`），玩家生物根本走不进来。

> ⚠️ 我上一轮的推断是**错的**：我只看到"有 `IsEnemy` 判定"，就推断
> "玩家也会走这个方法，只是跳过怪物分支"。**没看那个分支跳到哪。**
> 这是本轮最大的教训 —— 见下方"通用教训"。

同时确认 `CombatManager.StartCombatInternal` 的状态机里调用的是
`AfterCreatureAdded(Creature, CombatState)`（**私有二参重载**），不是我挂的一参版本。

### 正解
改用本体自己的机制。`Hook.BeforeCombatStart` 的状态机：

```
IL_001E: callvirt  IRunState::IterateHookListeners(ICombatState)
IL_004D: callvirt  AbstractModel::BeforeCombatStart() -> Task
```

它遍历**钩子监听者模型**并调用它们的 `BeforeCombatStart()`
（本体的 `GalvanicPower`、遗物 `Anchor` / `Kusarigama` / `LetterOpener` 就是这么生效的）。

**我们给它打 Prefix**：

```csharp
[HarmonyPatch(typeof(Hook), nameof(Hook.BeforeCombatStart))]
internal static class PlayerCombatStartPatch
{
    [HarmonyPrefix]
    internal static void Prefix(ICombatState combatState)
    {
        foreach (var creature in combatState.PlayerCreatures) { ApplyFrail(creature); ApplyWeak(creature); }
    }
}
```

为什么 Prefix 可行：Prefix 在**调用方线程上同步执行**，时机正好是
"战斗刚开始、玩家第一回合之前"；而实测 `ICombatState` 上就有
`Players` / `PlayerCreatures`，生物直接拿得到。

> 中间试过"自定义 `PowerModel` 覆写 `BeforeCombatStart`"，但那要重生
> `Type` / `StackType` 等抽象成员、还要额外的挂载步骤（订阅 `RunManager.RunStarted`），
> 明显更重，已放弃。Prefix 更直接。

### 通用教训
> **看到一个条件分支，必须看它跳到哪，不能只看到"有这个判定"就推断行为。**
> 我因为这一步偷懒，多花了两轮排查。

> **补丁装上了不等于代码会执行。** 这两件事必须分别验证 ——
> 所以每个补丁体内部都要有"钩子被调用了"的诊断行（限量），
> 否则你无法区分"没装上"和"装上了但没触发"。

> 找挂载点时，优先找**本体自己实现了同类效果的地方**，看它走哪条路。
> `GalvanicPower` / `Anchor` 直接指向了 `Hook.BeforeCombatStart`。

---

## 第 4 类：给返回 `Task` 的方法打 Postfix → 看到的是旧状态

### 现象
A12 补丁**安装成功**，但完全没生效，而且**一条日志都没有**。

### 根因
第一版挂在 `RunManager.EnterNextAct()` 的 Postfix 上，而它返回 `Task`：

```csharp
public Task EnterNextAct() {
    var sm = new <EnterNextAct>d__189();
    sm.<>t__builder.Start(ref sm);     // ← 状态机开始跑，遇到 await 就返回
    return sm.<>t__builder.Task;
}
```

Harmony 的 Postfix 在这个方法体执行完之后运行 ——
也就是"状态机刚启动、还没真正换幕"的那一刻。
此时 `state.CurrentActIndex` 读到的仍是**旧值 0**，于是永远停在"第 0 幕，跳过"。

### 正解
改挂**真正改变状态的那个成员** —— `RunState.CurrentActIndex` 的 **setter**：

```csharp
[HarmonyPatch(typeof(RunState), nameof(RunState.CurrentActIndex), MethodType.Setter)]
internal static class ActAttritionPatch
{
    [HarmonyPostfix]
    internal static void Postfix(RunState __instance) { ... }
}
```

它是自动属性，setter 恰好在"幕号真正改变的那一瞬间"被调用，纯同步；
而且**正好就是我们的幂等键**（记录"哪一幕已经扣过"），语义天然对齐。

### 通用教训
> **给返回 `Task` 的方法打 Postfix，得到的是"方法开始执行"这个时刻，不是"方法完成后"。**
>
> 想在某件事真正发生之后做事，优先级：
> 1. **挂真正改变状态的那个成员**（属性 setter / 字段）—— 最可靠
> 2. 找同步时机的钩子
> 3. 本体自己留的模型虚方法（如 `BeforeCombatStart`）
>
> 不要指望 async 方法的 Postfix。

---

## 第 5 类（附带）：A14 刷了 166 条日志

`CardPile.MaxCardsInHand` 是**超热 getter**，一局下来被调用几百次。
补丁必须每次都改返回值，但没必要每次都打日志 —— 改成只打一次（`_logged` 标志）。

---

## 第 6 类：内存态幂等标记与存档不同步 —— A20 的 SL 失效 bug

> 这是 **CY 实测发现** 的，不在我原来的四类里。它比前四类都更隐蔽，
> 因为**功能本身完全正常**，只在"存档 → 退出 → 读档"这条路径上坏掉。

### 现象
A20 在 Boss 战生效（诅咒正确加入卡组），但**保存存档再进入（SL）之后**：
- 那张诅咒**不在牌组里**（没被存进存档）
- 而**重进 Boss 房也不会再补一张**

净效果：**SL 一次，A20 这一局的这个效果就永久失效了。**

### 根因：标记与诅咒的**生命周期不一致**

第一版用内存态 `HashSet<int>` 按房间号记录"这场 Boss 战已给过诅咒"。
而游戏的存档时机是：

```
RunManager.EnterMapPointInternal
    ├─ ... 建好房间 ...
    └─ SaveManager.SaveRun(...)          ← ★ 存档在这里
（之后才）CombatManager.StartCombatInternal
    └─ Hook.BeforeCombatStart            ← ★ 我们在这里加诅咒
```

也就是说：**诅咒是在那一次存档之后才加的 → 那次存档里没有它。**
读档后牌组回到"没有诅咒"的状态，可内存里的 `HashSet` **还在**
（它不随存档清空）→ 幂等判定认为"已经给过了" → **不再补加**。

**一句话：标记活得比被标记的东西久。**

### 正解：让标记与效果**同生共死**

把标记做成**一张卡**，与诅咒在**同一次操作**里一起加入牌组。
于是它们必然一起进档、一起读回 —— **牌组成为唯一真相**，
不再有任何内存态状态需要与存档同步。

```csharp
if (DeckContains<AscensionCurseMark>(player)) return;   // 判定完全基于牌组内容
player.Deck.AddInternal(curse, -1, silent: true);        // 加诅咒
player.Deck.AddInternal(mark,  -1, silent: true);        // 加标记 —— 两者同生共死
```

顺带把这个需求也一并解决了："读档重进同一 Boss 房不该重复加" ——
因为标记跟着存档回来了，重进时能正确判定"已经给过了"。

### 标记卡怎么造（BaseLib 的扩展点）

BaseLib 提供了 `CustomCardModel`（自动实现 `ICustomModel` + `ILocalizationProvider`）：

```csharp
public sealed class AscensionCurseMark
    : CustomCardModel                                     // 基类
{
    public AscensionCurseMark()
        : base(-1, CardType.Curse, CardRarity.Curse, TargetType.None,
               showInCardLibrary: false,   // 不进卡牌库
               autoAdd: false)             // 不进卡池
    { }

    public override List<(string, string)>? Localization => [ ("title", "…"), ("description", "…") ];
}
```

### ⚠️ 为什么不用 `SavedSpireField`（看起来最像正确答案的那个）

BaseLib 有个 `SavedSpireField<TKey, TVal>`，注释是"会自动保存和加载的 SpireField"，
看起来正是为这种需求准备的。**但它的源码里明确留着 TODO：**

```csharp
//TODO - Patch SerializablePlayer
//Allow SavedSpirefield<player> to be saved and loaded to this
```

也就是说**它支持不了 `Player`** —— 只对支持 `SavedProperties` 的模型
（主要是卡牌与遗物）生效，实现挂在 `SavedProperties.FromInternal/FillInternal` 上。

> 教训：**读扩展库的源码，不要只读它的注释和名字。**
> 如果我按注释判断，会花很多时间在一个从设计上就没做完的特性上。

### 附带：本地化 key 是编译器分析器告诉我的

写完标记卡后编译报：

```
error STS001: Localization BETTERASCENSION-ASCENSION_CURSE_MARK.title,
              BETTERASCENSION-ASCENSION_CURSE_MARK.description not found
              for symbol 'BetterAscension.Cards.AscensionCurseMark'
```

`Alchyr.Sts2.ModAnalyzers` 直接给出了它期望的 key 格式
（`{根命名空间大写}-{模型名大写}.title`），并要求这些 key 必须出现在
csproj 里 `<AdditionalFiles>` 指向的本地化 json 中。
所以补了 `BetterAscension/localization/{eng,zhs}/cards.json`，
并同步把 `deploy.ps1` 的 `.pck` 断言从 2 个文件扩到 4 个。

### 通用教训

> **任何"只存在内存里"的状态，只要它与某个会被存档的东西配对，迟早会不同步。**
> 修复方向永远是：**把标记放到与效果同一个存储里**，让它们原子地一起持久化。
>
> 判断"要不要担心 SL"的方法：问自己
> **"这个标记如果活过了存档点，而它标记的东西没活过，会发生什么？"**
> 如果答案是"效果永久失效"，就必须修。

---

## 配套工具：`tools\PatchCheck` —— 在游戏外验证补丁目标

每轮排查都要"启动游戏 → 进对局 → 退出 → 读日志"，太慢。
`tools\PatchCheck` 加载真实 `sts2.dll` 与模组 DLL，
把每个补丁类的**目标解析结果**在几秒内报出来：

```
  [OK]   ActAttritionPatch            RunState.CurrentActIndex [Setter]: 唯一 ✓
  [OK]   CharacterStatsMaxAscensionPatch  CharacterStats.MaxAscension [Getter]: 唯一 ✓
  [显式] PlayerCombatStartPatch           用 [TargetMethod] 自己消歧
  [失败] SomePatch                    Foo.Bar: **2 个重载，歧义！** () / (Int32)
```

退出码可作构建门禁。**它覆盖第 1、2 类问题，但不覆盖第 3、4 类** ——
那两类是"钩子选错/时机错位"，只能靠运行时的诊断日志发现。

### ⚠️ 这个工具自己踩过的两个坑（都已修）

**① 不能在游戏外**执行**补丁方法。** 第一版真的调用了
`Harmony.CreateClassProcessor(type).Patch()`，结果 Transpiler 内部的 `ModLog.Info`
触发 `ModLog` 静态构造 → 初始化游戏 `Logger` → 去问 `Godot.OS` 要命令行参数 →
**在 Godot 进程外直接段错误 `0xC0000005`**（崩在 `godotsharp_string_new_with_utf16_chars`）。
现在只做**反射解析**，不执行任何补丁方法。

**② 必须尊重 `MethodType`。** 属性 getter 与 setter 在反射里是同名的两个方法
（`get_Xxx` / `set_Xxx`），只看名字会误报歧义。
第一版工具就把带了 `MethodType.Getter` 的 `CharacterStatsMaxAscensionPatch`
误判成"2 个重载，歧义" —— **工具自己的假阳性**。

---

## 附带确认的事实

### ① BaseLib 生成的是哈希值，且 `Enum.GetNames()` 看不到
```
已注册进阶 11 → AscensionLevel.-1684051577（生成值 -1684051577）
AscensionLevel 现有成员：None, SwarmingElites, ..., DoubleBoss     ← 只有原版 11 个
```
BaseLib 是**运行时注入枚举值**，没往元数据里加枚举项。
推论：**`HasLevel(AscensionLevel)` 对我们没用**（它的语义是 `_level >= (int)level`，
而我们的值是哈希）。本模组所有进阶判定都走**原始等级整数**比较。

### ② `MerchantEntry._player` 是 protected 字段，Publicizer 没公开
需要 `AccessTools.Field` 反射读取。

### ③ `.pck` 里会混入 `.godot/global_script_class_cache.cfg`
它的**内容里**含有 `res://tools/...` 之类路径字符串。
所以**不要**写"从 .pck 里正则抓 `res://` 路径来判断是否泄漏"的校验 —— 必然误报。

---

## 最终验收状态（A11~A20 全部实测生效）

| 条目 | 落点 | 状态 |
| --- | --- | --- |
| A11 精英 +1 力量 | `MonsterModel.AfterAddedToRoom` Postfix | ✅ |
| A12 换幕 -6 最大生命 | `RunState.CurrentActIndex` **setter** Postfix | ✅ |
| A13 买卡扣 15 小费 | `MerchantEntry.InvokePurchaseCompleted` Postfix | ✅ |
| A14 手牌上限 10→9 | `CardPile.MaxCardsInHand` getter Postfix | ✅ |
| A15 进阶之灾失去虚无 | `AscendersBane.CanonicalKeywords` getter Postfix | ✅ |
| A16 卡价 +20% | `Hook.ModifyMerchantPrice` Postfix | ✅ |
| A17 药水掉率 40%→30% | `PlayerOddsSet` 构造 + `FromSerializable` Postfix | ✅ |
| A18/A19 开局脆弱/虚弱 | `Hook.BeforeCombatStart` **Prefix** | ✅ |
| A20 Boss 战加诅咒 | 同上（`BeforeCombatStart` Prefix） | ✅ |

**★ 可复用的钩子知识（A21~A30 直接查这张表）**

| 想做的事 | 用哪个钩子 | 备注 |
| --- | --- | --- |
| 敌人开局加能力 | `MonsterModel.AfterAddedToRoom` Postfix | 同步，A11 验证 |
| 玩家开局加能力 | `Hook.BeforeCombatStart` **Prefix** | 同步，参数带 `ICombatState.PlayerCreatures` |
| 战斗开始（敌人侧） | `CombatManager.AfterCreatureAdded` | ⚠️ **只对敌人调用**，玩家不走 |
| 幕切换 | `RunState.CurrentActIndex` **setter** | ⚠️ **不要**用 `RunManager.EnterNextAct`（async） |
| 商店改价 | `Hook.ModifyMerchantPrice` Postfix | 官方入口，同步 |
| 购买成功 | `MerchantEntry.InvokePurchaseCompleted` Postfix | 同步；玩家用 `_player` 反射读 |
| 改一个上限（属性） | 对应属性的 **getter** Postfix | 优先找属性，不要改 IL |
| 玩家概率 | `PlayerOddsSet` 构造 + `FromSerializable` | 两条路径都要补 |
| 加能力到生物 | `ModelDb.Power<T>()` + `MutableClone()` + `ApplyInternal` | 同步内核，见 `PowerGiver` |
| 扣金币 | `PlayerCmd.LoseGold` | 返回 `Task` 但**实际同步**，自带 `int.Max(0,…)` 夹取 |
| 改最大生命 | `Creature.SetMaxHpInternal` | 同步；设**绝对值**且必须 ≥ 0 |
