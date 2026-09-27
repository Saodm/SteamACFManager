# 搭建用于验证「缺少 ACF」判定的合成 Steam 环境（placeholder 文件用 SetLength 创建，不实际占盘）
$ErrorActionPreference = 'Stop'
$rootA = 'D:\SteamACFManager\tests\steamrootA'
$rootB = 'D:\SteamACFManager\tests\steamrootB'
foreach ($d in @($rootA, $rootB)) { if (Test-Path $d) { Remove-Item $d -Recurse -Force } }
New-Item -ItemType Directory -Force -Path "$rootA\steamapps\common", "$rootA\appcache", "$rootA\userdata\12345678", "$rootB\steamapps" | Out-Null
New-Item -ItemType File -Force -Path "$rootA\steam.exe" | Out-Null

Copy-Item 'd:\steam\appcache\appinfo.vdf' "$rootA\appcache\appinfo.vdf" -Force
# 库B 的 Stardew Valley ACF 由夹具自己写（不复制真实机器上的文件）：
# 真实机器上的 ACF 会被工具本身改写（SizeOnDisk 变成实测值），复制过来会让「A 库是完整重复副本」这个前提失效。
@'
"AppState"
{
	"appid"		"413150"
	"Universe"		"1"
	"name"		"Stardew Valley"
	"StateFlags"		"4"
	"installdir"		"Stardew Valley"
	"SizeOnDisk"		"691846347"
	"StagingSize"		"0"
	"buildid"		"16826371"
	"UpdateResult"		"0"
	"InstalledDepots"
	{
		"413151"
		{
			"manifest"		"1"
			"size"		"691846347"
		}
	}
}
'@ | Set-Content -Path "$rootB\steamapps\appmanifest_413150.acf" -Encoding UTF8

# 本地证据：content_log 的「最近一次完成更新」记录 + depotcache 里对应的 manifest
#   - 4001890：记录的 buildid/gid 与 appinfo 的 public buildid 相同、但 manifest gid 不同（真实机器上的情形）
#   - 322330 ：记录的 buildid/gid 都与 appinfo 的 public 不同 → 用于证明 content_log 优先于 public
New-Item -ItemType Directory -Force -Path "$rootA\logs", "$rootA\depotcache" | Out-Null
@'
[2026-09-27 08:05:05] AppID 4001890 finished update, 1 mounted depots (BuildID 25127368) : 4001891 (8322781817822782512),
[2026-09-27 08:06:10] AppID 322330 finished update, 1 mounted depots (BuildID 24700693) : 322331 (998877),
'@ | Set-Content -Path "$rootA\logs\content_log.txt" -Encoding UTF8
foreach ($m in @('4001891_8322781817822782512', '322331_998877')) {
  [System.IO.File]::WriteAllBytes((Join-Path $rootA "depotcache\$m.manifest"), (New-Object byte[] 16))
}

function New-BigFile([string]$path, [long]$size) {
  $dir = Split-Path $path -Parent
  if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
  $fs = [System.IO.File]::Create($path)
  $fs.SetLength($size)
  $fs.Close()
}

# 1) 真·缺 ACF：文件完整（体积≈Steam 缓存里 How to Fish 的 depot 体积 632,945,138），任何库里都没有它的 ACF
New-BigFile "$rootA\steamapps\common\How to Fish\How to Fish.exe" 1704704
New-BigFile "$rootA\steamapps\common\How to Fish\data.bundle" 637569886   # 合计 = 639,274,590（比 depots 多 ~1%，模拟额外文件）

# 2) 需修复：文件完整但 ACF 是坏的（StateFlags=6 / SizeOnDisk=0 / buildid=0）
New-BigFile (Join-Path $rootA "steamapps\common\Don't Starve Together\data.bundle") 4417468445
@'
"AppState"
{
	"appid"		"322330"
	"Universe"		"1"
	"LauncherPath"		"D:\\SteamACFManager\\tests\\steamrootA\\steam.exe"
	"name"		"Don't Starve Together"
	"StateFlags"		"6"
	"installdir"		"Don't Starve Together"
	"SizeOnDisk"		"0"
	"StagingSize"		"0"
	"buildid"		"0"
	"LastOwner"		"76561197972611406"
	"UpdateResult"		"0"
	"InstalledDepots"
	{
		"322331"
		{
			"manifest"		"1"
			"size"		"0"
		}
	}
	"UserConfig"
	{
		"language"		"schinese"
	}
	"MountedConfig"
	{
	}
}
'@ | Set-Content -Path "$rootA\steamapps\appmanifest_322330.acf" -Encoding UTF8

# 3) 文件完整、但同一个 AppID 已在另一个库安装（库B里有 413150 的 ACF）→ 不应再生成第二份
New-BigFile "$rootA\steamapps\common\Stardew Valley\Stardew Valley.exe" 691846347

# 4) 残留：只剩崩溃转储
New-Item -ItemType Directory -Force -Path "$rootA\steamapps\common\Counter-Strike Global Offensive\game\bin\win64" | Out-Null
New-BigFile "$rootA\steamapps\common\Counter-Strike Global Offensive\game\bin\win64\cs2_error.mdmp" 8234016

# 5) 空文件夹
New-Item -ItemType Directory -Force -Path "$rootA\steamapps\common\Black Myth Wukong Benchmark Tool" | Out-Null

# 6) 无法确定 AppID、且只有 40 字节
New-Item -ItemType Directory -Force -Path "$rootA\steamapps\common\Leftover Junk" | Out-Null
'settings.dat' | Set-Content -Path "$rootA\steamapps\common\Leftover Junk\settings.dat" -Encoding ASCII
$fs = [System.IO.File]::Create("$rootA\steamapps\common\Leftover Junk\settings.dat"); $fs.SetLength(40); $fs.Close()

# 7) 残留：AppID 已知但文件远不完整（Tomb Raider 的本体 depot 体积未知，只能按已知下界判）
New-BigFile "$rootA\steamapps\common\Tomb Raider\ChinesePatch.zip" 2861634395

# 8) AppID 已知、体积远低于本体 depot（Subnautica 只剩存档）
$sub = Join-Path $rootA 'steamapps\common\Subnautica\SNAppData\SavedGames'
New-Item -ItemType Directory -Force -Path $sub | Out-Null
$fs = [System.IO.File]::Create((Join-Path $sub 'options.bin')); $fs.SetLength(2723); $fs.Close()

@"
"libraryfolders"
{
	"0"
	{
		"path"		"$($rootA -replace '\\','\\')"
		"label"		""
		"apps"
		{
		}
	}
	"1"
	{
		"path"		"$($rootB -replace '\\','\\')"
		"label"		""
		"apps"
		{
			"413150"		"691846347"
		}
	}
}
"@ | Set-Content -Path "$rootA\steamapps\libraryfolders.vdf" -Encoding ASCII

"test root built:"
$expected = @('How to Fish', "Don't Starve Together", 'Stardew Valley', 'Counter-Strike Global Offensive',
              'Black Myth Wukong Benchmark Tool', 'Leftover Junk', 'Tomb Raider', 'Subnautica')
$found = Get-ChildItem (Join-Path $rootA 'steamapps\common') | Select-Object -ExpandProperty Name
foreach ($name in $expected) {
  if ($found -notcontains $name) { Write-Error "缺少测试目录: $name" }
}
Get-ChildItem "$rootA\steamapps\common" | ForEach-Object {
  $files = Get-ChildItem -File -Recurse -Force $_.FullName -ErrorAction SilentlyContinue
  $sum = ($files | Measure-Object Length -Sum).Sum
  if ($null -eq $sum) { $sum = 0 }
  "{0,-38} {1,5} files  {2,15:N0} bytes" -f $_.Name, $files.Count, $sum
}
Get-ChildItem "$rootA\steamapps\*.acf", "$rootB\steamapps\*.acf" | ForEach-Object { "acf: " + $_.FullName }
