// SteamACFManager - 扫描/修复/导出 Steam 游戏 ACF 文件（命令行版）
//
// 说明：图形界面版（SteamACFManagerGUI.cs）才是功能完整的主程序，联网补全（steamcmd 等）
// 只在图形版里实现。命令行版与管理器共用 AcfCore.cs 的核心判定：目录存在 ≠ 游戏存在，
// 只有“文件确实完整”的目录才算「缺少 ACF」。
//
// 编译: csc /codepage:65001 /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll /out:SteamACFManagerCLI.exe AcfCore.cs SteamACFManager.cs
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace SteamACFManager
{
    internal enum AcfStatus
    {
        InstalledOk,    // ACF 正常，Steam 显示已安装
        Damaged,        // ACF 存在但损坏（可修复）
        MissingAcf,     // 有游戏文件夹、文件已校验完整、只缺 ACF（可生成）
        Unverified,     // 有游戏文件夹，但无法校验是否完整
        ResidueFolder,  // 有文件夹但只剩残留/文件不完整（不能生成 ACF）
        EmptyFolder     // 有文件夹但里面没有任何文件
    }

    internal sealed class DepotInfo
    {
        public string Manifest = "";
        public string Size = "0";
        public string DlcAppId = "";
    }

    internal sealed class AcfInfo
    {
        public string Path = "";
        public string Library = "";
        public string AppId = "";
        public string Name = "";
        public long StateFlags = 0;
        public string InstallDir = "";
        public long SizeOnDisk = 0;
        public long BuildId = 0;
        public long TargetBuildId = 0;
        public string LastOwner = "";
        public string LauncherPath = "";
        public string Language = "schinese";
        public Dictionary<string, DepotInfo> Depots = new Dictionary<string, DepotInfo>();
        public HashSet<string> DlcDepotIds = new HashSet<string>();
        public List<KeyValuePair<string, string>> SharedDepots = new List<KeyValuePair<string, string>>();
        public AcfStatus Status = AcfStatus.MissingAcf;
        public string DamageReason = "";
        public bool FullyInstalled { get { return (StateFlags & 4) != 0; } }

        // 孤儿目录（无 ACF）专用
        public string Folder = "";
        public long ActualBytes = 0;
        public ContentCheck Check;
        public string AppIdSource = "";
        public List<KeyValuePair<string, string>> InstallScripts = new List<KeyValuePair<string, string>>();
    }

    internal sealed class VdfNode
    {
        public string Key = "";
        public string Value = "";
        public bool HasValue = false;
        public List<VdfNode> Children = new List<VdfNode>();

        public VdfNode Find(string key)
        {
            foreach (VdfNode c in Children) if (c.Key == key) return c;
            return null;
        }
        public string Get(string key)
        {
            VdfNode n = Find(key);
            if (n != null && n.HasValue) return n.Value;
            return null;
        }
        public long GetLong(string key, long def)
        {
            string s = Get(key);
            long v;
            if (s != null && long.TryParse(s, out v)) return v;
            return def;
        }
    }

    internal static class Program
    {
        private const long SteamIdBase = 76561197960265728L;
        private static string steamPath = "";
        private static List<string> libraries = new List<string>();
        private static string LastPreview = "";

        private static int Main(string[] args)
        {
            Loc.Initialize();   // 默认英文；可用 STEAM_ACF_LANG 环境变量切换
            if (!DetectSteam())
            {
                Console.WriteLine(Loc.T("cli.notFound"));
                
                return 1;
            }
            LoadLibraries();
            if (args.Length == 0) return RunInteractive();
            return RunCli(args);
        }

        // ---------------- Steam 定位 ----------------
        private static bool DetectSteam()
        {
            steamPath = "";
            // 允许用环境变量指定 Steam 根目录（自建测试环境 / 便携安装用得上）
            string overridePath = Environment.GetEnvironmentVariable("STEAM_ACF_ROOT");
            if (!string.IsNullOrEmpty(overridePath) && Directory.Exists(overridePath)) steamPath = overridePath;
            if (string.IsNullOrEmpty(steamPath))
            {
                try { object v = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null); if (v != null) { steamPath = v.ToString(); } } catch { }
            }
            if (string.IsNullOrEmpty(steamPath))
            {
                try { object v = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null); if (v != null) { steamPath = v.ToString(); } } catch { }
            }
            if (string.IsNullOrEmpty(steamPath))
            {
                string[] cand = { @"C:\Program Files (x86)\Steam", @"C:\Program Files\Steam", @"D:\Steam", @"E:\Steam", @"D:\Program Files (x86)\Steam" };
                foreach (string c in cand) if (File.Exists(Path.Combine(c, "steam.exe"))) { steamPath = c; break; }
            }
            if (string.IsNullOrEmpty(steamPath)) return false;
            steamPath = steamPath.Replace("/", "\\").TrimEnd('\\');
            Console.WriteLine(Loc.T("cli.steamFolder", steamPath));
            // 载入 Steam 本地 App 元数据缓存（离线判定 installdir / buildid / depot 体积）
            AppInfoCache.EnsureLoaded(steamPath);
            if (AppInfoCache.Available)
                Console.WriteLine(Loc.T("ui.cacheOk", AppInfoCache.AppCount));
            else
                Console.WriteLine(Loc.T("ui.cacheBad", AppInfoCache.LoadError));
            return true;
        }

        private static void LoadLibraries()
        {
            libraries.Clear();
            string lf = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
            if (File.Exists(lf))
            {
                try
                {
                    VdfNode lfRoot = ParseVdf(File.ReadAllText(lf));
                    if (lfRoot != null && lfRoot.Key == "libraryfolders")
                    {
                        foreach (VdfNode c in lfRoot.Children)
                        {
                            string p = c.Get("path");
                            if (!string.IsNullOrEmpty(p)) AddLibrary(p.Replace("/", "\\").TrimEnd('\\'));
                        }
                    }
                }
                catch { }
            }
            AddLibrary(steamPath);
        }

        private static void AddLibrary(string path)
        {
            foreach (string l in libraries)
                if (string.Equals(l, path, StringComparison.OrdinalIgnoreCase)) return;
            libraries.Add(path);
        }

        // ---------------- VDF 解析 ----------------
        private static List<string> Tokenize(string s)
        {
            List<string> tokens = new List<string>();
            int i = 0;
            while (i < s.Length)
            {
                char c = s[i];
                if (c == '{') { tokens.Add("{"); i++; }
                else if (c == '}') { tokens.Add("}"); i++; }
                else if (c == '"')
                {
                    i++;
                    StringBuilder sb = new StringBuilder();
                    while (i < s.Length)
                    {
                        char d = s[i];
                        if (d == '\\' && i + 1 < s.Length) { sb.Append(s[i + 1]); i += 2; continue; }
                        if (d == '"') { i++; break; }
                        sb.Append(d); i++;
                    }
                    tokens.Add(sb.ToString());
                }
                else { i++; }
            }
            return tokens;
        }

        private static VdfNode ParseVdf(string text)
        {
            List<string> tokens = Tokenize(text);
            if (tokens.Count == 0) return null;
            int pos = 0;
            return ParseNode(tokens, ref pos);
        }

        private static VdfNode ParseNode(List<string> tokens, ref int pos)
        {
            VdfNode node = new VdfNode();
            node.Key = tokens[pos]; pos++;
            if (pos < tokens.Count && tokens[pos] == "{")
            {
                pos++;
                while (pos < tokens.Count && tokens[pos] != "}")
                {
                    node.Children.Add(ParseNode(tokens, ref pos));
                }
                if (pos < tokens.Count) pos++; // consume }
                node.HasValue = false;
            }
            else
            {
                node.Value = tokens[pos]; pos++;
                node.HasValue = true;
            }
            return node;
        }

        // ---------------- ACF 解析 ----------------
        private static AcfInfo ParseAcf(string path)
        {
            VdfNode app = ParseVdf(File.ReadAllText(path, Encoding.UTF8));
            if (app == null || app.Key != "AppState") return null;

            AcfInfo info = new AcfInfo();
            info.Path = path;
            info.AppId = app.Get("appid") ?? "";
            info.Name = app.Get("name") ?? Path.GetFileName(path);
            info.StateFlags = app.GetLong("StateFlags", 0);
            info.InstallDir = app.Get("installdir") ?? "";
            info.SizeOnDisk = app.GetLong("SizeOnDisk", 0);
            info.BuildId = app.GetLong("buildid", 0);
            info.TargetBuildId = app.GetLong("TargetBuildID", 0);
            info.LastOwner = app.Get("LastOwner") ?? "";
            info.LauncherPath = app.Get("LauncherPath") ?? "";
            info.Language = "schinese";

            VdfNode ucfg = app.Find("UserConfig");
            if (ucfg != null) { string l = ucfg.Get("language"); if (!string.IsNullOrEmpty(l)) info.Language = l; }

            VdfNode deps = app.Find("InstalledDepots");
            if (deps != null)
            {
                foreach (VdfNode d in deps.Children)
                {
                    DepotInfo di = new DepotInfo();
                    di.Manifest = d.Get("manifest") ?? "";
                    di.Size = d.Get("size") ?? "0";
                    di.DlcAppId = d.Get("dlcappid") ?? "";
                    info.Depots[d.Key] = di;
                }
            }

            VdfNode dlc = app.Find("DlcDownloads");
            if (dlc != null)
            {
                foreach (VdfNode d in dlc.Children) info.DlcDepotIds.Add(d.Key);
            }

            VdfNode shared = app.Find("SharedDepots");
            if (shared != null)
            {
                foreach (VdfNode d in shared.Children) info.SharedDepots.Add(new KeyValuePair<string, string>(d.Key, d.Value));
            }

            string sa = Path.GetDirectoryName(info.Path);
            info.Library = Path.GetDirectoryName(sa) ?? "";
            return info;
        }

        private static string ReadSteamAppIdFile(string folder)
        {
            string f = Path.Combine(folder, "steam_appid.txt");
            if (!File.Exists(f)) return "";
            try
            {
                string content = File.ReadAllText(f);
                foreach (string line in content.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string t = line.Trim();
                    long v;
                    if (t.Length > 0 && long.TryParse(t, out v)) return t;
                }
                long vv;
                string c = content.Trim();
                if (long.TryParse(c, out vv)) return c;
                return "";
            }
            catch { return ""; }
        }

        private static string StatusText(AcfStatus st)
        {
            // 控制台用 ASCII 标记版本的状态名（emoji 在 GBK 控制台里会变乱码）
            switch (st)
            {
                case AcfStatus.InstalledOk: return Loc.T("st.cli.installed");
                case AcfStatus.Damaged: return Loc.T("st.cli.damaged");
                case AcfStatus.MissingAcf: return Loc.T("st.cli.missingAcf");
                case AcfStatus.Unverified: return Loc.T("st.cli.unverified");
                case AcfStatus.ResidueFolder: return Loc.T("st.cli.residue");
                default: return Loc.T("st.cli.empty");
            }
        }

        private static string GetCurrentSteamId()
        {
            string ud = Path.Combine(steamPath, "userdata");
            if (Directory.Exists(ud))
            {
                foreach (string d in Directory.GetDirectories(ud))
                {
                    string name = Path.GetFileName(d);
                    long acc;
                    if (long.TryParse(name, out acc) && acc > 0) return (SteamIdBase + acc).ToString();
                }
            }
            return "";
        }

        private static void Classify(AcfInfo a)
        {
            if (a.Depots.Count == 0)
            {
                a.Status = AcfStatus.Damaged;
                a.DamageReason = Loc.T("classify.noDepots");
            }
            else if (!a.FullyInstalled)
            {
                a.Status = AcfStatus.Damaged;
                a.DamageReason = Loc.T("classify.noInstalledBit", a.StateFlags);
            }
            else if (a.SizeOnDisk <= 0)
            {
                a.Status = AcfStatus.Damaged;
                a.DamageReason = "SizeOnDisk=0";
            }
            else if (a.BuildId <= 0)
            {
                a.Status = AcfStatus.Damaged;
                a.DamageReason = "buildid=0";
            }
            else
            {
                a.Status = AcfStatus.InstalledOk;
            }
        }

        // ---------------- 扫描 ----------------
        private static List<AcfInfo> ScanGames()
        {
            List<AcfInfo> result = new List<AcfInfo>();
            HashSet<string> acfAppIds = new HashSet<string>();

            foreach (string lib in libraries)
            {
                string sa = Path.Combine(lib, "steamapps");
                if (!Directory.Exists(sa)) continue;
                string[] acfs;
                try { acfs = Directory.GetFiles(sa, "appmanifest_*.acf"); } catch { continue; }
                foreach (string f in acfs)
                {
                    try
                    {
                        AcfInfo a = ParseAcf(f);
                        if (a != null)
                        {
                            Classify(a);
                            result.Add(a);
                            if (!string.IsNullOrEmpty(a.AppId)) acfAppIds.Add(a.AppId);
                        }
                    }
                    catch (Exception ex)
                    {
                        AcfInfo a = new AcfInfo();
                        a.Path = f;
                        a.Name = Path.GetFileName(f);
                        a.Library = lib;
                        a.Status = AcfStatus.Damaged;
                        a.DamageReason = Loc.T("classify.parseError", ex.Message);
                        result.Add(a);
                    }
                }

            }

            // 第二遍：孤儿文件夹（common 下有目录但这台机器上没有对应 ACF 的 installdir）。
            // 必须等所有库的 ACF 都扫完再判断，否则会漏掉“同 AppID 已在另一个库安装”的情况；
            // 而且必须先用目录内容判定：只剩存档/日志/补丁等残留（甚至空目录）的目录不算「缺少 ACF」。
            foreach (string lib in libraries)
            {
                string sa = Path.Combine(lib, "steamapps");
                if (!Directory.Exists(sa)) continue;
                string common = Path.Combine(sa, "common");
                if (!Directory.Exists(common)) continue;
                string[] dirs;
                try { dirs = Directory.GetDirectories(common); } catch { dirs = new string[0]; }
                foreach (string d in dirs)
                {
                    string folderName = Path.GetFileName(d);
                    if (folderName == "Steamworks Shared" || folderName == "Steam Controller Configs") continue;
                    if (folderName.StartsWith("SteamLinuxRuntime", StringComparison.OrdinalIgnoreCase)) continue;

                    bool hasAcf = false;
                    foreach (AcfInfo a in result)
                    {
                        if (string.Equals(a.Library, lib, StringComparison.OrdinalIgnoreCase)
                            && string.Equals(a.InstallDir, folderName, StringComparison.OrdinalIgnoreCase)) { hasAcf = true; break; }
                    }
                    if (hasAcf) continue;

                    result.Add(BuildOrphanEntry(d, folderName, lib, result));
                }
            }

            result.Sort(delegate(AcfInfo x, AcfInfo y)
            {
                int s = x.Status.CompareTo(y.Status);
                if (s != 0) return s;
                return string.Compare(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);
            });
            return result;
        }

        /// <summary>
        /// 判定一个“有目录但没 ACF”的目录：确定 AppID → 量体积 → 校验内容完整性 →
        /// 只有校验通过才算「缺 ACF（可生成）」。
        /// </summary>
        private static AcfInfo BuildOrphanEntry(string dir, string folderName, string lib, List<AcfInfo> allAcfs)
        {
            AcfInfo orphan = new AcfInfo();
            orphan.Path = dir;
            orphan.Folder = dir;
            orphan.Name = folderName;
            orphan.InstallDir = folderName;
            orphan.Library = lib;

            orphan.AppId = ReadSteamAppIdFile(dir);
            if (!string.IsNullOrEmpty(orphan.AppId)) orphan.AppIdSource = Loc.T("appid.from.appidtxt");
            if (string.IsNullOrEmpty(orphan.AppId))
            {
                string id = AppInfoCache.FindAppIdByInstallDir(folderName);
                if (!string.IsNullOrEmpty(id)) { orphan.AppId = id; orphan.AppIdSource = Loc.T("appid.from.installdir"); }
            }
            if (string.IsNullOrEmpty(orphan.AppId))
            {
                string id = AppInfoCache.FindAppIdByLooseName(folderName);
                if (!string.IsNullOrEmpty(id)) { orphan.AppId = id; orphan.AppIdSource = Loc.T("appid.from.loose"); }
            }

            AppMeta meta = AppInfoCache.Get(orphan.AppId);
            if (meta != null && !string.IsNullOrEmpty(meta.Name)) orphan.Name = meta.Name;

            long reference = 0;
            string referenceSource = "";
            foreach (AcfInfo a in allAcfs)
            {
                if (!string.IsNullOrEmpty(orphan.AppId) && a.AppId == orphan.AppId && a.SizeOnDisk > 0)
                {
                    reference = a.SizeOnDisk;
                    referenceSource = Loc.T("src.templateAcf");
                    break;
                }
            }

            ContentCheck chk = ContentVerifier.Check(dir, meta, reference, referenceSource);
            orphan.Check = chk;
            orphan.ActualBytes = chk.ActualBytes;
            orphan.SizeOnDisk = chk.ActualBytes;

            string appIdNote = string.IsNullOrEmpty(orphan.AppId)
                ? Loc.T("appid.unknown")
                : Loc.T("appid.suffix", orphan.AppId, orphan.AppIdSource);

            AcfInfo elsewhere = null;
            if (!string.IsNullOrEmpty(orphan.AppId))
            {
                foreach (AcfInfo a in allAcfs)
                {
                    if (a.AppId == orphan.AppId && !string.Equals(a.Library, lib, StringComparison.OrdinalIgnoreCase))
                    { elsewhere = a; break; }
                }
            }

            switch (chk.Verdict)
            {
                case ContentVerdict.Empty:
                    orphan.Status = AcfStatus.EmptyFolder;
                    orphan.DamageReason = chk.Reason + appIdNote;
                    break;
                case ContentVerdict.Incomplete:
                    orphan.Status = AcfStatus.ResidueFolder;
                    orphan.DamageReason = chk.Reason + appIdNote;
                    break;
                case ContentVerdict.Complete:
                    if (elsewhere != null)
                    {
                        orphan.Status = AcfStatus.Unverified;
                        orphan.DamageReason = Loc.T("reason.dupStale", elsewhere.Library) + appIdNote;
                    }
                    else
                    {
                        orphan.Status = AcfStatus.MissingAcf;
                        orphan.DamageReason = chk.Reason + Loc.T("reason.onlyAcfMissing") + appIdNote;
                    }
                    break;
                default:
                    orphan.Status = AcfStatus.Unverified;
                    orphan.DamageReason = chk.Reason + appIdNote + Loc.T("reason.unverifiedHint");
                    break;
            }
            return orphan;
        }

        private static string Truncate(string s, int max)
        {
            if (s == null) return "";
            if (s.Length <= max) return s;
            return s.Substring(0, max - 1) + "…";
        }

        private static string FormatSize(long bytes)
        {
            // 统一用 AcfFormat：40 字节会显示 “40 B”，而不是误导性的 “0.0 MB”
            return AcfFormat.Bytes(bytes);
        }

        private static void PrintScan(List<AcfInfo> games)
        {
            Console.WriteLine();
            Console.WriteLine(Loc.T("cli.totalEntries", games.Count));
            Console.WriteLine();
            Console.WriteLine(Loc.T("cli.scanTable"));
            Console.WriteLine(new string('-', 100));
            foreach (AcfInfo g in games)
            {
                bool hasAcf = g.Folder.Length == 0;   // 孤儿目录没有 ACF，不显示 StateFlags/buildid
                Console.WriteLine(string.Format("{0,-18} {1,-10} {2,-26} {3,-34} {4,-11} {5}",
                    StatusText(g.Status), g.AppId, Truncate(g.Name, 26),
                    hasAcf ? Loc.StateFlagsText(g.StateFlags) : "", FormatSize(g.SizeOnDisk), Truncate(g.Library, 40)));
                Console.WriteLine("    " + g.Path);
                if (!hasAcf || (g.Status != AcfStatus.InstalledOk && !string.IsNullOrEmpty(g.DamageReason)))
                    Console.WriteLine("    " + g.DamageReason);
            }
        }

        private static AcfInfo FindAcf(string appid)
        {
            foreach (string lib in libraries)
            {
                string f = Path.Combine(lib, "steamapps", "appmanifest_" + appid + ".acf");
                if (File.Exists(f))
                {
                    try { return ParseAcf(f); } catch { }
                }
            }
            return null;
        }

        // ---------------- 修复 ----------------
        private static string GetGameFolder(AcfInfo acf)
        {
            return Path.Combine(acf.Library, "steamapps", "common", acf.InstallDir);
        }

        private static long ComputeFolderSize(string dir)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return 0;
            long total = 0;
            try
            {
                foreach (string f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    try { total += new FileInfo(f).Length; } catch { }
                }
            }
            catch { }
            return total;
        }

        private static string[] ReadAllLinesShared(string path)
        {
            try
            {
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (StreamReader sr = new StreamReader(fs, Encoding.UTF8))
                {
                    List<string> list = new List<string>();
                    string line;
                    while ((line = sr.ReadLine()) != null) list.Add(line);
                    return list.ToArray();
                }
            }
            catch { return null; }
        }

        private static bool TryExtractFromContentLog(string appid, out Dictionary<string, string> manifests)
        {
            manifests = new Dictionary<string, string>();
            string log = Path.Combine(steamPath, "logs", "content_log.txt");
            if (!File.Exists(log)) return false;

            string[] lines = ReadAllLinesShared(log);
            if (lines == null) return false;

            Regex depotRegex = new Regex(@"AppID\s+" + Regex.Escape(appid) + @"\s+config changed\s*:\s*added depots\s+(.+)");
            List<string> depotIds = new List<string>();
            foreach (string line in lines)
            {
                Match m = depotRegex.Match(line);
                if (m.Success)
                {
                    depotIds.Clear();
                    string list = m.Groups[1].Value.Trim();
                    foreach (string part in list.Split(','))
                    {
                        string p = part.Trim();
                        if (p.Length > 0) depotIds.Add(p);
                    }
                }
            }
            if (depotIds.Count == 0) return false;

            Regex manRegex = new Regex(@"depot/(\d+)/manifest/(\d+)/");
            foreach (string line in lines)
            {
                Match m = manRegex.Match(line);
                if (m.Success)
                {
                    string dep = m.Groups[1].Value;
                    string man = m.Groups[2].Value;
                    if (depotIds.Contains(dep)) manifests[dep] = man;
                }
            }
            return manifests.Count > 0;
        }

        /// <summary>
        /// 获取 depot/buildid 元数据：优先 Steam 本地缓存 appinfo.vdf（离线），
        /// 其次 content_log.txt（只有 depot 的 manifest，没有 buildid 与体积）。
        /// 命令行版不联网，需要联网补全时请用图形界面版。
        /// </summary>
        private static AppMeta ResolveMeta(string appid, out string error)
        {
            error = null;
            AppMeta meta = AppInfoCache.Get(appid);
            if (meta != null && meta.Depots.Count > 0)
            {
                foreach (DepotMeta d in meta.Depots) if (d.HasManifest) return meta;
            }

            Dictionary<string, string> manifests;
            if (TryExtractFromContentLog(appid, out manifests))
            {
                AppMeta fromLog = new AppMeta();
                fromLog.AppId = appid;
                fromLog.Source = "content_log.txt";
                foreach (KeyValuePair<string, string> kv in manifests)
                {
                    DepotMeta dm = new DepotMeta();
                    dm.Id = kv.Key;
                    dm.Manifest = kv.Value;
                    fromLog.Depots.Add(dm);
                }
                return fromLog;
            }

            error = Loc.T("err.noDepotInfoCli", appid);
            return null;
        }

        private static string GetSteamLanguage()
        {
            try
            {
                object v = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "Language", null);
                if (v != null && v.ToString().Length > 0) return v.ToString();
            }
            catch { }
            return "schinese";
        }

        private static bool LooksLikeManifest(string manifest)
        {
            long v;
            if (string.IsNullOrEmpty(manifest) || !long.TryParse(manifest, out v)) return false;
            return v > 1;
        }

        private static void FillUnknownSizes(List<DepotMeta> depots, long totalBytes)
        {
            long known = 0;
            int unknown = 0;
            foreach (DepotMeta d in depots)
            {
                if (d.Size > 0) known += d.Size; else unknown++;
            }
            if (unknown == 0) return;
            long rest = totalBytes - known;
            if (rest < 0) rest = 0;
            long each = rest / unknown;
            foreach (DepotMeta d in depots) if (d.Size <= 0) d.Size = each;
        }

        // 修复已存在的（损坏的）ACF。成功返回 null，失败返回错误信息（预览见 LastPreview）。
        private static string Repair(string appid, bool dryRun)
        {
            LastPreview = "";
            AcfInfo acf = FindAcf(appid);
            if (acf == null) return Loc.T("err.noAcfForAppId", appid);

            string err;
            AppMeta meta = ResolveMeta(appid, out err);
            if (meta == null) return err;

            long buildid = meta.BuildId;
            if (buildid <= 0) buildid = acf.TargetBuildId > 0 ? acf.TargetBuildId : acf.BuildId;
            if (buildid <= 0)
                return Loc.T("err.noBuildId", appid);

            string folder = GetGameFolder(acf);
            ContentCheck chk = ContentVerifier.Check(folder, meta, acf.SizeOnDisk, Loc.T("src.oldAcf"));
            if (chk.Verdict != ContentVerdict.Complete)
                return Loc.T("err.refuseRepair", chk.Reason, folder, "") + "\n"
                     + Loc.T("err.repairHint");

            List<string> notes = new List<string>();
            List<DepotMeta> depots = new List<DepotMeta>();
            int kept = 0, refreshed = 0;
            foreach (KeyValuePair<string, DepotInfo> kv in acf.Depots)
            {
                DepotMeta live = meta.FindDepot(kv.Key);
                DepotMeta use = new DepotMeta();
                use.Id = kv.Key;
                use.DlcAppId = kv.Value.DlcAppId;
                if (live != null)
                {
                    use.Shared = live.Shared;
                    use.SharedOwner = live.SharedOwner;
                    use.Language = live.Language;
                    use.OsList = live.OsList;
                }
                long recorded;
                long.TryParse(kv.Value.Size, out recorded);
                if (LooksLikeManifest(kv.Value.Manifest))
                {
                    // 原 ACF 里的 manifest 描述的是磁盘上这份内容，可能比缓存里的当前 public 旧，必须保留
                    use.Manifest = kv.Value.Manifest;
                    use.Size = recorded;
                    kept++;
                }
                else if (live != null && live.HasManifest)
                {
                    use.Manifest = live.Manifest;
                    use.Size = live.Size;
                    refreshed++;
                    notes.Add(Loc.T("log.depotFixed", kv.Key, kv.Value.Manifest));
                }
                else
                {
                    use.Manifest = kv.Value.Manifest;
                    use.Size = recorded;
                }
                depots.Add(use);
            }
            if (depots.Count == 0)
            {
                depots = AcfWriter.PickInstalledDepots(meta, chk.ActualBytes, GetSteamLanguage(), notes);
                notes.Add(Loc.T("log.depotRebuilt", depots.Count));
            }
            else
            {
                notes.Add(Loc.T("log.depotKept", depots.Count, kept, refreshed));
            }
            FillUnknownSizes(depots, chk.ActualBytes);

            AcfSpec spec = new AcfSpec();
            spec.AppId = appid;
            spec.Name = !string.IsNullOrEmpty(meta.Name) ? meta.Name : acf.Name;
            spec.InstallDir = acf.InstallDir;
            spec.LauncherPath = !string.IsNullOrEmpty(acf.LauncherPath) ? acf.LauncherPath : Path.Combine(steamPath, "steam.exe");
            spec.LastOwner = acf.LastOwner;
            spec.Language = acf.Language;
            spec.StateFlags = 4;
            spec.SizeOnDisk = chk.ActualBytes;
            spec.BuildId = buildid;
            spec.TargetBuildId = buildid;
            spec.LastUpdated = AcfWriter.NowUnix();
            spec.Depots = depots;
            spec.SharedDepots = AcfWriter.SharedDepotsOf(meta, acf.SharedDepots);
            spec.InstallScripts = acf.InstallScripts;
            string content = AcfWriter.Build(spec);
            LastPreview = content;

            Console.WriteLine(Loc.T("log.appid", appid, spec.Name));
            Console.WriteLine(Loc.T("log.folder", folder));
            Console.WriteLine(Loc.T("log.contentCheck", chk.Reason));
            Console.WriteLine(Loc.T("log.sizes", chk.ActualBytes, buildid, 4));
            foreach (string note in notes) Console.WriteLine("  " + note);

            if (dryRun)
            {
                Console.WriteLine(Loc.T("cli.previewHeader"));
                Console.WriteLine(content);
                Console.WriteLine("================================");
                return null;
            }

            string bak = acf.Path + ".bak";
            File.Copy(acf.Path, bak, true);
            File.WriteAllText(acf.Path, content, new UTF8Encoding(false));
            Console.WriteLine(Loc.T("cli.repaired", acf.Path, bak));
            ExportCopyToDefaultDir(appid, content);
            Console.WriteLine(Loc.T("cli.restartSteam"));
            return null;
        }

        // 为“确认文件完整、只缺 ACF”的目录生成 ACF。成功返回 null，失败返回错误信息。
        private static string Generate(AcfInfo orphan, bool dryRun)
        {
            LastPreview = "";
            if (string.IsNullOrEmpty(orphan.AppId))
                return Loc.T("err.noAppId");

            string err;
            AppMeta meta = ResolveMeta(orphan.AppId, out err);
            if (meta == null) return err;

            ContentCheck chk = ContentVerifier.Check(orphan.Path, meta, 0, "");
            if (chk.Verdict != ContentVerdict.Complete)
                return Loc.T("err.refuseIncomplete", chk.Reason, orphan.Path);

            if (meta.BuildId <= 0)
                return Loc.T("err.noBuildId", orphan.AppId);

            string acfPath = Path.Combine(orphan.Library, "steamapps", "appmanifest_" + orphan.AppId + ".acf");
            if (File.Exists(acfPath))
                return Loc.T("err.targetAcfExists", acfPath);

            foreach (string lib in libraries)
            {
                if (string.Equals(lib, orphan.Library, StringComparison.OrdinalIgnoreCase)) continue;
                string other = Path.Combine(lib, "steamapps", "appmanifest_" + orphan.AppId + ".acf");
                if (File.Exists(other))
                    return Loc.T("err.duplicateOtherLib", orphan.AppId, lib, other, other)
                         ;
            }

            List<string> notes = new List<string>();
            List<DepotMeta> depots = AcfWriter.PickInstalledDepots(meta, chk.ActualBytes, GetSteamLanguage(), notes);
            if (depots.Count == 0) return Loc.T("err.noUsableDepots", orphan.AppId);
            FillUnknownSizes(depots, chk.ActualBytes);

            AcfSpec spec = new AcfSpec();
            spec.AppId = orphan.AppId;
            spec.Name = !string.IsNullOrEmpty(orphan.Name) ? orphan.Name : meta.Name;
            spec.InstallDir = orphan.InstallDir;
            spec.LauncherPath = Path.Combine(steamPath, "steam.exe");
            spec.LastOwner = GetCurrentSteamId();
            spec.Language = GetSteamLanguage();
            spec.StateFlags = 4;
            spec.SizeOnDisk = chk.ActualBytes;
            spec.BuildId = meta.BuildId;
            spec.TargetBuildId = meta.BuildId;
            spec.LastUpdated = AcfWriter.NowUnix();
            spec.Depots = depots;
            spec.SharedDepots = AcfWriter.SharedDepotsOf(meta, null);
            string content = AcfWriter.Build(spec);
            LastPreview = content;

            Console.WriteLine(Loc.T("log.appid", orphan.AppId, spec.Name));
            Console.WriteLine(Loc.T("log.folder", orphan.Path));
            Console.WriteLine(Loc.T("log.contentCheck", chk.Reason));
            Console.WriteLine(Loc.T("log.sizes", chk.ActualBytes, meta.BuildId, 4));
            foreach (string note in notes) Console.WriteLine("  " + note);

            if (dryRun)
            {
                Console.WriteLine(Loc.T("cli.previewHeader"));
                Console.WriteLine(content);
                Console.WriteLine("================================");
                return null;
            }

            File.WriteAllText(acfPath, content, new UTF8Encoding(false));
            Console.WriteLine(Loc.T("cli.generated", acfPath));
            ExportCopyToDefaultDir(orphan.AppId, content);
            Console.WriteLine(Loc.T("cli.restartSteam"));
            return null;
        }

        private static void ExportCopyToDefaultDir(string appid, string content)
        {
            try
            {
                string outDir = Environment.GetEnvironmentVariable("STEAM_ACF_EXPORT_DIR");
                if (string.IsNullOrEmpty(outDir)) outDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "export");
                Directory.CreateDirectory(outDir);
                string dest = Path.Combine(outDir, "appmanifest_" + appid + ".acf");
                File.WriteAllText(dest, content, new UTF8Encoding(false));
                Console.WriteLine(Loc.T("cli.exportedCopy", dest));
            }
            catch { }
        }

        private static void RepairAllDamaged(bool dryRun)
        {
            List<AcfInfo> games = ScanGames();
            int ok = 0, fail = 0, skipped = 0;
            foreach (AcfInfo g in games)
            {
                if (g.Status == AcfStatus.Damaged)
                {
                    if (string.IsNullOrEmpty(g.AppId)) { skipped++; continue; }
                    Console.WriteLine();
                    Console.WriteLine(Loc.T("ui.working", g.Name, g.AppId));
                    if (Repair(g.AppId, dryRun) == null) ok++; else fail++;
                }
                else if (g.Status == AcfStatus.MissingAcf)
                {
                    Console.WriteLine();
                    Console.WriteLine(Loc.T("ui.working", g.Name, g.AppId));
                    if (Generate(g, dryRun) == null) ok++; else fail++;
                }
                else if (g.Status == AcfStatus.Unverified || g.Status == AcfStatus.ResidueFolder || g.Status == AcfStatus.EmptyFolder)
                {
                    skipped++;
                }
            }
            Console.WriteLine();
            Console.WriteLine(Loc.T("cli.processDone", ok, fail, skipped));
        }

// ---------------- 导出 ----------------
        private static string ResolveOutDir(string outDir)
        {
            if (string.IsNullOrEmpty(outDir)) outDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "export");
            Directory.CreateDirectory(outDir);
            return outDir;
        }

        private static void Export(string appid, string outDir)
        {
            AcfInfo acf = FindAcf(appid);
            if (acf == null) { Console.WriteLine(Loc.T("cli.exportNotFound", appid)); return; }
            string dir = ResolveOutDir(outDir);
            string dest = Path.Combine(dir, "appmanifest_" + appid + ".acf");
            File.Copy(acf.Path, dest, true);
            Console.WriteLine(Loc.T("cli.exportTo", dest));
        }

        private static void ExportAll(string outDir)
        {
            List<AcfInfo> games = ScanGames();
            string dir = ResolveOutDir(outDir);
            int n = 0;
            foreach (AcfInfo g in games)
            {
                if (g.Status == AcfStatus.InstalledOk && !string.IsNullOrEmpty(g.AppId))
                {
                    string dest = Path.Combine(dir, "appmanifest_" + g.AppId + ".acf");
                    File.Copy(g.Path, dest, true);
                    Console.WriteLine(Loc.T("cli.exportItem", g.AppId, g.Name));
                    n++;
                }
            }
            Console.WriteLine(Loc.T("cli.exportAllDone", n, dir));
        }

        // ---------------- CLI / 交互 ----------------
        private static int RunCli(string[] args)
        {
            string cmd = args[0].ToLowerInvariant();
            if (cmd == "scan") { PrintScan(ScanGames()); return 0; }
            if (cmd == "repair")
            {
                if (args.Length < 2) { Console.WriteLine(Loc.T("cli.repairArgs")); return 1; }
                bool dry = args.Any(a => a == "--dry-run");
                string r = Repair(args[1], dry);
                if (r != null) { Console.WriteLine(Loc.T("cli.fail", r)); return 1; }
                Console.WriteLine("OK");
                return 0;
            }
            if (cmd == "generate")
            {
                if (args.Length < 2) { Console.WriteLine(Loc.T("cli.generateArgs")); return 1; }
                bool dry = args.Any(a => a == "--dry-run");
                string appid = null;
                foreach (string a in args)
                {
                    long tmp;
                    if (a != args[0] && long.TryParse(a, out tmp)) appid = a;
                }
                AcfInfo g = null;
                foreach (AcfInfo x in ScanGames())
                {
                    if (x.Status != AcfStatus.MissingAcf && x.Status != AcfStatus.Unverified) continue;
                    if (string.Equals(x.InstallDir, args[1], StringComparison.OrdinalIgnoreCase)
                        || string.Equals(x.Name, args[1], StringComparison.OrdinalIgnoreCase)) { g = x; break; }
                }
                if (g == null) { Console.WriteLine(Loc.T("cli.orphanNotFound")); return 1; }
                if (appid != null) g.AppId = appid;
                string r = Generate(g, dry);
                if (r != null) { Console.WriteLine(Loc.T("cli.fail", r)); return 1; }
                Console.WriteLine("OK");
                return 0;
            }
            if (cmd == "repair-all")
            {
                bool dry = args.Any(a => a == "--dry-run");
                RepairAllDamaged(dry);
                return 0;
            }
            if (cmd == "export")
            {
                if (args.Length < 2) { Console.WriteLine(Loc.T("cli.exportArgs")); return 1; }
                string outDir = args.Length >= 3 ? args[2] : null;
                Export(args[1], outDir);
                return 0;
            }
            if (cmd == "export-all")
            {
                string outDir = args.Length >= 2 ? args[1] : null;
                ExportAll(outDir);
                return 0;
            }
            Console.WriteLine(Loc.T("cli.unknownCmd"));
            Console.WriteLine("  scan");
            Console.WriteLine("  repair <appid> [--dry-run]");
            Console.WriteLine("  generate <installDir> [appid] [--dry-run]");
            Console.WriteLine("  repair-all [--dry-run]");
            Console.WriteLine("  " + Loc.T("cli.exportArgs"));
            Console.WriteLine("  " + Loc.T("cli.exportAllArgs"));
            Console.WriteLine(Loc.T("cli.hint"));
            return 1;
        }

        private static int RunInteractive()
        {
            while (true)
            {
                PrintScan(ScanGames());
                Console.WriteLine();
                Console.WriteLine(Loc.T("menu.title"));
                Console.WriteLine(Loc.T("menu.repairOne"));
                Console.WriteLine(Loc.T("menu.exportOne"));
                Console.WriteLine(Loc.T("menu.exportAll"));
                Console.WriteLine(Loc.T("menu.repairAll"));
                Console.WriteLine(Loc.T("menu.rescan"));
                Console.WriteLine(Loc.T("menu.exit"));
                Console.Write("> ");
                string input = Console.ReadLine();
                if (input == null) break;
                input = input.Trim();
                if (input == "0" || input == "") break;
                switch (input)
                {
                    case "1":
                        Console.Write(Loc.T("menu.prompt"));
                        string appid = Console.ReadLine();
                        if (!string.IsNullOrEmpty(appid)) Repair(appid.Trim(), false);
                        break;
                    case "2":
                        Console.Write(Loc.T("menu.prompt"));
                        appid = Console.ReadLine();
                        if (!string.IsNullOrEmpty(appid)) Export(appid.Trim(), null);
                        break;
                    case "3":
                        ExportAll(null);
                        break;
                    case "4":
                        RepairAllDamaged(false);
                        break;
                    case "5":
                        break;
                    default:
                        Console.WriteLine(Loc.T("menu.invalid"));
                        break;
                }
                Console.WriteLine();
            }
            return 0;
        }
    }
}
