// SteamEvidence.cs - 本地证据：content_log 的“完成更新”记录 + depotcache 里的 manifest
//
// 这两个来源解决了「凭空把 ACF 修复正确」最核心的问题：
//
//  1) logs\content_log.txt 里每个 App 最近一次“成功完成更新”会留下：
//        [2026-09-27 08:05:05] AppID 4001890 finished update, 1 mounted depots (BuildID 25127368)
//          : 4001891 (8322781817822782512),
//     它给出了 **与磁盘内容自洽的 buildid + 每个 depot 的 manifest gid**。
//     用 Steam 缓存里的“当前 public”去猜是错的：那可能是比磁盘更新的版本。
//
//  2) <Steam 根>\depotcache\<depot>_<gid>.manifest 是 Steam 手里的“收货单”。
//     如果 ACF 里写的 gid 在这里存在，Steam 不需要重新下载 manifest，直接就能核对/接受文件；
//     反之 Steam 必须先下载 manifest，然后必然拿它去核对文件（用户看到的就是“又在验证”）。
//
// 注意：新版本客户端的 depotcache 在 **Steam 根目录** 下（不是 steamapps 下），
// 旧版在 steamapps\depotcache；本模块两处都会找。
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace SteamACFManager
{
    /// <summary>某个 App 最近一次“成功完成更新”的记录。</summary>
    internal sealed class UpdateEvidence
    {
        public string AppId = "";
        public long BuildId;
        public string When = "";
        public string RawLine = "";
        public List<KeyValuePair<string, string>> Depots = new List<KeyValuePair<string, string>>();

        public string GidOf(string depotId)
        {
            foreach (KeyValuePair<string, string> kv in Depots)
                if (kv.Key == depotId) return kv.Value;
            return "";
        }
        public bool HasDepot(string depotId) { return GidOf(depotId).Length > 0; }
    }

    internal static class ContentLogEvidence
    {
        private static readonly Dictionary<string, UpdateEvidence> byApp =
            new Dictionary<string, UpdateEvidence>(StringComparer.Ordinal);
        private static bool loaded;
        private static string loadedFrom = "";
        private static string loadError = "";
        private static DateTime loadedAt = DateTime.MinValue;

        public static bool Available { get { return loaded && byApp.Count > 0; } }
        public static int Count { get { return byApp.Count; } }
        public static string LoadError { get { return loadError; } }
        public static string LoadedFrom { get { return loadedFrom; } }

        public static void EnsureLoaded(string steamPath)
        {
            string path = Path.Combine(steamPath, "logs", "content_log.txt");
            if (loaded && string.Equals(path, loadedFrom, StringComparison.OrdinalIgnoreCase)) return;

            // 日志可能正在被 Steam 写，文件很大；只在第一次（或文件更新后）读一遍
            loaded = true;
            loadedFrom = path;
            loadError = "";
            byApp.Clear();
            if (!File.Exists(path)) { loadError = "未找到 " + path; return; }
            try
            {
                loadedAt = File.GetLastWriteTimeUtc(path);
                Regex rx = new Regex(@"AppID\s+(\d+)\s+finished update.*?\(BuildID\s+(\d+)\)\s*:\s*(.+)$");
                Regex rxDepot = new Regex(@"(\d+)\s*\((\d+)\)");
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (StreamReader sr = new StreamReader(fs))
                {
                    string line;
                    while ((line = sr.ReadLine()) != null)
                    {
                        Match m = rx.Match(line);
                        if (!m.Success) continue;
                        UpdateEvidence ev = new UpdateEvidence();
                        ev.AppId = m.Groups[1].Value;
                        long bid;
                        if (!long.TryParse(m.Groups[2].Value, out bid)) continue;
                        ev.BuildId = bid;
                        ev.When = line.Length > 21 ? line.Substring(0, 21) : "";
                        ev.RawLine = line.Trim();
                        foreach (Match dm in rxDepot.Matches(m.Groups[3].Value))
                            ev.Depots.Add(new KeyValuePair<string, string>(dm.Groups[1].Value, dm.Groups[2].Value));
                        byApp[ev.AppId] = ev;   // 后面的记录覆盖前面的 => 保留“最近一次”
                    }
                }
                if (byApp.Count == 0) loadError = "content_log.txt 里没有 “finished update” 记录";
            }
            catch (Exception ex) { loadError = ex.Message; }
        }

        /// <summary>文件被 Steam 写过之后重新载入（长时间开着的界面也能拿到新证据）。</summary>
        public static void RefreshIfChanged(string steamPath)
        {
            string path = Path.Combine(steamPath, "logs", "content_log.txt");
            if (!File.Exists(path)) return;
            try
            {
                if (File.GetLastWriteTimeUtc(path) != loadedAt) { loaded = false; EnsureLoaded(steamPath); }
            }
            catch { }
        }

        public static UpdateEvidence Get(string appid)
        {
            if (string.IsNullOrEmpty(appid)) return null;
            UpdateEvidence ev;
            if (byApp.TryGetValue(appid, out ev)) return ev;
            return null;
        }
    }

    /// <summary>depotcache：Steam 已经下载到本地的 manifest 文件（“收货单”）。</summary>
    internal static class DepotCache
    {
        private static readonly Dictionary<string, string> byKey =
            new Dictionary<string, string>(StringComparer.Ordinal);          // "depot_gid" -> 文件路径
        private static readonly Dictionary<string, List<string>> byDepot =
            new Dictionary<string, List<string>>(StringComparer.Ordinal);    // depot -> gid 列表
        private static bool loaded;
        private static string loadedKey = "";
        private static List<string> scannedDirs = new List<string>();

        public static bool Available { get { return loaded && byKey.Count > 0; } }
        public static int Count { get { return byKey.Count; } }
        public static string ScannedDirs { get { return string.Join("; ", scannedDirs.ToArray()); } }

        public static void EnsureLoaded(string steamPath, List<string> libraryPaths)
        {
            string key = steamPath + "|" + (libraryPaths == null ? "" : string.Join(",", libraryPaths.ToArray()));
            if (loaded && key == loadedKey) return;
            loaded = true;
            loadedKey = key;
            byKey.Clear();
            byDepot.Clear();
            scannedDirs.Clear();

            List<string> dirs = new List<string>();
            AddDir(dirs, Path.Combine(steamPath, "depotcache"));            // 新版客户端：Steam 根目录
            AddDir(dirs, Path.Combine(steamPath, "steamapps", "depotcache")); // 旧版：steamapps 下
            if (libraryPaths != null)
            {
                foreach (string lib in libraryPaths)
                {
                    AddDir(dirs, Path.Combine(lib, "depotcache"));
                    AddDir(dirs, Path.Combine(lib, "steamapps", "depotcache"));
                }
            }

            foreach (string dir in dirs)
            {
                string[] files;
                try { files = Directory.GetFiles(dir, "*.manifest"); } catch { continue; }
                scannedDirs.Add(dir);
                foreach (string f in files)
                {
                    string name = Path.GetFileNameWithoutExtension(f);
                    int sep = name.IndexOf('_');
                    if (sep <= 0 || sep >= name.Length - 1) continue;
                    string depot = name.Substring(0, sep);
                    string gid = name.Substring(sep + 1);
                    string k = depot + "_" + gid;
                    if (byKey.ContainsKey(k)) continue;
                    byKey[k] = f;
                    List<string> list;
                    if (!byDepot.TryGetValue(depot, out list)) { list = new List<string>(); byDepot[depot] = list; }
                    list.Add(gid);
                }
            }
        }

        private static void AddDir(List<string> dirs, string dir)
        {
            if (Directory.Exists(dir) && !dirs.Contains(dir)) dirs.Add(dir);
        }

        public static bool Has(string depotId, string gid)
        {
            if (string.IsNullOrEmpty(depotId) || string.IsNullOrEmpty(gid)) return false;
            return byKey.ContainsKey(depotId + "_" + gid);
        }

        public static string PathOf(string depotId, string gid)
        {
            string p;
            if (byKey.TryGetValue(depotId + "_" + gid, out p)) return p;
            return "";
        }

        public static List<string> GidsOf(string depotId)
        {
            List<string> list;
            if (byDepot.TryGetValue(depotId, out list)) return list;
            return new List<string>();
        }
    }

    /// <summary>一份“该写进 ACF 的 depot 方案”及其依据。</summary>
    internal sealed class DepotPlan
    {
        public List<DepotMeta> Depots = new List<DepotMeta>();
        public long BuildId;
        public string BuildIdSourceKey = "";
        public long PublicBuildId;
        public bool UsedEvidence;               // 是否用了 content_log 的“完成更新”记录
        public int GidFromEvidence;
        public int GidFromPublic;
        public int GidFromCacheOnly;
        public int ManifestPresent;
        public int ManifestMissing;
        public int GidDiffersFromPublic;   // 与 Steam 当前 public 的 manifest 不同的 depot 数
        public AppMeta PublicMeta;                 // Steam 缓存的当前 public 元数据（用于对比）
        public List<string> Notes = new List<string>();

        public DepotMeta PublicDepotOf(string depotId)
        {
            return PublicMeta == null ? null : PublicMeta.FindDepot(depotId);
        }
    }

    /// <summary>
    /// 决定“往 ACF 里写哪些 depot、每个用哪个 manifest gid、buildid 取哪个”。
    /// 优先级：content_log 最近一次完成更新的 gid（与磁盘自洽）→ Steam 缓存的当前 public gid
    /// → depotcache 里已有的其它 gid。并记录每个 gid 的 manifest 是否已在 depotcache。
    /// </summary>
    internal static class DepotPlanner
    {
        public static DepotPlan Build(string appid, AppMeta meta, List<DepotMeta> existing)
        {
            DepotPlan plan = new DepotPlan();
            plan.PublicMeta = meta;
            if (meta != null) plan.PublicBuildId = meta.BuildId;
            UpdateEvidence ev = ContentLogEvidence.Get(appid);

            // 1) 确定 depot 集合
            List<DepotMeta> set = new List<DepotMeta>();
            if (existing != null && existing.Count > 0)
            {
                // 修复：沿用原 ACF 里那套（= 这台机器实际装过的组合）
                set.AddRange(existing);
            }
            else if (ev != null && ev.Depots.Count > 0)
            {
                // 生成：优先用日志里那次“完成更新”挂载的 depot 集合
                foreach (KeyValuePair<string, string> kv in ev.Depots)
                {
                    DepotMeta d = new DepotMeta();
                    d.Id = kv.Key;
                    d.Manifest = kv.Value;
                    if (meta != null)
                    {
                        DepotMeta live = meta.FindDepot(kv.Key);
                        if (live != null)
                        {
                            d.DlcAppId = live.DlcAppId;
                            d.Shared = live.Shared;
                            d.Language = live.Language;
                            d.OsList = live.OsList;
                            if (live.Size > 0) d.Size = live.Size;
                        }
                    }
                    set.Add(d);
                }
                plan.UsedEvidence = true;
            }
            else if (meta != null)
            {
                // 没有任何本地证据：只能按缓存的当前 public 版本推导（会提示可能触发一次验证）
                set = AcfWriter.PickInstalledDepots(meta, 0, "", plan.Notes);
            }

            // 2) 逐个 depot 挑 gid
            foreach (DepotMeta d in set)
            {
                string evidenceGid = ev != null ? ev.GidOf(d.Id) : "";
                string publicGid = "";
                long publicSize = -1;
                if (meta != null)
                {
                    DepotMeta live = meta.FindDepot(d.Id);
                    if (live != null && live.HasManifest)
                    {
                        publicGid = live.Manifest;
                        publicSize = live.Size;
                    }
                }

                string chosen = "";
                if (evidenceGid.Length > 0 && DepotCache.Has(d.Id, evidenceGid))
                {
                    chosen = evidenceGid; plan.GidFromEvidence++;
                }
                else if (publicGid.Length > 0 && DepotCache.Has(d.Id, publicGid))
                {
                    chosen = publicGid; plan.GidFromPublic++;
                }
                else if (evidenceGid.Length > 0)
                {
                    chosen = evidenceGid; plan.GidFromEvidence++;      // depotcache 里没有，Steam 会去下载 manifest
                }
                else if (publicGid.Length > 0)
                {
                    chosen = publicGid; plan.GidFromPublic++;
                }
                else
                {
                    List<string> cached = DepotCache.GidsOf(d.Id);
                    if (cached.Count > 0) { chosen = cached[cached.Count - 1]; plan.GidFromCacheOnly++; }
                    else chosen = d.Manifest;
                }

                d.Manifest = chosen;
                if (chosen == publicGid && publicSize > 0) d.Size = publicSize;
                if (DepotCache.Has(d.Id, chosen)) plan.ManifestPresent++; else plan.ManifestMissing++;
                if (publicGid.Length > 0 && publicGid != chosen) plan.GidDiffersFromPublic++;
                plan.Depots.Add(d);
            }

            // 3) buildid：优先与磁盘内容自洽的那次记录
            if (ev != null && ev.BuildId > 0)
            {
                plan.BuildId = ev.BuildId;
                plan.BuildIdSourceKey = "log.buildidFromLog";
                plan.UsedEvidence = true;
            }
            else if (meta != null && meta.BuildId > 0)
            {
                plan.BuildId = meta.BuildId;
                plan.BuildIdSourceKey = "log.buildidFromCache";
            }
            return plan;
        }
    }
}
