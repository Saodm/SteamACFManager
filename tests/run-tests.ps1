# ===================================================================
#  Steam ACF 管理器 - 回归测试
#  在 tests\steamrootA / steamrootB 里搭一个合成 Steam 环境，
#  验证「缺少 ACF」的判定与生成逻辑（不碰真实 Steam 库）。
#  用法: tests\run-tests.cmd  或  powershell -ExecutionPolicy Bypass -File tests\run-tests.ps1
# ===================================================================
$ErrorActionPreference = 'Stop'
$gbk = [System.Text.Encoding]::GetEncoding(936)
[Console]::OutputEncoding = $gbk          # 让子进程（GBK 控制台程序）的输出能被正确解码

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $here
$exe = Join-Path $root 'SteamACFManager.exe'
$cli = Join-Path $root 'SteamACFManagerCLI.exe'
$testRoot = Join-Path $here 'steamrootA'
$outDir = Join-Path $here 'out'

$script:pass = 0
$script:fail = 0
function Check([string]$name, [bool]$ok, [string]$detail) {
  if ($ok) { $script:pass++; Write-Host ("  [通过] " + $name) -ForegroundColor Green }
  else { $script:fail++; Write-Host ("  [失败] " + $name + "  -> " + $detail) -ForegroundColor Red }
}
function Decode-Output([byte[]]$bytes) {
  # 工具在“当前控制台代码页表示不了”的语言下会输出 UTF-8，否则是 GBK；这里先试 UTF-8
  $strict = New-Object System.Text.UTF8Encoding($false, $true)
  try { return $strict.GetString($bytes) } catch { return [System.Text.Encoding]::GetEncoding(936).GetString($bytes) }
}

function RunExe([string]$exePath, [string[]]$arguments, [string]$rootPath, [string]$lang = "zh-CN") {
  $env:STEAM_ACF_ROOT = $rootPath
  if ([string]::IsNullOrEmpty($lang)) { Remove-Item Env:\STEAM_ACF_LANG -ErrorAction SilentlyContinue }
  else { $env:STEAM_ACF_LANG = $lang }
  $env:STEAM_ACF_EXPORT_DIR = Join-Path $outDir 'export'   # 测试的导出副本不要污染真实 export 目录
  $tmp = Join-Path $outDir ('raw_' + [guid]::NewGuid().ToString('N') + '.txt')
  $argline = (($arguments | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }) -join ' ')
  cmd /c "`"$exePath`" $argline > `"$tmp`" 2>&1" | Out-Null
  if (-not (Test-Path $tmp)) { return "" }
  $bytes = [System.IO.File]::ReadAllBytes($tmp)
  Remove-Item $tmp -Force
  return (Decode-Output $bytes)
}

if (-not (Test-Path $exe)) { Write-Host "[错误] 找不到 $exe，请先运行 build.cmd" -ForegroundColor Red; exit 1 }
if (-not (Test-Path $cli)) { Write-Host "[错误] 找不到 $cli，请先运行 build-cli.cmd" -ForegroundColor Red; exit 1 }

Write-Host "=== 1. 重建合成 Steam 环境 ==="
& (Join-Path $here 'build-testroot.ps1') | Out-Null
if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

Write-Host ""
Write-Host "=== 2. 扫描（图形版 exe 的 scan 命令） ==="
$scan = RunExe $exe @('scan') $testRoot
$scan | Set-Content -Path (Join-Path $outDir 'scan.txt') -Encoding UTF8
Write-Host $scan

function Expect([string]$needle, [string]$status) {
  $line = ($scan -split "`r?`n" | Where-Object { $_ -like ("*" + $needle + "*") } | Select-Object -First 1)
  $ok = ($line -ne $null) -and ($line -like ("*" + $status + "*"))
  Check ("$needle -> $status") $ok ($line)
}

Write-Host "=== 3. 判定结果断言 ==="
Expect 'How to Fish' '缺 ACF'
Expect "Don't Starve Together" '需修复'
Expect 'Counter-Strike 2' '残留目录'
Expect 'Benchmark Tool' '空文件夹'
Expect 'Leftover Junk' '残留目录'
Expect 'Tomb Raider' '残留目录'
Expect 'Subnautica' '残留目录'
# Stardew Valley 出现在两个库里：B 库是正常安装，A 库是文件完整但重复的目录
$stardewA = ($scan -split "`r?`n" | Where-Object { $_ -like '*Stardew Valley*steamrootA*' } | Select-Object -First 1)
Check 'Stardew Valley(steamrootA) -> 未验证（重复安装）' ($stardewA -like '*未验证*') $stardewA

Write-Host ""
Write-Host "=== 4. 生成 ACF（真·缺 ACF 的场景） ==="
$gen = RunExe $exe @('generate', 'How to Fish') $testRoot
$gen | Set-Content -Path (Join-Path $outDir 'generate.txt') -Encoding UTF8
$acfHowToFish = Join-Path $testRoot 'steamapps\appmanifest_4001890.acf'
Check '生成命令成功' ($gen -match 'OK') ($gen -split "`r?`n" | Select-Object -First 3)
$acfText = Get-Content $acfHowToFish -Raw
Check 'buildid 取自 Steam 本地缓存 (25127368)' ($acfText -match '"buildid"\s+"25127368"') 'missing buildid'
Check 'depot manifest 为当前 public 版本' ($acfText -match '3889140805796509645') 'missing manifest'
Check 'StateFlags=4' ($acfText -match '"StateFlags"\s+"4"') 'missing StateFlags'
Check 'SizeOnDisk 为实测体积' ($acfText -match '"SizeOnDisk"\s+"639274590"') 'missing SizeOnDisk'
Check 'InstalledDepots 含 depot 4001891' ($acfText -match '"4001891"') 'missing depot'

Write-Host ""
Write-Host "=== 5. 重复生成应被拒绝（不覆盖已有 ACF） ==="
$hash1 = (Get-FileHash $acfHowToFish -Algorithm SHA256).Hash
$gen2 = RunExe $exe @('generate', 'How to Fish') $testRoot
$hash2 = (Get-FileHash $acfHowToFish -Algorithm SHA256).Hash
Check '已拒绝重复生成' ($gen2 -match '未找到该孤儿文件夹' -or $gen2 -match '拒绝生成') ($gen2.Trim())
Check '已有 ACF 未被改动' ($hash1 -eq $hash2) 'hash changed'

Write-Host ""
Write-Host "=== 6. 残留目录 / 空目录 / 重复安装 / 错 AppID 必须拒绝生成 ==="
$rej1 = RunExe $exe @('generate', 'Tomb Raider') $testRoot
Check '残留目录被拒绝' ($rej1 -match '未找到该孤儿文件夹' -or $rej1 -match '拒绝生成') ($rej1.Trim())
$rej2 = RunExe $exe @('generate', 'Stardew Valley') $testRoot
Check '重复安装被拒绝' ($rej2 -match '拒绝生成') ($rej2.Trim())
$rej3 = RunExe $exe @('generate', 'How to Fish', '730') $testRoot
Check '错误 AppID 被体积校验挡下' ($rej3 -match '拒绝生成' -or $rej3 -match '未找到') ($rej3.Trim())
Check '被拒绝后 How to Fish 的 ACF 仍在' (Test-Path $acfHowToFish) 'file missing'

Write-Host ""
Write-Host "=== 7. 修复损坏 ACF（保留磁盘上的 manifest，写入当前 buildid） ==="
$rep = RunExe $exe @('repair', '322330') $testRoot
$rep | Set-Content -Path (Join-Path $outDir 'repair.txt') -Encoding UTF8
$acfDst = Join-Path $testRoot 'steamapps\appmanifest_322330.acf'
$dstText = Get-Content $acfDst -Raw
Check '修复命令成功' ($rep -match 'OK') ($rep -split "`r?`n" | Select-Object -First 3)
Check 'StateFlags=4' ($dstText -match '"StateFlags"\s+"4"') 'missing StateFlags'
Check 'buildid=当前 public 版本 (24700692)' ($dstText -match '24700692') 'missing buildid'
Check 'manifest 已补全为有效值' ($dstText -match '3356367044060314490') 'missing manifest'
Check 'SharedDepots 带归属 AppID' ($dstText -match '"228982"\s+"228980"') 'missing shared owner'
Check '原 ACF 已备份为 .bak' (Test-Path ($acfDst + '.bak')) 'no backup'

Write-Host ""
Write-Host "=== 8. 图形版重新扫描：生成/修复后的条目应变成“已安装” ==="
$scan2 = RunExe $exe @('scan') $testRoot
$h1 = ($scan2 -split "`r?`n" | Where-Object { $_ -like '*How to Fish*' } | Select-Object -First 1)
$d1 = ($scan2 -split "`r?`n" | Where-Object { $_ -like "*Don't Starve Together*" } | Select-Object -First 1)
Check 'How to Fish 变为已安装' ($h1 -like '*已安装*') $h1
Check "Don't Starve Together 变为已安装" ($d1 -like '*已安装*') $d1

Write-Host ""
Write-Host "=== 9. 命令行版（共用 AcfCore 判定）结果应与图形版一致 ==="
$cliScan = RunExe $cli @('scan') $testRoot
$cliScan | Set-Content -Path (Join-Path $outDir 'scan-cli.txt') -Encoding UTF8
Check 'CLI 也把完整目录判为缺 ACF 或已安装' ((($cliScan -match '\[生成\] 缺 ACF') -or ($cliScan -match '\[ OK \] 已安装'))) 'no match'
Check 'CLI 能识别残留目录' ($cliScan -match '\[跳过\] 残留目录') 'no match'
Check 'CLI 能识别空文件夹' ($cliScan -match '\[跳过\] 空文件夹') 'no match'
Check 'CLI 不把已有 ACF 的目录当成孤儿' (($cliScan -split "`n" | Where-Object { $_ -like "*Don't Starve*" -and $_ -like '*[生成]*' }).Count -eq 0) 'duplicate orphan listed'

Write-Host ""
Write-Host "=== 9b. StateFlags 列带英文含义 + 多语言无缺译文 ==="
Check 'StateFlags 带含义（中文模式：已完整安装）' ($scan -match '\(已完整安装\)') 'no localised flags'
Check 'StateFlags 多位置位也可读（中文：需要更新 | 已完整安装）' ($scan -match '\(需要更新 \| 已完整安装\)') 'no multi-bit text'

# 默认语言必须是英文（不设置 STEAM_ACF_LANG；先把机器上已有的语言设置临时挪开，避免依赖用户环境）
$settingsFile = Join-Path $env:APPDATA 'SteamACFManager\settings.ini'
$settingsBackup = $settingsFile + '.test-backup'
$settingsMoved = $false
if (Test-Path $settingsFile) {
  if (Test-Path $settingsBackup) { Remove-Item $settingsBackup -Force }
  Move-Item $settingsFile $settingsBackup -Force
  $settingsMoved = $true
}
$scanEn = RunExe $exe @('scan') $testRoot ""
Check '默认语言为英文' (($scanEn -match 'Missing ACF|Installed') -and ($scanEn -notmatch '缺 ACF')) 'english default failed'
Check '英文下 StateFlags 用官方标识符' ($scanEn -match '\(FullyInstalled\)') 'no english flags'

# 逐语言真的切换一次：文案（状态名 + StateFlags 含义）都要跟着变，且无缺译文
$flagExpect = @{
  'en'    = '(FullyInstalled)'
  'zh-CN' = '(已完整安装)'
  'zh-TW' = '(已完整安裝)'
  'ja'    = '(完全インストール済み)'
  'ko'    = '(완전 설치됨)'
  'es'    = '(Instalado por completo)'
  'de'    = '(Vollständig installiert)'
  'ru'    = '(Полностью установлено)'
}
$settingsBefore = if (Test-Path $settingsFile) { Get-Content $settingsFile -Raw } else { '<none>' }
foreach ($code in @('zh-CN','zh-TW','ja','ko','es','de','ru')) {
  $scanL = RunExe $exe @('scan') $testRoot $code
  Check ("语言 $code 的 StateFlags 文案已本地化") ($scanL -match [regex]::Escape($flagExpect[$code])) (($scanL -split "`n" | Where-Object { $_ -match '\d+ \(' } | Select-Object -First 1))
  $stn = RunExe $exe @('gui-selftest') $testRoot $code
  $info = ($stn -split "`r?`n" | Where-Object { $_ -match '^i18n:' } | Select-Object -First 1)
  $missing = -1
  if ($info -match 'missing=(\d+)') { $missing = [int]$Matches[1] }
  Check ("语言 $code 无缺译文") ($missing -eq 0) ($info + "  (lang=" + $code + ")")
  Check ("语言 $code 的 i18n 行语言正确") ($info -match ("lang=" + [regex]::Escape($code) + " ")) $info
}
$settingsAfter = if (Test-Path $settingsFile) { Get-Content $settingsFile -Raw } else { '<none>' }
Check '启动/自检不会改写语言设置文件' ($settingsBefore -eq $settingsAfter) ("before=" + $settingsBefore.Trim() + " after=" + $settingsAfter.Trim())

$stBack = RunExe $exe @('gui-selftest') $testRoot 'zh-CN'
Check '切换回中文仍然正常' ($stBack -match 'MainForm 构建成功') $stBack.Trim()

Write-Host ""
Write-Host "=== 9c. 切换语言时说明列必须立即跟着变（不需要重启） ==="
$live1 = RunExe $exe @('gui-selftest') $testRoot 'en'
$line1 = ($live1 -split "`r?`n" | Where-Object { $_ -match 'live-lang:' } | Select-Object -First 1)
Check '英文→中文：说明列内容发生变化' ($line1 -match 'notesChangedRows=[1-9]') $line1
Check '英文→中文：StateFlags 列内容发生变化' ($line1 -match 'flagsChanged=True') $line1
Check '英文→中文：示例说明已变成中文' ($line1 -match 'sampleAfter="[^"]*[\u4e00-\u9fff]') $line1
$live2 = RunExe $exe @('gui-selftest') $testRoot 'zh-CN'
$line2 = ($live2 -split "`r?`n" | Where-Object { $_ -match 'live-lang:' } | Select-Object -First 1)
Check '中文→英文：示例说明已不含中文' ($line2 -match 'sampleAfter="' -and $line2 -notmatch 'sampleAfter="[^"]*[\u4e00-\u9fff]') $line2
Check '中文→英文：说明列内容发生变化' ($line2 -match 'notesChangedRows=[1-9]') $line2
if ($settingsMoved) { Move-Item $settingsBackup $settingsFile -Force }   # 还原用户原有的语言设置

Write-Host ""
Write-Host "=== 10. 界面自检（构建窗体并填表，不显示窗口） ==="
$st = RunExe $exe @('gui-selftest') $testRoot
Check '界面构建正常' ($st -match 'MainForm 构建成功') ($st.Trim())
Check '异步重新扫描（后台线程回填）正常' ($st -match '异步重新扫描完成') ($st.Trim())

Write-Host ""
Write-Host "==================================================="
Write-Host ("  通过 {0} 项，失败 {1} 项" -f $script:pass, $script:fail)
Write-Host "==================================================="
if ($script:fail -gt 0) { exit 1 }
exit 0
