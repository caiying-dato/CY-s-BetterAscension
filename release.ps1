<#
.SYNOPSIS
    生成可直接上传 GitHub Releases 的发布包（两个版本）。

.DESCRIPTION
    产出两个压缩包，区别只在**要不要附带前置模组 BaseLib**：

        release\BetterAscension-vX.Y.Z.zip          只含本模组
        release\BetterAscension-vX.Y.Z+BaseLib.zip  本模组 + 前置 BaseLib

    为什么要分两个
    --------------
    已经装过 BaseLib 的玩家（例如从创意工坊订阅过）**不应该**再收到一份重复的。
    游戏的加载器检测到同一 id 存在两份时会警告并**禁用创意工坊版本**，
    作者更新后就不生效了。所以「已装 BaseLib 的人」用不带前置的那个包。

    ★ 两个包解压后都只包含 mods\ 与说明文件，结构刻意如此：

        mods/
          BetterAscension/
            BetterAscension.dll / .json / .pck / .pdb
          BaseLib/                    ← 仅 +BaseLib 版有
            BaseLib.dll / .json / .pck
        安装说明.txt

    玩家解压到《杀戮尖塔 2》**安装目录**即可 —— 其中的 mods 文件夹会与游戏自带的合并。

    注意 mods 下的**子文件夹名必须等于各模组清单里的 id**：
    游戏按 `mods\<id>\<id>.json` 找清单，而本地化路径又是 `res://<id>/localization/...`
    写死的。名字错了会静默失效，所以脚本里有断言。

.EXAMPLE
    pwsh -File .\release.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$projectDir = $PSScriptRoot
$modName = 'BetterAscension'
$baseLibName = 'BaseLib'
$baseLibVersion = '3.4.7'

# ── 读清单的工具函数 ──────────────────────────────────────────────
# 只想从模组清单里取几个**顶层**字段（id / name / author / version）。
# 这段刻意手写而不依赖 JSON 解析器，原因有二：
#
# ⚠️ 陷阱 1：不要用 `ConvertFrom-Json`
#    Windows PowerShell（5.1）在未显式指定编码时按系统 ANSI 代码页解码文件，
#    而清单里有中文 —— JSON 会在中文处被解坏并抛
#    `ConvertFrom-Json: Invalid JSON primitive`，
#    **看起来像清单损坏，其实文件完全正常**（本项目为此白排查过一轮）。
#
# ⚠️ 陷阱 2：不要用 System.Text.Json
#    它在 Windows PowerShell 5.1 里没有，而 release.cmd 在没有 pwsh 的机器上
#    会回退到 5.1 —— 那样脚本会直接报 TypeNotFound。
#
# 所以：用 .NET **显式按 UTF-8** 读成字符串，再用正则提取顶层字段。
# 模组清单是扁平的键值结构，这样足够可靠；解析不到 id 会明确抛错。
function Read-Manifest {
    param([string]$Path)

    $text = [System.IO.File]::ReadAllText($Path, [System.Text.Encoding]::UTF8)
    $map = @{}

    foreach ($field in @('id', 'name', 'author', 'version')) {
        # 匹配 "field" : "value"，值内允许转义序列
        $m = [regex]::Match($text, '"' + $field + '"\s*:\s*"((?:[^"\\]|\\.)*)"')
        if (-not $m.Success) { continue }

        $value = $m.Groups[1].Value
        $value = [regex]::Replace($value, '\\u([0-9a-fA-F]{4})', {
                param($mm) [char][Convert]::ToInt32($mm.Groups[1].Value, 16)
            })
        $value = $value.Replace('\"', '"').Replace('\\', '\')
        $map[$field] = $value
    }

    if (-not $map.ContainsKey('id')) {
        throw "无法从 $Path 解析出 id —— 清单格式可能已变动，请检查它是否为合法 JSON。"
    }

    return $map
}

# ── 读取版本号（以模组清单为准，单一真源）────────────────────────────
$manifestPath = Join-Path $projectDir "$modName.json"
if (-not (Test-Path -LiteralPath $manifestPath)) {
    throw "找不到模组清单：$manifestPath"
}

$manifestJson = Read-Manifest -Path $manifestPath
$version = $manifestJson['version']

Write-Host "模组      : $($manifestJson['name'])  ($modName)" -ForegroundColor Cyan
Write-Host "版本      : $version" -ForegroundColor Cyan
Write-Host "作者      : $($manifestJson['author'])" -ForegroundColor Cyan
Write-Host ''

# ── 收集本模组产物 ────────────────────────────────────────────────
# dist\ 是 deploy.ps1 归档的可分发副本（刻意保留在版本控制里，
# 这样即使模组从游戏卸载，也能不重新编译就装回去）。
$distMod = Join-Path $projectDir "dist\$modName"
if (-not (Test-Path -LiteralPath $distMod)) {
    throw "找不到 dist\$modName —— 请先运行 .\deploy.cmd 生成产物。"
}

$modFiles = [ordered]@{}
# .pdb 一起发布是刻意的：运行时异常会带文件名与行号，排查速度差一个数量级
foreach ($name in @("$modName.dll", "$modName.json", "$modName.pck", "$modName.pdb")) {
    $src = Join-Path $distMod $name
    if (Test-Path -LiteralPath $src) {
        $modFiles[$name] = $src
    }
    elseif ($name -like '*.pdb') {
        Write-Host "  · 未找到 $name（没有调试符号也能运行，只是异常缺少行号）" -ForegroundColor Yellow
    }
    else {
        throw "缺少必需文件 $name（在 $distMod）—— 请重新运行 .\deploy.cmd"
    }
}

# ── 收集前置模组 BaseLib ──────────────────────────────────────────
# 包结构：Content\ 放清单与 .pck，lib\net9.0\ 放真正的 .dll —— 两处都要取。
$baseLibFiles = [ordered]@{}
$pkgContent = Join-Path $projectDir "packages\alchyr.sts2.baselib\$baseLibVersion\Content"
$pkgLib = Join-Path $projectDir "packages\alchyr.sts2.baselib\$baseLibVersion\lib\net9.0"

if ((Test-Path -LiteralPath $pkgContent) -and (Test-Path -LiteralPath $pkgLib)) {
    foreach ($pair in @(
            @('BaseLib.dll', (Join-Path $pkgLib 'BaseLib.dll')),
            @('BaseLib.json', (Join-Path $pkgContent 'BaseLib.json')),
            @('BaseLib.pck', (Join-Path $pkgContent 'BaseLib.pck')))) {
        if (Test-Path -LiteralPath $pair[1]) { $baseLibFiles[$pair[0]] = $pair[1] }
    }
}

$hasBaseLib = ($baseLibFiles.Count -eq 3)
if (-not $hasBaseLib) {
    Write-Host "  警告：工程内置的 BaseLib $baseLibVersion 不完整，" -ForegroundColor Yellow
    Write-Host '        本次只生成不带前置的那个发布包。' -ForegroundColor Yellow
}

# ── 打包辅助 ─────────────────────────────────────────────────────
function New-ReleasePackage {
    param(
        [string]$ZipPath,
        [string]$ReadmeText,
        [hashtable]$ExtraModFiles,
        [string]$ExtraModName
    )

    $stageDir = Join-Path $releaseDir ("_stage_" + [System.IO.Path]::GetFileNameWithoutExtension($ZipPath))
    if (Test-Path -LiteralPath $stageDir) { Remove-Item -LiteralPath $stageDir -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $stageDir | Out-Null

    $stagedMods = Join-Path $stageDir 'mods'
    New-Item -ItemType Directory -Force -Path $stagedMods | Out-Null

    # 本模组
    $dstMod = Join-Path $stagedMods $modName
    New-Item -ItemType Directory -Force -Path $dstMod | Out-Null
    foreach ($k in $modFiles.Keys) {
        Copy-Item -LiteralPath $modFiles[$k] -Destination (Join-Path $dstMod $k) -Force
    }

    # 可选的前置模组
    if ($ExtraModFiles -and $ExtraModName) {
        $dstExtra = Join-Path $stagedMods $ExtraModName
        New-Item -ItemType Directory -Force -Path $dstExtra | Out-Null
        foreach ($k in $ExtraModFiles.Keys) {
            Copy-Item -LiteralPath $ExtraModFiles[$k] -Destination (Join-Path $dstExtra $k) -Force
        }
    }

    # 说明文件（UTF-8 with BOM，记事本等编辑器兼容性最好）
    $readmePath = Join-Path $stageDir '安装说明.txt'
    [System.IO.File]::WriteAllText($readmePath, $ReadmeText, (New-Object System.Text.UTF8Encoding($true)))

    # ── 打包前的结构断言 ──
    # 文件夹名必须等于清单里的 id，否则游戏找不到，而且**完全静默**
    foreach ($folder in (Get-ChildItem -LiteralPath $stagedMods -Directory)) {
        $mf = Join-Path $folder.FullName "$($folder.Name).json"
        if (-not (Test-Path -LiteralPath $mf)) {
            throw "[$([System.IO.Path]::GetFileName($ZipPath))] mods\$($folder.Name)\ 下缺少 $($folder.Name).json"
        }
        $id = (Read-Manifest -Path $mf)['id']
        if ($id -ne $folder.Name) {
            throw "[$([System.IO.Path]::GetFileName($ZipPath))] 文件夹名 '$($folder.Name)' 与清单 id '$id' 不一致 —— 游戏会静默失效。"
        }
    }

    if (Test-Path -LiteralPath $ZipPath) { Remove-Item -LiteralPath $ZipPath -Force }
    Compress-Archive -Path (Join-Path $stageDir '*') -DestinationPath $ZipPath -CompressionLevel Optimal
    Remove-Item -LiteralPath $stageDir -Recurse -Force

    return Get-Item -LiteralPath $ZipPath
}

# ── 说明文件 ─────────────────────────────────────────────────────
$header = @"
$($manifestJson.name) $version
作者：$($manifestJson.author)

============================================================
 安装方法
============================================================

1. 找到《杀戮尖塔 2》的安装目录。

   在 Steam 里右键游戏 → 管理 → 浏览本地文件，
   即可打开安装目录（通常形如
   ...\steamapps\common\Slay the Spire 2）。

2. 把本压缩包里的 mods 文件夹，解压/拖进上面那个安装目录。
"@

$footer = @"

============================================================
 常见问题
============================================================

Q: 进阶面板最高只能选到 10？
A: BaseLib 没启用。到 设置 → 模组设置 把它打开，然后**完全退出并重启游戏**。
   模组只在游戏启动时加载，返回主菜单是不够的。

Q: 界面上的进阶标题显示成 "LEVEL_11.title" 这样的原文？
A: 说明本地化文件没被读到。请确认 BetterAscension.pck 确实和 .dll 放在同一个
   BetterAscension 文件夹里。

Q: 模组没有生效／游戏里看不到？
A: 请检查三件事：
   ① mods 文件夹里各模组文件夹的**名字**是否与上面第 2 步完全一致
      （游戏按文件夹名找清单，名字错了会静默失效）；
   ② 设置 → 模组设置 里是否已启用；
   ③ 是否完全重启过游戏。

Q: 想看日志排查问题？
A: 日志在 %APPDATA%\SlayTheSpire2\logs\godot.log
   搜 [BetterAscension] 即可筛出本模组的输出。
"@

$readmeWithBaseLib = $header + @"

   解压后应当是这样的结构：

       Slay the Spire 2\
         mods\
           BetterAscension\
             BetterAscension.dll
             BetterAscension.json
             BetterAscension.pck
           BaseLib\
             BaseLib.dll
             BaseLib.json
             BaseLib.pck

   · 如果游戏目录里已经有 mods 文件夹，直接合并即可
     （系统提示"是否合并/替换"时选合并）。
   · 本包**已附带**前置模组 BaseLib，无需另外下载。
   · 但如果你已经在 Steam 创意工坊订阅过 BaseLib，
     请改用「$modName-$version.zip」那个**不带前置**的包 ——
     两份 BaseLib 同时存在会让游戏禁用创意工坊版本，作者更新后就不生效了。

3. 启动游戏，进入 设置 → 模组设置，
   确认 BetterAscension 与 BaseLib 都处于**启用**状态。

4. 开始游戏时在进阶面板上把进阶等级调到 11 以上即可。
"@ + $footer

$readmeSolo = $header + @"

   解压后应当是这样的结构：

       Slay the Spire 2\
         mods\
           BetterAscension\
             BetterAscension.dll
             BetterAscension.json
             BetterAscension.pck

   · 如果游戏目录里已经有 mods 文件夹，直接合并即可
     （系统提示"是否合并/替换"时选合并）。

   ★ 本包**不含**前置模组 BaseLib，请确认你已经装好它：

       · 从 Steam 创意工坊订阅 BaseLib（推荐，作者更新后会自动升级）；或
       · 如果你还没有 BaseLib，请改用「$modName-$version+$baseLibName.zip」
         那个带前置的包。

3. 启动游戏，进入 设置 → 模组设置，
   确认 BetterAscension 与 BaseLib 都处于**启用**状态。

4. 开始游戏时在进阶面板上把进阶等级调到 11 以上即可。
"@ + $footer

# ── 生成两个包 ───────────────────────────────────────────────────
$releaseDir = Join-Path $projectDir 'release'
New-Item -ItemType Directory -Force -Path $releaseDir | Out-Null

Write-Host '打包中……' -ForegroundColor Cyan

$zipSolo = Join-Path $releaseDir "$modName-$version.zip"
$itemSolo = New-ReleasePackage -ZipPath $zipSolo -ReadmeText $readmeSolo

$itemFull = $null
if ($hasBaseLib) {
    $zipFull = Join-Path $releaseDir "$modName-$version+$baseLibName.zip"
    $extra = @{}
    foreach ($k in $baseLibFiles.Keys) { $extra[$k] = $baseLibFiles[$k] }
    $itemFull = New-ReleasePackage -ZipPath $zipFull -ReadmeText $readmeWithBaseLib `
        -ExtraModFiles $extra -ExtraModName $baseLibName
}

# ── 报告 ─────────────────────────────────────────────────────────
function Write-PackageReport($item, [string]$hint) {
    $hash = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash
    Write-Host ''
    Write-Host "  $($item.Name)" -ForegroundColor Green
    Write-Host "    大小   : $([math]::Round($item.Length / 1KB, 1)) KB"
    Write-Host "    SHA256 : $hash"
    Write-Host "    用途   : $hint" -ForegroundColor DarkGray
    return $hash
}

Write-Host ''
Write-Host '════════════════════════════════════════════════════════' -ForegroundColor Cyan
Write-Host ' 发布包已生成' -ForegroundColor Green
Write-Host '════════════════════════════════════════════════════════' -ForegroundColor Cyan

Write-PackageReport $itemSolo '给已经装好 BaseLib 的玩家' | Out-Null
if ($itemFull) { Write-PackageReport $itemFull '给还没有 BaseLib 的玩家（含前置）' | Out-Null }

Write-Host ''
Write-Host '上传 GitHub Releases 时：' -ForegroundColor Cyan
Write-Host "  · Tag 建议：$version（与模组清单一致）"
Write-Host '  · 两个 zip 都作为附件上传，并在说明里写清各自用途：'
Write-Host "      已经装好 BaseLib  →  $($itemSolo.Name)"
if ($itemFull) { Write-Host "      还没有 BaseLib    →  $($itemFull.Name)" }
Write-Host '  · 把两个 SHA256 贴进 Release 说明，方便玩家校验'
