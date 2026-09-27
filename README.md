# Steam ACF Manager

A small Windows tool that scans, verifies, repairs and generates Steam's
`appmanifest_<AppID>.acf` files — **without ever inventing an ACF for a game that is not really installed.**

* GUI (WinForms) + command line in one executable
* 8 UI languages, **English by default** (switch it right next to the *Rescan* button)
* `StateFlags` is shown as a number **plus its English meaning**, e.g. `4 (FullyInstalled)`
* Runs fully offline: it reads Steam's own metadata cache (`appcache/appinfo.vdf`)

---

## 1. The problem it solves

Steam's ACF file is just an *installation record*. Many tools (and earlier versions of this one)
treated "a folder exists in `steamapps\common` but there is no ACF" as "the game is installed but
its ACF is missing" — and happily generated an ACF for it.

That is wrong. When Steam uninstalls a game it deletes the manifest-tracked files but leaves
everything that is *not* tracked behind, and that leftover can look like a game:

| Leftover type | Real example found on the dev machine |
|---|---|
| Empty folder | `Black Myth Wukong Benchmark Tool` — 0 files / 0 bytes |
| Crash dumps | `Counter-Strike Global Offensive` — 8 MB of `*.mdmp` |
| Saves / config | `Subnautica` — 2.7 KB `options.bin`; `ForzaHorizon4` — 18 bytes |
| Fan patches / mods | `Life Is Strange` — 15 MB patch installer |
| Telemetry / logs | `DEATH STRANDING DIRECTORS CUT` — 128 KB `telemetry.dat` |
| DLC patches the game downloaded itself | `SenrenBanka` — 2.4 GB of `adult*.xp3`, base game gone |

Generating an ACF for those makes Steam believe the game is installed and download the whole thing again.

**This tool only calls a folder "missing ACF" when both of these hold:**

1. the folder's **contents are verified complete** (measured size ≥ 90 % of the known full size), and
2. **no library has an ACF for that AppID** (otherwise it is a duplicate / moved-library leftover).

---

## 2. Status model

| Status | Meaning | Actionable |
|---|---|---|
| ✅ Installed | ACF parses, has depots, `StateFlags` has the installed bit, `SizeOnDisk > 0`, `buildid > 0` | no (a note is shown when Steam marks it "update required") |
| ⚠ Needs repair | ACF exists but is damaged (no depots / no installed bit / `SizeOnDisk=0` / `buildid=0`) | **repair** (only if files are complete) |
| ⚠ Missing ACF | Files verified complete, no ACF anywhere | **generate** |
| ？ Unverified | AppID unknown or no size baseline → completeness cannot be decided | set the AppID manually, then retry |
| ✖ Leftovers | Files incomplete (saves/logs/mods/patches only) | ignored (folder can be deleted) |
| ✖ Empty folder | No files at all | ignored (folder can be deleted) |

"Fix All Issues" only touches the actionable rows; leftovers/empty/unverified folders are skipped.

---

## 3. Where the facts come from

`Steam\appcache\appinfo.vdf` (format version `0x07564429`) is parsed offline and gives, per AppID:

* `config.installdir` / `common.name` → map a folder name to an AppID without any network access
* `depots.branches.public.buildid` → the current public build id
* per depot: `manifests.public.gid` / `size` / `download`, `config.oslist`, `config.language`,
  `dlcappid`, `sharedinstall` + `depotfromapp`

From that the tool computes *"how big a complete install should be"* (preferring the `SizeOnDisk`
Steam itself recorded in an existing ACF of the same AppID) and compares it with the measured folder size.
When no baseline is available it does **not** guess — the row is reported as *Unverified*.

Fallbacks when the cache is unusable: `logs\content_log.txt`, and (GUI build only) `steamcmd +app_info_print`.

> Cache format notes (v29): header `u32 magic | u32 universe | u64 string-table offset`;
> records are `appid | size | infoState | lastUpdated | token(8) | 44-byte digest | binary KV`,
> where KV keys are indexes into the string table at the end of the file; node types are
> `0=object 1=string 2=int32 7=uint64 8=end-of-object`. The same field can be stored as an integer
> node in one record and as a string node in another — both are handled.

---

## 4. What repair / generate actually write

**Repair (ACF present, damaged)**

1. The folder is verified; if it is not complete the repair is **refused** (otherwise Steam would
   believe the game is installed and re-download everything).
2. The depot set of the existing ACF is kept (that is what this machine really installed).
3. The manifest GID stored in the ACF describes *the content on disk* and may be older than the
   current public one — it is **kept** in that case; the cache value is only used when the recorded
   value is missing/invalid (e.g. `0`, `1`).
4. `StateFlags=4`, `SizeOnDisk=` measured, `buildid`/`TargetBuildID` = current public build,
   `Bytes*` counters = measured size, `SharedDepots` with owner AppIDs.
5. The original file is backed up as `.bak` and a copy is written to the export folder.

**Generate (files complete, ACF missing)**

1. Content is verified first; otherwise the tool refuses and shows *measured X / expected Y (%)*.
2. Also refused: empty folder, incomplete content, unknown AppID, an ACF already exists for that
   AppID (here or in another library), no current `buildid`, no usable depot manifest.
3. Depots written = required base depots + language packs that fit the measured size
   (DLC depots are left out: ownership cannot be verified offline).
4. `buildid` and every depot manifest come from Steam's cache (current public branch), so Steam's
   verification matches; if the files are an older build Steam patches them on the next start.

> Close Steam before repairing/generating, then restart it.

---

## 5. StateFlags column

`StateFlags` is a bit mask. The grid shows the number followed by the set bits in English:

```
4  (FullyInstalled)                          -> Steam shows "Play"
6  (UpdateRequired | FullyInstalled)         -> installed, update pending
2  (UpdateRequired)                          -> needs an update/download
1024 / 1048576 (UpdateStarted / Downloading) -> downloading right now
```

Full bit list (also available as a tooltip on the column header):

| Bit | Name | Bit | Name |
|---|---|---|---|
| 1 | Uninstalled | 2048 | Uninstalling |
| 2 | UpdateRequired | 4096 | BackupRunning |
| 4 | FullyInstalled | 65536 | Reconfiguring |
| 8 | UpdateQueued | 131072 | Validating |
| 16 | UpdateOptional | 262144 | AddingFiles |
| 32 | FilesMissing | 524288 | Preallocating |
| 64 | SharedOnly | 1048576 | Downloading |
| 128 | FilesCorrupt | 2097152 | Staging |
| 256 | UpdateRunning | 4194304 | Committing |
| 512 | UpdatePaused | 8388608 | UpdateStopping |

The column auto-sizes to its content (`AllCells` + a measured minimum width) so the text is never clipped.

---

## 6. Languages

English (default), 简体中文, 繁體中文, 日本語, 한국어, Español, Deutsch, Русский.

Pick one in the drop-down **immediately left of the "Rescan" button**; the choice is stored in
`%APPDATA%\SteamACFManager\settings.ini` and restored on the next start. `STEAM_ACF_LANG=zh-CN`
overrides it for one run (handy for scripts). The self-test reports how many translations are missing,
so adding a language is a matter of adding one column in `Localization.cs`.

---

## 7. Requirements & build

Windows, .NET Framework 4.5 or newer (nothing else — the in-box `csc.exe` is enough).

```
build.cmd        -> SteamACFManager.exe      (GUI + CLI, the main program)
build-cli.cmd    -> SteamACFManagerCLI.exe   (console only, no network)
```

Manual equivalent:

```
csc /codepage:65001 /target:winexe ^
    /r:System.Windows.Forms.dll /r:System.Drawing.dll ^
    /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll ^
    /r:System.Web.Extensions.dll /r:Microsoft.VisualBasic.dll ^
    /out:SteamACFManager.exe Localization.cs AcfCore.cs SteamACFManagerGUI.cs
```

> The `.cmd` files must stay **GBK + CRLF** encoded, otherwise `cmd.exe` mis-parses the Chinese text.

## 8. Command line

```
SteamACFManager.exe scan                                       list every entry with its verdict
SteamACFManager.exe repair <appid> [--dry-run]                 repair a damaged ACF
SteamACFManager.exe generate <installDir> [appid] [--dry-run]  generate an ACF for a verified folder
SteamACFManager.exe gui-selftest                               build the UI headlessly (self-test)
```

Environment variables:

| Variable | Effect |
|---|---|
| `STEAM_ACF_ROOT` | use another Steam root (portable install, tests) |
| `STEAM_ACF_LANG` | force a UI language, e.g. `zh-CN` |
| `STEAM_ACF_EXPORT_DIR` | where export copies go (default: `export\` next to the exe) |

## 9. Tests

```
tests\run-tests.cmd
```

Builds a synthetic Steam root under `tests\steamrootA|B` (placeholder files created with
`SetLength`, so they occupy no real space) and runs **45 assertions**: empty folder, leftover folders,
complete-but-no-ACF, duplicate install, wrong AppID, damaged ACF repair, generated ACF re-scan,
StateFlags rendering, English default and "no missing translations" for all 8 languages.
The real Steam library is never touched (`STEAM_ACF_ROOT` isolates everything).

## 10. Project layout

```
Localization.cs          8-language string table, StateFlags decoding, language persistence
AcfCore.cs               appinfo.vdf parser, folder probe, completeness verifier, ACF writer
SteamACFManagerGUI.cs    GUI (WinForms) + its CLI commands
SteamACFManager.cs       standalone console build
build.cmd / build-cli.cmd
tests\                   synthetic Steam root builder + regression suite (45 checks)
SteamACFManager.exe      prebuilt GUI build
SteamACFManagerCLI.exe   prebuilt console build
```

---

## 中文说明（摘要）

这个工具扫描 / 校验 / 修复 / 生成 Steam 的 `appmanifest_<AppID>.acf`。

**核心原则：目录存在 ≠ 游戏存在。** Steam 卸载游戏后会留下不受 manifest 管理的残留（存档、日志、
崩溃转储、汉化补丁、模组、甚至 0 字节空目录），旧版程序只看「有目录 + 没有 ACF」就报「缺少 ACF」，
还会为这些目录凭空生成 `buildid=0`、`SizeOnDisk=0` 的 ACF，让 Steam 误以为已安装并重新下载整个游戏。

现在只有当 **① 目录内容经校验确实完整**（实测体积 ≥ 已知完整体积的 90%）且 **② 任何库里都没有该 AppID
的 ACF** 时，才判定为「缺 ACF」。判定依据是 Steam 自己的本地缓存 `appcache/appinfo.vdf`（完全离线）：
里面有 `installdir`、当前 public 分支的 `buildid`、每个 depot 的 manifest GID / 体积 / 平台 / 语言 /
DLC / 共享标记。拿不到体积基准时不会乱猜，而是列为「未验证」。

界面默认英文，可在「重新扫描」左边的下拉框切换 8 种语言；`StateFlags` 列显示为
`4 (FullyInstalled)`、`6 (UpdateRequired | FullyInstalled)` 这种「数字 + 英文含义」形式，列宽自适应不会截断。

修复会保留原 ACF 里描述「磁盘上这份内容」的 manifest（可能比缓存的当前版本旧），只在原值无效时才用缓存补；
生成前必须先通过内容校验，否则一律拒绝并给出「实测 X / 预期 Y（百分比）」。操作前请关闭 Steam。

编译：`build.cmd`（图形版）/ `build-cli.cmd`（命令行版）；测试：`tests\run-tests.cmd`（45 项断言全部通过）。

> 说明：仓库未附带开源许可证（License），如需开源请自行添加（例如 MIT）。
