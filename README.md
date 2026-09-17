# CY's BetterAscension

为《杀戮尖塔 2》扩展进阶难度：**新增进阶 11 ~ 20**。

[![游戏版本](https://img.shields.io/badge/游戏-v0.111.0-blue)](#)
[![前置](https://img.shields.io/badge/前置-BaseLib%203.4.7-orange)](#前置模组)
[![语言](https://img.shields.io/badge/语言-简体中文%20%7C%20English-green)](#)

---

## 这是什么

《杀戮尖塔 2》本体提供进阶 0 ~ 10。这个模组把进阶扩展到 **20**，
新增的 10 阶沿用本体的「**高阶自动包含所有低阶效果**」规则 ——
选了进阶 15，就会同时吃到 1 ~ 15 的全部效果。

进阶 11 ~ 20 的效果如下（都可以在游戏内的进阶面板里看到）：

| 等级 | 名称 | 效果 |
|:---:|:---|:---|
| **A11** | 精英强敌 | **精英**敌人在战斗开始时获得 **1** 点**力量**。 |
| **A12** | 水土不服 | 每进入新的阶段时，最大生命值减少 **6** 点。 |
| **A13** | ？富有？ | 在商人处购买卡牌后，额外扣除 **15** 枚**金币**的小费。 |
| **A14** | 咬紧牙关 | 手牌上限减少 **1**。 |
| **A15** | 进阶之灾+ | **进阶之灾**失去**虚无**。 |
| **A16** | 通货膨胀+ | 商店售卖的卡牌价格提升 **20%**。 |
| **A17** | 珍稀 | **药水**的掉落概率降低。 |
| **A18** | 柔弱身躯 | 进入战斗时获得 **1** 层**脆弱**。 |
| **A19** | 无力攻击 | 进入战斗时获得 **1** 层**虚弱**。 |
| **A20** | 最终考验 | 进入**首领**战时，随机将一张**诅咒**加入你的卡组。 |

---

## 安装

### 方式一：下载发布包（推荐）

到本仓库的 [**Releases**](../../releases) 页面，**根据你是否已经装了 BaseLib 选择其中一个**：

| 你的情况 | 下载哪个 | 大小 |
|:---|:---|:---|
| **已经装了 BaseLib**（例如从创意工坊订阅过） | `BetterAscension-vX.Y.Z.zip` | 约 31 KB |
| **还没有 BaseLib** | `BetterAscension-vX.Y.Z+BaseLib.zip` ← **含前置，省事** | 约 485 KB |

> ⚠️ **别下错**：两个包**不要同时用**。如果下载了带 BaseLib 的包，
> 但你本来就已经订阅了创意工坊的 BaseLib，请把包里那个 `BaseLib` 文件夹删掉再放进游戏 ——
> 同一模组存在两份时，游戏会警告并**禁用创意工坊版本**，之后 BaseLib 作者更新就不会自动生效了。

下载后：

1. 在 Steam 里**右键游戏 → 管理 → 浏览本地文件**，打开《杀戮尖塔 2》的安装目录。
2. 把压缩包里的 **`mods` 文件夹**解压/拖进这个安装目录。

   以带前置的包为例，完成后应该是：

   ```
   Slay the Spire 2\
     mods\
       BetterAscension\
         BetterAscension.dll
         BetterAscension.json
         BetterAscension.pck
       BaseLib\                 ← 仅 +BaseLib 版才有
         BaseLib.dll
         BaseLib.json
         BaseLib.pck
   ```

   > 游戏目录里通常已经有 `mods` 文件夹了，直接**合并**即可
   > （系统提示"是否合并/替换"时选合并）。

3. 启动游戏 → **设置 → 模组设置** → 确认 `BetterAscension`（以及 `BaseLib`）都是**启用**状态。
4. 开始新游戏时，在进阶面板把等级调到 11 以上。

### 方式二：Steam 创意工坊

> 本模组暂未上传创意工坊。

前置 [**BaseLib**](https://steamcommunity.com/sharedfiles/filedetails/?id=3737335127)
可以从创意工坊订阅（好处是作者更新后自动升级）；
订阅了它就用**不带 BaseLib** 的那个发布包。

---

## 前置模组

必需：[**BaseLib**](https://steamcommunity.com/sharedfiles/filedetails/?id=3737335127) `>= 3.4.7`（作者 Alchyr）

本模组只用到 BaseLib 的 **`[CustomEnum]`** 一个特性 —— 用来往游戏的
`AscensionLevel` 枚举里新增成员。除此之外的本地化、能力施加、IL 修改、补丁
全部走游戏原生 API 与 Harmony，没有依赖 BaseLib 的其它功能。

**获取方式**：从创意工坊订阅，或使用发布包里的 `+BaseLib` 版本（已附带，开箱即用）。

---

## 常见问题

<details>
<summary><b>进阶面板最高只能选到 10</b></summary>

BaseLib 没有启用。到 **设置 → 模组设置** 把它打开，然后**完全退出并重启游戏**
（模组只在游戏启动时加载，返回主菜单是不够的）。
</details>

<details>
<summary><b>界面上的进阶标题显示成 <code>LEVEL_11.title</code> 这样的原文</b></summary>

这说明本地化文件没被读到。请确认 `BetterAscension.pck` 与 `BetterAscension.dll`
**在同一个文件夹里**。

界面上出现原始 key（形如 `xxx.title`）**永远是「查不到文案」**，
而不是文案本身写错了 —— 这是 100% 可靠的判别信号。
</details>

<details>
<summary><b>模组装好了但游戏里没反应</b></summary>

依次检查三件事：

1. `mods` 下两个文件夹的名字是否**恰好**是 `BetterAscension` 和 `BaseLib`
   —— 游戏按文件夹名找模组清单，名字不对会**静默失效**；
2. **设置 → 模组设置** 里是否已启用；
3. 是否**完全重启**过游戏。
</details>

<details>
<summary><b>想看日志排查问题</b></summary>

日志在：

```
%APPDATA%\SlayTheSpire2\logs\godot.log
```

筛选本模组的输出：

```powershell
Select-String -Path "$env:APPDATA\SlayTheSpire2\logs\godot.log" -Pattern '\[BetterAscension\]'
```

启动时应该能看到补丁安装与进阶注册的记录。
</details>

---

## 已知限制

- **多人游戏未充分测试。** 各效果都按「对所有玩家生效」实现，但没有做过联机验证。
- **与其它进阶扩展模组可能冲突。** 它们会修改同一批硬编码常量、并各自注册
  `AscensionLevel`，建议只启用一个。
- **游戏处于抢先体验阶段**，版本更新可能改动内部接口。
  本模组的所有补丁都写了「失败可诊断」的日志，接口变动时会明确报警而不是静默失效。

---

## 从源码构建

<details>
<summary>展开构建说明</summary>

### 环境要求

| 依赖 | 版本 | 说明 |
|:---|:---|:---|
| .NET SDK | 9.0 或更高 | 游戏目标框架是 `net9.0`；更高的 SDK（如 10.x）也能编译 |
| Godot | **4.5.1 mono** | 仅 `dotnet publish` 导出 `.pck` 时需要 |
| 杀戮尖塔 2 | `v0.111.0` | 提供 `sts2.dll` 与 `0Harmony.dll` |

### 步骤

```powershell
# 1. 配置本机路径
Copy-Item local.props.template local.props
#    编辑 local.props：设置 <Sts2Path>（游戏安装目录）与 <GodotPath>（Godot 可执行文件）

# 2. 编译（只改了 .cs 时够用，但不会重打 .pck）
dotnet build .\BetterAscension.csproj -c Debug

# 3. 完整部署：编译 + 打 .pck + 装进 mods + 断言 .pck 内容
.\deploy.cmd

# 4. 生成可上传 Releases 的发布包
.\release.cmd

# 5. 改了 IL 补丁后必跑（几秒，比进游戏试快得多）
.\check.cmd
```

> ⚠️ **游戏运行时无法部署** —— 它会锁住 `BetterAscension.dll`。
> `deploy.ps1` 会先检测并询问是否关闭游戏。

### 项目结构

```
.
├─ Code/                        模组源码（16 个文件 / 约 1600 行）
│  ├─ AscensionLevelRegistry.cs ★ 单一常量中心：进阶等级、枚举身份、本地化 key
│  ├─ PowerGiver.cs             同步施加能力的助手
│  ├─ RunBookkeeping.cs         按运行局实例挂载的幂等状态
│  └─ Patches/                  各条进阶的补丁
├─ BetterAscension/             ★ 要打进 .pck 的资源
│  └─ localization/{zhs,eng}/ascension.json
├─ tools/
│  ├─ ApiDump/                  查游戏 API：签名 / IsLiteral / 读 IL / 扫调用者
│  ├─ PatchCheck/               在游戏外验证补丁目标有无歧义（秒级）
│  └─ TranspilerCheck/          把 IL 重写逻辑跑在真实 sts2.dll 上做断言
├─ docs/                        开发过程的技术文档（见下）
└─ deploy / release / check     一键脚本
```

</details>

---

## 技术文档

开发过程中踩的坑与验证方法都记录在 `docs/` 下，对做同类模组的人可能有用：

| 文档 | 内容 |
|:---|:---|
| [落点研究](docs/落点研究-A11~A20.md) | 每条进阶在游戏内部的挂载点，含真实 `sts2.dll` 的实测证据 |
| [实测记录](docs/实测记录-Harmony补丁静默缺失.md) | Harmony 补丁的 6 类**静默失败**：现象 → 日志证据 → 根因 → 正解 |
| [本机环境备忘](docs/本机环境备忘.md) | 工具链搭建中踩到的坑（Mono 版 Godot、PowerShell 编码等） |
| [A11 复盘](docs/A11复盘/) | 本项目前身（单条进阶 11）的完整复盘 |

<details>
<summary><b>四条最值得记住的结论</b></summary>

1. **Harmony 的「补丁没装上」是完全静默的。**
   `Patch()` 不报错、不警告，只返回一个更短的列表。
   入口必须**同时报告「发现了几个」和「装上了几个」**，差集一眼可见。

2. **目标方法只要有重载，就必须显式指定目标。**
   `[HarmonyPatch(typeof(X), nameof(X.Y))]` 只给方法名；
   属性要加 `MethodType.Getter`/`Setter`，有重载要用 `[HarmonyTargetMethod]` 按签名挑。
   否则整个补丁类安装失败。

3. **补丁装上了 ≠ 代码会执行。**
   每个补丁体内部都要有「钩子被调用了」的诊断行，否则无法区分这两种情况。
   找挂载点时优先看**游戏本体自己是怎么实现同类效果的**。

4. **任何 `static` 可变状态，只要它的键在新的生命周期里会重复，就一定会出事。**
   本项目就因此出过「每次开游戏只生效一次」「开新档后永久失效」这类 bug。
   优先把状态挂到实例上（如 `ConditionalWeakTable`），让生命周期自动对齐。

</details>

---

## 致谢

- [**BaseLib**](https://github.com/Alchyr/BaseLib-StS2)（Alchyr）—— 本模组的前置依赖，
  提供了扩展 `AscensionLevel` 枚举所需的 `[CustomEnum]`
- [Harmony](https://github.com/pardeike/Harmony) —— 运行时补丁库

---

## 许可证

本模组源码采用 **MIT 许可证**，详见 [LICENSE](LICENSE)。

发布包内附带的 **BaseLib 版权归其作者 Alchyr 所有**，按其原始许可分发。
