# ===================================================================
#  第二场景测试流程：游戏文件完整，但 ACF 让 Steam 一直「需要更新 / 卡在验证」
#
#  用法（推荐用仓库根目录的 test-scenario2.cmd 调用）：
#     test-scenario2.cmd 413150              只读预演：看当前状态 + 工具准备怎么修（不写文件）
#     test-scenario2.cmd 413150 break        人工造出第二场景：只把 ACF 的账目改坏（游戏文件不动）
#     test-scenario2.cmd 413150 apply        真正写入修复（要求 Steam 已完全退出）
#     test-scenario2.cmd 413150 rollback     还原（优先用 .testbak，其次用工具生成的 .bak）
#
#  完整闭环：
#     break → 启动 Steam 看它变回「更新/验证」→ 退出 Steam → preview → apply
#           → 启动 Steam 看「开始游戏」→ 退出 Steam → rollback
#
#  这个脚本只改 appmanifest_<AppID>.acf 这一个文本文件，绝不碰游戏文件。
# ===================================================================
param(
  [Parameter(Mandatory=$true, Position=0)][int]$AppId,
  [Parameter(Position=1)][ValidateSet('preview','break','apply','rollback')][string]$Mode = 'preview'
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = [System.Text.Encoding]::GetEncoding(936) } catch { }
$gbk = [System.Text.Encoding]::GetEncoding(936)

$root = $PSScriptRoot
if (-not $root) { $root = Split-Path -Parent $MyInvocation.MyCommand.Path }
$exe  = Join-Path $root 'SteamACFManager.exe'
if (-not (Test-Path $exe)) { throw ("找不到 " + $exe) }

function Run-Tool([string[]]$arguments) {
  $tmp = Join-Path $env:TEMP ('acftest_' + [guid]::NewGuid().ToString('N') + '.txt')
  $argline = (($arguments | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }) -join ' ')
  cmd /c "`"$exe`" $argline > `"$tmp`" 2>&1" | Out-Null
  $text = ''
  if (Test-Path $tmp) { $text = [System.IO.File]::ReadAllText($tmp, $gbk); Remove-Item $tmp -Force }
  return $text
}
function Steam-Running { return (@(Get-Process -Name steam -ErrorAction SilentlyContinue).Count -gt 0) }
function Acf-Path-Of([string]$text) {
  $line = ($text -split "`r?`n" | Where-Object { $_ -match '^写入位置：' } | Select-Object -First 1)
  if (-not $line) { return '' }
  $m = [regex]::Match($line, '^写入位置：(.+?)（原文件备份为')
  if ($m.Success) { return $m.Groups[1].Value }
  return ''
}
function Show-Acf([string]$tag, [string]$path) {
  if (-not (Test-Path $path)) { "{0}: （文件不存在）" -f $tag; return }
  $t = [System.IO.File]::ReadAllText($path, [System.Text.Encoding]::UTF8)
  $sf  = [regex]::Match($t, '"StateFlags"\s+"([^"]*)"').Groups[1].Value
  $bid = [regex]::Match($t, '"buildid"\s+"([^"]*)"').Groups[1].Value
  $sod = [regex]::Match($t, '"SizeOnDisk"\s+"([^"]*)"').Groups[1].Value
  $g = @([regex]::Matches($t, '"manifest"\s+"(\d+)"') | ForEach-Object { $_.Groups[1].Value })
  "{0}: StateFlags={1}   buildid={2}   SizeOnDisk={3}" -f $tag, $sf, $bid, $sod
  "      manifest: {0}" -f ((($g | Select-Object -First 3) -join ', ') + $(if ($g.Count -gt 3) { " …（共 $($g.Count) 个）" } else { '' }))
}
function Set-Acf-Field([string]$text, [string]$key, [string]$value) {
  $pattern = '("' + $key + '"\s+")([^"]*)(")'
  if ([regex]::IsMatch($text, $pattern)) { return [regex]::Replace($text, $pattern, ('${1}' + $value + '${3}'), 1) }
  return $text
}

Write-Host "==================== 第二场景测试 ===================="
$fi = Get-Item $exe
$hasEvidence = [System.Text.Encoding]::UTF8.GetString([System.IO.File]::ReadAllBytes($exe)).Contains('DepotPlanner')
"工具: {0}" -f $exe
Write-Host ("      {0:N0} 字节 / 编译于 {1} / 含本机凭证逻辑(DepotPlanner): {2}" -f $fi.Length, $fi.LastWriteTime, $hasEvidence)
if (Steam-Running) {
  if ($env:STEAM_ACF_ROOT) {
    Write-Host "[提示] STEAM_ACF_ROOT 已设置（合成测试目录）→ 跳过 Steam 运行检查。" -ForegroundColor DarkGray
  } else {
    Write-Host "[停止] Steam 正在运行：它会在后台自己改写 ACF，测试必须在 Steam 完全退出后进行。" -ForegroundColor Yellow
    Write-Host "       请在托盘图标上右键 → 退出 Steam，然后重新执行本命令。" -ForegroundColor Yellow
    exit 3
  }
}
Write-Host ""

Write-Host "--- 1. 工具扫描这一条（只读） ---"
$scan = Run-Tool @('scan')
$row = ($scan -split "`r?`n" | Where-Object { $_ -match ("\s" + $AppId + "\s") } | Select-Object -First 1)
if ($row) { Write-Host $row.Trim() } else { Write-Host "（扫描结果里没有 $AppId：可能没装，或文件夹已不在）" -ForegroundColor Yellow }

Write-Host ""
Write-Host "--- 2. 修复预演（--dry-run，只读） ---"
$dry = Run-Tool @('repair', "$AppId", '--dry-run')
$acf = Acf-Path-Of $dry
if (-not $acf) {
  Write-Host (($dry -split "`r?`n" | Where-Object { $_ -match '^(失败|错误)' } | Select-Object -First 1)) -ForegroundColor Red
  Write-Host "（工具没有给出可写入的方案：文件不完整，或这不是孤儿目录 —— 属于另一个场景）" -ForegroundColor Yellow
  exit 4
}
$testBak = $acf + '.testbak'
Show-Acf '当前' $acf
Write-Host ("      ACF: " + $acf)
Write-Host ""

# ---------- 3) 按模式执行 ----------
if ($Mode -eq 'rollback') {
  $src = ''
  if (Test-Path $testBak) { $src = $testBak } elseif (Test-Path ($acf + '.bak')) { $src = $acf + '.bak' }
  if (-not $src) { Write-Host ("[停止] 找不到备份（" + $testBak + " 和 " + $acf + ".bak 都不存在）") -ForegroundColor Yellow; exit 5 }
  Copy-Item $src $acf -Force
  Write-Host ("--- 3. 已回滚（用 " + (Split-Path $src -Leaf) + " 覆盖 ACF） ---") -ForegroundColor Green
  Show-Acf '回滚后' $acf
  $sfNow = [regex]::Match([System.IO.File]::ReadAllText($acf, [System.Text.Encoding]::UTF8), '"StateFlags"\s+"([^"]*)"').Groups[1].Value
  if ($sfNow -eq '4') {
    Write-Host "回滚后是 4（可玩）：说明测试前这份 ACF 本来就是好的。想再看一次第二场景，跑 break 即可。"
  } else {
    Write-Host ("回滚后是 " + $sfNow + "：Steam 应该恢复成你改动这一条之前的样子。")
  }
  exit 0
}

if ($Mode -eq 'break') {
  if (-not (Test-Path $testBak)) { Copy-Item $acf $testBak -Force; Write-Host ("已备份原始 ACF → " + $testBak) }
  $t = [System.IO.File]::ReadAllText($acf, [System.Text.Encoding]::UTF8)
  $t = Set-Acf-Field $t 'StateFlags' '6'      # 6 = 需要更新 | 已完整安装
  $t = Set-Acf-Field $t 'buildid' '0'         # 模拟手动搬文件后账目失效
  $t = Set-Acf-Field $t 'SizeOnDisk' '0'
  $t = Set-Acf-Field $t 'StagingSize' '0'
  [System.IO.File]::WriteAllText($acf, $t, (New-Object System.Text.UTF8Encoding($false)))
  Write-Host "--- 3. 已造出第二场景（只改 ACF 账目，游戏文件一个字节都没动） ---" -ForegroundColor Green
  Show-Acf '改坏后' $acf
  Write-Host ""
  Write-Host "工具扫描到的状态（改坏后）：" -ForegroundColor DarkGray
  $scan2 = Run-Tool @('scan')
  $row2 = ($scan2 -split "`r?`n" | Where-Object { $_ -match ("\s" + $AppId + "\s") } | Select-Object -First 1)
  if ($row2) { Write-Host ("  " + $row2.Trim()) }
  Write-Host "工具修复方案（改坏后）：" -ForegroundColor DarkGray
  $dry2 = Run-Tool @('repair', "$AppId", '--dry-run')
  ($dry2 -split "`r?`n") | Where-Object { $_ -match '^(OK|失败)|内容校验|^SizeOnDisk=|^buildid=|^depot \d+：|^Steam |^提示：' } | ForEach-Object { "  " + $_.Trim() }
  Write-Host ""
  Write-Host "接下来：" -ForegroundColor Green
  Write-Host "  1) 启动 Steam → 这一条会变成「更新」/一点就进入验证（这就是第二场景的样子）；"
  Write-Host "  2) 完全退出 Steam；"
  Write-Host ("  3) test-scenario2.cmd {0} apply   ← 写入修复（想先看方案可跑 preview）" -f $AppId)
  Write-Host "  4) 启动 Steam → 应该显示「开始游戏」，点进去能进游戏；"
  Write-Host ("  5) 还原：test-scenario2.cmd {0} rollback" -f $AppId)
  exit 0
}

# preview / apply：打印工具给出的方案
($dry -split "`r?`n") | Where-Object { $_ -match '^(内容校验|SizeOnDisk=|depot 组合|buildid=|depot \d+：|Steam |提示：|depot 清单来源)' } | ForEach-Object { "  " + $_.Trim() }
Write-Host ""

if ($Mode -eq 'preview') {
  Write-Host "--- 3. 预演结束：没有写入任何文件 ---" -ForegroundColor Green
  Write-Host ("确认方案后执行：test-scenario2.cmd {0} apply" -f $AppId)
  Write-Host ("撤销：          test-scenario2.cmd {0} rollback" -f $AppId)
  exit 0
}

Write-Host "--- 3. 真正写入（repair） ---"
$rep = Run-Tool @('repair', "$AppId")
$repLines = $rep -split "`r?`n"
if (-not ($repLines | Where-Object { $_ -match '^OK' })) {
  Write-Host (($repLines | Where-Object { $_ -match '^(失败|错误)' } | Select-Object -First 1)) -ForegroundColor Red
  exit 6
}
Show-Acf '修复后' $acf
"备份: {0}  ({1})" -f ($acf + '.bak'), $(if (Test-Path ($acf + '.bak')) { '已生成，可回滚' } else { '未生成' })
Write-Host ""
Write-Host "接下来只能你手动做：" -ForegroundColor Green
Write-Host "  1) 启动 Steam → 看这一条是「开始游戏 / 已安装」，而不是「更新 / 正在验证文件」；"
Write-Host "  2) 点「开始游戏」，确认能进游戏；"
Write-Host "  3) 若 Steam 仍提示有更新，看上面报告最后一句结论："
Write-Host "     * 「Steam 不会提示更新」→ 账目与磁盘完全自洽，Steam 不该再动它；"
Write-Host "     * 「会提示可更新并只下载增量」→ public 分支确实有更新版，属正常更新，不是验证循环；"
Write-Host ("  4) 反向验证：退出 Steam → test-scenario2.cmd {0} rollback（还原成测试前）→ 再 test-scenario2.cmd {0} break 就能重现「更新」。" -f $AppId)
exit 0
