<#
.SYNOPSIS
    编译 CY's BetterAscension，打包 .pck，并安装到游戏的 mods 目录。

.DESCRIPTION
    游戏在运行期间会一直持有 BetterAscension.dll 的句柄，所以运行中部署会报
    "file is locked by SlayTheSpire2.exe"。本脚本会先检查并（询问后）关闭游戏。

    必须用 publish（而不是 build）才会重新生成 .pck —— 本地化文本就在 .pck 里，
    只 build 的话界面上会一直显示 LEVEL_11.title 原文。

    本脚本还会：
      · 检测 BaseLib 是否已由 Steam 工坊提供；**有就不装本地副本**
        （装本地副本会与工坊版重复，游戏会警告并禁用工坊版，导致作者更新后不生效）
      · 断言 .pck 里确实含预期的本地化路径（防"目录名写错"这类静默失败）

.EXAMPLE
    pwsh -File .\deploy.ps1
    pwsh -File .\deploy.ps1 -Force        # 不询问，直接关游戏
    pwsh -File .\deploy.ps1 -SkipPck      # 只改了 .cs，跳过 Godot 打包
    pwsh -File .\deploy.ps1 -SkipBuild    # 用已有产物，只做安装与断言
#>
[CmdletBinding()]
param(
    [switch]$Force,
    [switch]$SkipPck,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'

$projectDir = $PSScriptRoot
$modName = 'BetterAscension'
$baseLibVersion = '3.4.7'

# ── 定位游戏 ──────────────────────────────────────────────────────
$gameCandidates = @(
    'C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2',
    'D:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2',
    'D:\Steam\steamapps\common\Slay the Spire 2',
    'E:\Steam\steamapps\common\Slay the Spire 2'
)
$gameDir = $gameCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $gameDir) {
    throw "找不到杀戮尖塔 2 的安装目录。请编辑本脚本顶部的 `$gameCandidates。"
}

$modsRoot = Join-Path $gameDir 'mods'
$modDir = Join-Path $modsRoot $modName

Write-Host "游戏目录 : $gameDir" -ForegroundColor Cyan
Write-Host "模组目录 : $modDir" -ForegroundColor Cyan
Write-Host ''

# ── 1. 确认游戏没有占用 DLL ────────────────────────────────────────
$game = Get-Process -Name 'SlayTheSpire2' -ErrorAction SilentlyContinue
if ($game) {
    if (-not $Force) {
        Write-Host "杀戮尖塔 2 正在运行（pid $($game.Id)），它锁住了 $modName.dll。" -ForegroundColor Yellow
        $answer = Read-Host '现在关闭它吗？[y/N]'
        if ($answer -notmatch '^[Yy]') { throw '已中止。请关闭游戏后重新运行。' }
    }
    Write-Host '正在关闭杀戮尖塔 2……'
    $game | Stop-Process -Force
    Start-Sleep -Seconds 2
}

# ── 2. 编译 + 打包 ────────────────────────────────────────────────
if (-not $SkipBuild) {
    $target = if ($SkipPck) { 'build' } else { 'publish' }
    Write-Host "执行 dotnet $target -c Debug ……"
    & dotnet $target (Join-Path $projectDir "$modName.csproj") -c Debug -v m --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet $target 失败。" }
}

# ── 3. BaseLib：优先用 Steam 工坊的，只有确实没有时才装本地副本 ──────
#
# ⚠️ 为什么要先查工坊
# ------------------
# 最初这一版**无条件**往 mods\BaseLib\ 装一份，结果与玩家已订阅的工坊版重复：
#
#   [WARN] Mod with ID BaseLib and version v3.4.7 is loaded both via Steam
#          and local mods directory. Disabling the Steam workshop version.
#
# 两份内容相同（都是 3.4.7）所以功能上没坏，但会：
#   · 在模组设置里出现两个 BaseLib
#   · 让工坊版被禁用 —— 作者更新后不会自动生效
#   · 日志里多一条没必要的警告
#
# 工坊才是正确来源（会随作者更新自动升级），所以**先检测、能不用就不用**。
# 工坊目录与游戏目录同级：<steamapps>\workshop\content\<appid>。
# 从 $gameDir（.../steamapps/common/Slay the Spire 2）往上两级回到 steamapps。
$steamApps = Split-Path (Split-Path $gameDir -Parent) -Parent
$workshopRoot = Join-Path $steamApps 'workshop\content'
$workshopBaseLib = if (Test-Path -LiteralPath $workshopRoot) {
    Get-ChildItem -LiteralPath $workshopRoot -Recurse -Depth 3 -Filter 'BaseLib.json' -File -ErrorAction SilentlyContinue |
        Select-Object -First 1
} else { $null }

if ($workshopBaseLib) {
    Write-Host "BaseLib：已由 Steam 工坊提供，跳过本地安装（工坊版会随作者更新自动升级）。" -ForegroundColor Green
    Write-Host "        $($workshopBaseLib.FullName)" -ForegroundColor DarkGray

    # 若之前装过本地副本，提示清理（不自动删 —— 那是玩家目录里的东西）
    $staleLocal = Join-Path $modsRoot 'BaseLib'
    if (Test-Path -LiteralPath $staleLocal) {
        Write-Host "  提示：mods\BaseLib\ 仍存在，会与工坊版重复。建议删除：" -ForegroundColor Yellow
        Write-Host "        Remove-Item -Recurse -Force `"$staleLocal`"" -ForegroundColor Yellow
    }
}
else {
    # 工坊没有 → 才从工程内置包安装（换机器/离线时的兜底）
    # 包结构：Content\ 放清单与 .pck，lib\net9.0\ 放真正的 .dll —— 两处都要取。
    $baseLibPkg = Join-Path $projectDir "packages\alchyr.sts2.baselib\$baseLibVersion"
    $baseLibContent = Join-Path $baseLibPkg 'Content'
    $baseLibLib = Join-Path $baseLibPkg 'lib\net9.0'

    if ((Test-Path -LiteralPath $baseLibContent) -and (Test-Path -LiteralPath $baseLibLib)) {
        $baseLibDst = Join-Path $modsRoot 'BaseLib'
        New-Item -ItemType Directory -Force -Path $baseLibDst | Out-Null

        foreach ($name in @('BaseLib.dll', 'BaseLib.json', 'BaseLib.pck')) {
            $src = if ($name -eq 'BaseLib.dll') { Join-Path $baseLibLib $name } else { Join-Path $baseLibContent $name }
            if (Test-Path -LiteralPath $src) {
                Copy-Item -LiteralPath $src -Destination $baseLibDst -Force
            }
            else {
                Write-Host "  警告：BaseLib 包里缺少 $name（$src）" -ForegroundColor Yellow
            }
        }

        Write-Host "BaseLib：工坊未找到，已从工程内置包安装 $baseLibVersion → $baseLibDst" -ForegroundColor Green
    }
    else {
        Write-Host "警告：工坊与工程内置包都没有 BaseLib $baseLibVersion。" -ForegroundColor Yellow
        Write-Host "      模组会因缺少前置依赖而无法加载。请订阅 BaseLib 或检查 $baseLibPkg" -ForegroundColor Yellow
    }
}

# ── 4. 确认产物确实落到了 mods 目录 ────────────────────────────────
if (-not (Test-Path -LiteralPath $modDir)) {
    throw "编译后仍找不到 $modDir。请检查 ${modName}.csproj 里的 ModsPath / Sts2Path 设置。"
}

Write-Host ''
Write-Host "已安装到 $modDir" -ForegroundColor Green
Get-ChildItem -LiteralPath $modDir | Select-Object Name, Length, LastWriteTime | Format-Table -AutoSize

# ── 5. 在工作区留一份可分发副本（卸载后能一键装回）────────────────
$distDir = Join-Path $projectDir "dist\$modName"
New-Item -ItemType Directory -Force -Path $distDir | Out-Null
Get-ChildItem -LiteralPath $modDir -File | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $distDir -Force
}
Write-Host "已归档一份可分发副本到 $distDir" -ForegroundColor Green

# ── 6. 断言 .pck 内容（防"资源目录名写错"这类静默失败）─────────────
if (-not $SkipPck) {
    $pck = Join-Path $distDir "$modName.pck"
    if (-not (Test-Path -LiteralPath $pck)) {
        throw "$modName.pck 没有生成 —— 检查 ${modName}.csproj 的 GodotPublish 目标与 local.props 里的 <GodotPath>。"
    }

    # .pck 里的路径字符串是明文，直接搜即可。虽然粗，但非常有效。
    $text = [System.Text.Encoding]::UTF8.GetString([System.IO.File]::ReadAllBytes($pck))
    $expected = @(
        "$modName/localization/eng/ascension.json",
        "$modName/localization/zhs/ascension.json"
    )
    foreach ($path in $expected) {
        if ($text.Contains($path)) {
            Write-Host "  OK  pck 含 $path" -ForegroundColor Green
        }
        else {
            throw "pck 缺少 $path —— 检查 ${modName}.csproj 的 <ResourcesDir> 是否等于模组 id（$modName）。"
        }
    }

    # 反向断言：包体不能混入工作区里那些不该发布的目录。
    #
    # 起因是一个真实 bug：export_filter 原本是 "all_resources"，它的语义是
    # "导出项目里所有资源"，而 exclude_filter 只影响资源导入扫描、**不阻止打包** ——
    # 结果 dist\BetterAscension\BetterAscension.json 被裹进了 .pck。
    # 功能不受影响，但包体不干净且极难察觉，所以让它显式失败。
    #
    # ⚠️ 只查 res://dist/ —— 这是"把模组自己又打了一遍"的特征，且不会误报。
    #    不查 res://tools/ 与 res://packages/：Godot 会把 .godot/global_script_class_cache.cfg
    #    一并打进包，而那份**元数据文件的内容里**就含有 res://tools/... 之类的路径字符串。
    #    它只有约 1 KB、不影响模组加载，属已知的无害项（见 docs\本机环境备忘.md）。
    $ascii = [System.Text.Encoding]::ASCII.GetString([System.IO.File]::ReadAllBytes($pck))
    if ($ascii.Contains('res://dist/')) {
        throw "pck 里混入了 res://dist/ —— 说明 export_filter 又变回 all_resources 了。`n"
            + '把它改成 export_filter="resources"（exclude_filter 不阻止打包）。'
    }
    Write-Host '  OK  未混入 dist/（没有把模组自己再打一遍）' -ForegroundColor Green
}

# ── 7. 提醒跑 IL 校验器 ───────────────────────────────────────────
Write-Host ''
Write-Host '提示：改过 Transpiler 后请跑一次 IL 校验器（几秒钟，比进游戏试快得多）：' -ForegroundColor Cyan
Write-Host '      dotnet run --project tools\TranspilerCheck -c Release' -ForegroundColor Cyan
Write-Host ''
Write-Host '完成。启动游戏后到 %APPDATA%\SlayTheSpire2\logs\godot.log 搜 [BetterAscension]。' -ForegroundColor Green
