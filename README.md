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

## 2. Typical use cases

### 2.1 You moved a Steam library and the ACF files did not come along

You copied or moved `steamapps\common\<Game>` to another drive/library (or restored a backup), and now
Steam says the game is not installed, or wants to download it all over again — because
`appmanifest_<AppID>.acf` stayed behind. Steam only recognises an install through that file, so the
game folder alone is invisible to it.

1. Close Steam.
2. Start the tool — the folder is listed as **⚠ Missing ACF** (files verified complete, no ACF in any library).
3. Select it and press **Repair / Generate Selected**. The ACF is written into that library's
   `steamapps` folder with the current public `buildid` and depot manifests taken from Steam's own
   cache, so Steam accepts the files already on disk instead of re-downloading them.
4. Start Steam — the game shows **Play**.

Command line: `SteamACFManager.exe generate "<folder name>"`

Notes:

* The AppID is looked up offline from Steam's cache (`installdir` / game-name index). If that is not
  possible, drop a `steam_appid.txt` containing the AppID into the folder, or type the AppID in the dialog.
* If the *old* library still holds a leftover ACF for the same AppID while its game folder is gone
  (the usual result of a move), the tool refuses to generate and tells you to delete that stale ACF
  first — otherwise Steam would see two installs of the same game. Delete it, rescan, generate.
* Only folders that pass the completeness check can be generated; leftover shells are reported as
  *Leftovers* and are refused.

### 2.2 Steam is stuck in "verifying files" (…99 % → validation failed → back to 1 %, forever)

Some downloads never finish: Steam reaches ~99 %, reports *file validation failed*, rolls back to an
earlier state and starts over, again and again. That loop comes from `StateFlags` in the ACF: while the
update/validation bits are set (typically `6 (UpdateRequired | FullyInstalled)`), Steam keeps verifying
the same depot. Writing the state back to `4` (`FullyInstalled`) tells Steam the install is complete,
which ends the loop.

1. Close Steam.
2. Start the tool — the game shows up as **⚠ Needs repair**, with `StateFlags` such as
   `6 (UpdateRequired | FullyInstalled)`.
3. Press **Repair / Generate Selected**. The folder is verified first (a ~99 % complete download
   passes), the depot manifests that match the files on disk are kept, and the ACF is rewritten with
   `StateFlags=4`, the measured `SizeOnDisk` and the current public `buildid`. The old ACF is kept as `.bak`.
4. Start Steam — the game shows **Play**. If you ever suspect the content really is damaged, you can
   still run "Verify integrity of game files" yourself, once, on your terms.

Command line: `SteamACFManager.exe repair <appid>` (add `--dry-run` to see the ACF before writing it)

Notes and honest limits:

* `StateFlags=4` is written **only when the folder passes the content check** (≥ 90 % of the expected
  full size). If the download really only has, say, 10 % on disk, the repair is refused — claiming
  "fully installed" there would just make Steam re-download everything.
* It only fixes Steam's own bookkeeping (state flags / depot manifests) for an install you already
  have; it does not bypass ownership, DRM or Steam's licence checks, and it never modifies game files.
* Steam owns the ACF and rewrites it while running, so always close Steam before repairing or
  generating, and restart it afterwards.

---

## 3. Status model

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

## 4. Where the facts come from

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

## 5. What repair / generate actually write

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

## 6. StateFlags column

`StateFlags` is a bit mask. The grid shows the number followed by the set bits in English:

```
4  (FullyInstalled)                          -> Steam shows "Play"
6  (UpdateRequired | FullyInstalled)         -> installed, update pending  (the state behind the 99% loop)
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

## 7. Languages

English (default), 简体中文, 繁體中文, 日本語, 한국어, Español, Deutsch, Русский.

Pick one in the drop-down **immediately left of the "Rescan" button**; the choice is stored in
`%APPDATA%\SteamACFManager\settings.ini` and restored on the next start. `STEAM_ACF_LANG=zh-CN`
overrides it for one run (handy for scripts). The self-test reports how many translations are missing,
so adding a language is a matter of adding one column in `Localization.cs`.

---

## 8. Requirements & build

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

## 9. Command line

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

## 10. Tests

```
tests\run-tests.cmd
```

Builds a synthetic Steam root under `tests\steamrootA|B` (placeholder files created with
`SetLength`, so they occupy no real space) and runs **45 assertions**: empty folder, leftover folders,
complete-but-no-ACF, duplicate install, wrong AppID, damaged ACF repair, generated ACF re-scan,
StateFlags rendering, English default and "no missing translations" for all 8 languages.
The real Steam library is never touched (`STEAM_ACF_ROOT` isolates everything).

## 11. Project layout

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

### 两个典型使用场景

**① 迁移了游戏文件夹，却忘了迁移 ACF**
把 `steamapps\common\<游戏>` 复制/搬到别的盘或别的库之后，Steam 只认 ACF 文件，不认光秃秃的目录，
于是显示未安装、甚至要重新下载。处理方法：**关掉 Steam → 打开本工具**，该目录会显示为
**⚠ 缺 ACF（文件完整）→ 选中后点「修复/生成选中项」**，工具会把 ACF 写进那个库的 `steamapps` 目录，
`buildid` 与每个 depot 的 manifest 都取自 Steam 当前 public 版本，因此 Steam 会直接接受已存在的文件，
不会重新下载。命令行：`SteamACFManager.exe generate "<文件夹名>"`。
注意：如果**旧的库还留着一个过期的 ACF**（游戏目录已经不在了），工具会拒绝生成并提示你先删掉那个陈旧
ACF——否则 Steam 会认为是两份安装；删掉后重新扫描再生成即可。

**② Steam 卡在「正在验证文件」：下到 99% → 提示文件验证失败 → 跳回前面，无限循环**
这种循环来自 ACF 里的 `StateFlags`：带着更新/验证位（通常是 `6 (UpdateRequired | FullyInstalled)`）时，
Steam 会反复验证同一个 depot。把状态改回 `4`（FullyInstalled，也就是显示「开始游戏」的状态）即可结束循环。
处理方法：**关掉 Steam → 打开本工具**，该游戏显示为 **⚠ 需修复**（StateFlags 列会写着
`6 (UpdateRequired | FullyInstalled)`）→ **选中后点「修复/生成选中项」**：工具先校验目录（约 99% 的
下载能通过），保留与磁盘文件相符的 depot manifest，并写入 `StateFlags=4`、实测 `SizeOnDisk` 与当前
public `buildid`，旧 ACF 备份为 `.bak`；重启 Steam 即可显示「开始游戏」。命令行：
`SteamACFManager.exe repair <appid>`（加 `--dry-run` 可先预览不写入）。
注意：只有目录内容通过校验（≥ 预期完整体积的 90%）时才会写 `StateFlags=4`；如果盘上其实只有 10%，
工具会拒绝——那样写「已安装」只会让 Steam 重新下载整包。它只修 Steam 自己的登记信息，
不绕过所有权/DRM，也不修改任何游戏文件。

界面默认英文，可在「重新扫描」左边的下拉框切换 8 种语言；`StateFlags` 列显示为
`4 (FullyInstalled)`、`6 (UpdateRequired | FullyInstalled)` 这种「数字 + 英文含义」形式，列宽自适应不会截断。

修复会保留原 ACF 里描述「磁盘上这份内容」的 manifest（可能比缓存的当前版本旧），只在原值无效时才用缓存补；
生成前必须先通过内容校验，否则一律拒绝并给出「实测 X / 预期 Y（百分比）」。操作前请关闭 Steam。

编译：`build.cmd`（图形版）/ `build-cli.cmd`（命令行版）；测试：`tests\run-tests.cmd`（45 项断言全部通过）。

> 说明：仓库未附带开源许可证（License），如需开源请自行添加（例如 MIT）。
