// SteamACFManager (GUI) - 扫描/修复/生成/导出 Steam 游戏 ACF 文件（WinForms）
// 编译: csc /codepage:65001 /target:winexe /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll /r:Microsoft.VisualBasic.dll /out:SteamACFManager.exe AcfCore.cs SteamACFManagerGUI.cs
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.VisualBasic;
using Microsoft.Win32;

namespace SteamACFManager
{
    internal enum AcfStatus
    {
        InstalledOk,    // ACF 正常
        Damaged,        // ACF 存在但损坏（可修复）
        MissingAcf,     // 有游戏文件夹、文件已校验完整、只缺 ACF（可生成）
        Unverified,     // 有游戏文件夹，但无法校验是否完整（未确定 AppID 或缺少体积基准）
        ResidueFolder,  // 有文件夹但只剩残留/文件不完整（不能生成 ACF）
        EmptyFolder     // 有文件夹但里面没有任何文件（卸载残留，与 ACF 无关）
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
        public List<KeyValuePair<string, string>> InstallScripts = new List<KeyValuePair<string, string>>();
        public AcfStatus Status = AcfStatus.MissingAcf;
        public string DamageReason = "";
        public bool FullyInstalled { get { return (StateFlags & 4) != 0; } }

        // 孤儿目录（无 ACF）专用：内容校验结果与实测体积
        public string Folder = "";
        public long ActualBytes = 0;
        public int FileCount = 0;
        public ContentCheck Check;
        public string AppIdSource = "";

        public bool Actionable
        {
            get { return Status == AcfStatus.Damaged || Status == AcfStatus.MissingAcf; }
        }
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

    // ---------------- 核心逻辑 ----------------
    internal static class Core
    {
        public static string steamPath = "";
        public static List<string> libraries = new List<string>();
        private const long SteamIdBase = 76561197960265728L;

        // 最近一次操作的日志与预览（dry-run 时填写），供界面/命令行显示
        public static string LastReport = "";
        public static string LastPreview = "";


        public static bool DetectSteam()
        {
            steamPath = "";
            // 允许用环境变量指定 Steam 根目录（自建测试环境 / 便携安装用得上）
            string overridePath = Environment.GetEnvironmentVariable("STEAM_ACF_ROOT");
            if (!string.IsNullOrEmpty(overridePath) && Directory.Exists(overridePath))
                steamPath = overridePath;
            if (string.IsNullOrEmpty(steamPath))
            {
                try { object v = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null); if (v != null) steamPath = v.ToString(); } catch { }
            }
            if (string.IsNullOrEmpty(steamPath))
            {
                try { object v = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null); if (v != null) steamPath = v.ToString(); } catch { }
            }
            if (string.IsNullOrEmpty(steamPath))
            {
                string[] cand = { @"C:\Program Files (x86)\Steam", @"C:\Program Files\Steam", @"D:\Steam", @"E:\Steam", @"D:\Program Files (x86)\Steam" };
                foreach (string c in cand) if (File.Exists(Path.Combine(c, "steam.exe"))) { steamPath = c; break; }
            }
            if (string.IsNullOrEmpty(steamPath)) return false;
            steamPath = steamPath.Replace("/", "\\").TrimEnd('\\');

            // 载入 Steam 自己的 App 元数据缓存：离线判定 installdir / buildid / depot 体积都靠它
            AppInfoCache.EnsureLoaded(steamPath);
            return true;
        }

        public static string AppInfoCacheStatus
        {
            get
            {
                if (AppInfoCache.Available)
                    return Loc.T("ui.cacheOk", AppInfoCache.AppCount);
                return Loc.T("ui.cacheBad", AppInfoCache.LoadError);
            }
        }

        public static string StatusTextOf(AcfStatus st)
        {
            switch (st)
            {
                case AcfStatus.InstalledOk: return Loc.T("st.installed");
                case AcfStatus.Damaged: return Loc.T("st.damaged");
                case AcfStatus.MissingAcf: return Loc.T("st.missingAcf");
                case AcfStatus.Unverified: return Loc.T("st.unverified");
                case AcfStatus.ResidueFolder: return Loc.T("st.residue");
                default: return Loc.T("st.empty");
            }
        }

        /// <summary>命令行输出用的状态名：纯 ASCII 标记，避免 emoji 在不支持的控制台里变成乱码。</summary>
        public static string StatusTextCliOf(AcfStatus st)
        {
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

        public static void LoadLibraries()
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
                if (pos < tokens.Count) pos++;
                node.HasValue = false;
            }
            else
            {
                node.Value = tokens[pos]; pos++;
                node.HasValue = true;
            }
            return node;
        }

        public static AcfInfo ParseAcf(string path)
        {
            VdfNode app = ParseVdf(File.ReadAllText(path, Encoding.UTF8));
            if (app == null || app.Key != "AppState") return null;

            AcfInfo info = new AcfInfo();
            info.Path = path;
            info.AppId = app.Get("appid") ?? "";
            if (string.IsNullOrEmpty(info.AppId))
            {
                string fn = Path.GetFileNameWithoutExtension(path);
                if (fn.StartsWith("appmanifest_")) info.AppId = fn.Substring("appmanifest_".Length);
            }
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
                a.DamageReason = Loc.T("classify.sizeZero");
            }
            else if (a.BuildId <= 0)
            {
                a.Status = AcfStatus.Damaged;
                a.DamageReason = Loc.T("classify.buildZero");
            }
            else
            {
                a.Status = AcfStatus.InstalledOk;
                // StateFlags 含位 2 表示 Steam 认为有可用更新（例如 6 = 已安装 + 需要更新）
                if ((a.StateFlags & 2) != 0)
                    a.DamageReason = Loc.T("classify.needsUpdate", a.StateFlags);
            }
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
                if (long.TryParse(content.Trim(), out vv)) return content.Trim();
                return "";
            }
            catch { return ""; }
        }

        public static AcfInfo FindAcfByInstallDir(string installDir)
        {
            if (string.IsNullOrEmpty(installDir)) return null;
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
                        if (a != null && string.Equals(a.InstallDir, installDir, StringComparison.OrdinalIgnoreCase)) return a;
                    }
                    catch { }
                }
            }
            return null;
        }

        // 按文件夹名（游戏标准名）搜索 Steam 商店，返回 (AppID, 名称)；找不到返回 ("","")
        public static KeyValuePair<string, string> SearchSteamStore(string term)
        {
            try
            {
                string url = "https://store.steampowered.com/api/storesearch/?term=" + Uri.EscapeDataString(term) + "&cc=US&l=en";
                string json;
                using (TimedWebClient wc = new TimedWebClient(10000))
                {
                    wc.Headers[HttpRequestHeader.UserAgent] = "Mozilla/5.0";
                    json = wc.DownloadString(url);
                }
                JavaScriptSerializer js = new JavaScriptSerializer();
                Dictionary<string, object> obj = js.DeserializeObject(json) as Dictionary<string, object>;
                if (obj == null || !obj.ContainsKey("items")) return new KeyValuePair<string, string>("", "");
                object[] items = obj["items"] as object[];
                if (items == null) return new KeyValuePair<string, string>("", "");
                foreach (object it in items)
                {
                    Dictionary<string, object> d = it as Dictionary<string, object>;
                    if (d == null) continue;
                    object t;
                    if (!d.TryGetValue("type", out t) || (t as string) != "app") continue;
                    object id, name;
                    string idStr = "";
                    string nameStr = "";
                    if (d.TryGetValue("id", out id)) idStr = Convert.ToString(id);
                    if (d.TryGetValue("name", out name)) nameStr = name as string;
                    if (idStr.Length > 0) return new KeyValuePair<string, string>(idStr, nameStr ?? "");
                }
                return new KeyValuePair<string, string>("", "");
            }
            catch { return new KeyValuePair<string, string>("", ""); }
        }

        public static string GetCurrentSteamId()
        {
            string ud = Path.Combine(steamPath, "userdata");
            if (Directory.Exists(ud))
            {
                foreach (string d in Directory.GetDirectories(ud))
                {
                    string name = Path.GetFileName(d);
                    long acc;
                    if (long.TryParse(name, out acc) && acc > 0)
                        return (SteamIdBase + acc).ToString();
                }
            }
            return "";
        }

        public static List<AcfInfo> ScanGames()
        {
            List<AcfInfo> result = new List<AcfInfo>();
            Dictionary<string, AcfInfo> acfByInstallDir = new Dictionary<string, AcfInfo>(StringComparer.OrdinalIgnoreCase);

            // 第一遍：扫描所有 ACF
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
                            if (!string.IsNullOrEmpty(a.InstallDir) && !acfByInstallDir.ContainsKey(a.InstallDir))
                                acfByInstallDir[a.InstallDir] = a;
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

            // 第二遍：扫描孤儿游戏文件夹（有文件夹但无 ACF）
            //
            // 关键：目录存在 ≠ 游戏存在。Steam 卸载/移动游戏后，steamapps\common 下常常
            // 残留一个只有存档、日志、崩溃转储、汉化补丁、模组、甚至 0 字节的空目录。
            // 这些目录里的文件并不属于游戏本体，所以“有文件夹但缺 ACF”必须先用目录内容
            // 校验：只有文件确实完整（体积与 Steam 记录的完整体积相符）时才算缺 ACF。
            foreach (string lib in libraries)
            {
                string common = Path.Combine(lib, "steamapps", "common");
                if (!Directory.Exists(common)) continue;
                string[] dirs;
                try { dirs = Directory.GetDirectories(common); } catch { continue; }
                foreach (string d in dirs)
                {
                    string folderName = Path.GetFileName(d);
                    if (IsNonGameCommonFolder(folderName)) continue;

                    bool hasAcf = false;
                    foreach (AcfInfo a in result)
                    {
                        if (string.Equals(a.Library, lib, StringComparison.OrdinalIgnoreCase)
                            && string.Equals(a.InstallDir, folderName, StringComparison.OrdinalIgnoreCase)) { hasAcf = true; break; }
                    }
                    if (hasAcf) continue;

                    result.Add(BuildOrphanEntry(d, folderName, lib, result, acfByInstallDir));
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

        /// <summary>这些 steamapps\common 下的目录不是游戏本体（共享运行库/手柄配置），不参与判定。</summary>
        public static bool IsNonGameCommonFolder(string folderName)
        {
            if (folderName == "Steamworks Shared") return true;
            if (folderName == "Steam Controller Configs") return true;
            if (folderName.StartsWith("SteamLinuxRuntime", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>
        /// 为一个“有目录但没 ACF”的目录建立条目：确定 AppID → 量体积 → 校验内容 →
        /// 分别判定为「缺 ACF（可生成）」「未验证」「残留/不完整」「空文件夹」。
        /// </summary>
        private static AcfInfo BuildOrphanEntry(string dir, string folderName, string lib,
            List<AcfInfo> allAcfs, Dictionary<string, AcfInfo> acfByInstallDir)
        {
            AcfInfo orphan = new AcfInfo();
            orphan.Path = dir;
            orphan.Folder = dir;
            orphan.Name = folderName;
            orphan.InstallDir = folderName;
            orphan.Library = lib;

            // 1) 确定 AppID：steam_appid.txt → 本地缓存 installdir/名称 索引 → 其它库同名 installdir 的 ACF
            AcfInfo tpl = null;
            orphan.AppId = ReadSteamAppIdFile(dir);
            if (!string.IsNullOrEmpty(orphan.AppId)) orphan.AppIdSource = Loc.T("appid.from.appidtxt");
            if (string.IsNullOrEmpty(orphan.AppId))
            {
                string id = AppInfoCache.FindAppIdByInstallDir(folderName);
                if (!string.IsNullOrEmpty(id)) { orphan.AppId = id; orphan.AppIdSource = Loc.T("appid.from.installdir"); }
            }
            if (string.IsNullOrEmpty(orphan.AppId))
            {
                string id = AppInfoCache.FindAppIdByName(folderName);
                if (!string.IsNullOrEmpty(id)) { orphan.AppId = id; orphan.AppIdSource = Loc.T("appid.from.name"); }
            }
            if (string.IsNullOrEmpty(orphan.AppId))
            {
                string id = AppInfoCache.FindAppIdByLooseName(folderName);
                if (!string.IsNullOrEmpty(id)) { orphan.AppId = id; orphan.AppIdSource = Loc.T("appid.from.loose"); }
            }
            if (string.IsNullOrEmpty(orphan.AppId) && acfByInstallDir.TryGetValue(folderName, out tpl)
                && tpl != null && !string.IsNullOrEmpty(tpl.AppId))
            {
                orphan.AppId = tpl.AppId;
                orphan.AppIdSource = Loc.T("appid.from.otheracf");
            }

            // 2) 元数据与“完整体积”基准
            AppMeta meta = AppInfoCache.Get(orphan.AppId);
            if (meta != null && !string.IsNullOrEmpty(meta.Name)) orphan.Name = meta.Name;
            if (meta == null && tpl != null && !string.IsNullOrEmpty(tpl.Name)) orphan.Name = tpl.Name;

            long reference = 0;
            string referenceSource = "";
            AcfInfo sameAppAcf = FindAcfForAppId(allAcfs, orphan.AppId);
            if (tpl != null && tpl.SizeOnDisk > 0)
            {
                reference = tpl.SizeOnDisk;
                referenceSource = Loc.T("src.templateAcf");
            }
            else if (sameAppAcf != null && sameAppAcf.SizeOnDisk > 0)
            {
                reference = sameAppAcf.SizeOnDisk;
                referenceSource = Loc.T("src.templateAcf");
            }

            // 3) 量体积并校验内容
            ContentCheck chk = ContentVerifier.Check(dir, meta, reference, referenceSource);
            orphan.Check = chk;
            orphan.ActualBytes = chk.ActualBytes;
            orphan.FileCount = chk.FileCount;
            orphan.SizeOnDisk = chk.ActualBytes;   // 让界面显示真实目录体积，而不是固定 0

            string appIdNote = string.IsNullOrEmpty(orphan.AppId)
                ? Loc.T("appid.unknown")
                : Loc.T("appid.suffix", orphan.AppId, orphan.AppIdSource);

            AcfInfo installedElsewhere = null;
            if (!string.IsNullOrEmpty(orphan.AppId))
            {
                foreach (AcfInfo a in allAcfs)
                {
                    if (a.AppId != orphan.AppId) continue;
                    if (string.Equals(a.Library, lib, StringComparison.OrdinalIgnoreCase)) continue;
                    installedElsewhere = a;
                    break;
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
                    if (installedElsewhere != null)
                    {
                        string otherFolder = Path.Combine(installedElsewhere.Library, "steamapps", "common", installedElsewhere.InstallDir);
                        bool otherStillHasFiles = Directory.Exists(otherFolder);
                        orphan.Status = AcfStatus.Unverified;
                        orphan.DamageReason = (otherStillHasFiles
                                ? Loc.T("reason.dupStillThere", installedElsewhere.Library)
                                : Loc.T("reason.dupStale", installedElsewhere.Library))
                            + appIdNote;
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

        private static AcfInfo FindAcfForAppId(List<AcfInfo> all, string appid)
        {
            if (string.IsNullOrEmpty(appid)) return null;
            foreach (AcfInfo a in all)
                if (a.AppId == appid && a.Path.EndsWith(".acf", StringComparison.OrdinalIgnoreCase)) return a;
            return null;
        }

        /// <summary>
        /// 在别的库里找同一个 AppID 的 ACF。只要存在，就说明该 AppID 并不是“缺少 ACF”，
        /// 而是重复/搬盘残留，需要人工处理，不能自动再生成一份。
        /// </summary>
        private static AcfInfo FindAcfOfAppIdInOtherLibrary(string appid, string excludeLibrary)
        {
            if (string.IsNullOrEmpty(appid)) return null;
            foreach (string lib in libraries)
            {
                if (string.Equals(lib, excludeLibrary, StringComparison.OrdinalIgnoreCase)) continue;
                string f = Path.Combine(lib, "steamapps", "appmanifest_" + appid + ".acf");
                if (!File.Exists(f)) continue;
                try { return ParseAcf(f); } catch { }
            }
            return null;
        }

        public static AcfInfo FindAcf(string appid)
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

        private static string FindSteamCmd()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string[] rel = { "steamcmd.exe", "steamcmd\\steamcmd.exe" };
            foreach (string n in rel)
            {
                string p = Path.Combine(baseDir, n);
                if (File.Exists(p)) return p;
            }
            string pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrEmpty(pathEnv))
            {
                foreach (string dir in pathEnv.Split(';'))
                {
                    if (string.IsNullOrEmpty(dir)) continue;
                    string p = Path.Combine(dir.Trim(), "steamcmd.exe");
                    if (File.Exists(p)) return p;
                }
            }
            string[] cand = {
                Path.Combine(steamPath, "steamcmd", "steamcmd.exe"),
                @"C:\steamcmd\steamcmd.exe",
                @"D:\steamcmd\steamcmd.exe"
            };
            foreach (string c in cand) if (File.Exists(c)) return c;
            return null;
        }

        private sealed class TimedWebClient : WebClient
        {
            private int _timeout;
            public TimedWebClient(int timeout) { _timeout = timeout; }
            protected override WebRequest GetWebRequest(Uri address)
            {
                WebRequest r = base.GetWebRequest(address);
                if (r != null) r.Timeout = _timeout;
                return r;
            }
        }

        private static string DownloadSteamCmd()
        {
            string dir = Path.Combine(Path.GetTempPath(), "steamcmd");
            string exe = Path.Combine(dir, "steamcmd.exe");
            if (File.Exists(exe)) return exe;
            try
            {
                Directory.CreateDirectory(dir);
                string zip = Path.Combine(dir, "steamcmd.zip");
                using (TimedWebClient wc = new TimedWebClient(30000))
                {
                    wc.DownloadFile("https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip", zip);
                }
                System.IO.Compression.ZipFile.ExtractToDirectory(zip, dir);
                if (File.Exists(exe)) return exe;
            }
            catch { }
            return null;
        }

        private static string ExtractVdfBlock(string text, string appid)
        {
            string marker = "\"" + appid + "\"";
            int idx = text.IndexOf(marker, StringComparison.Ordinal);
            if (idx < 0) return null;
            int brace = text.IndexOf('{', idx);
            if (brace < 0) return null;
            int depth = 0;
            for (int i = brace; i < text.Length; i++)
            {
                if (text[i] == '{') depth++;
                else if (text[i] == '}')
                {
                    depth--;
                    if (depth == 0) return text.Substring(idx, i - idx + 1);
                }
            }
            return null;
        }

        // 用 steamcmd 拉取 app_info_print，得到 name / installdir / buildid / 每个 depot 的
        // manifest GID、体积、平台、语言、DLC 标记。失败返回 null。
        private static AppMeta TryMetaFromSteamCmd(string appid)
        {
            string exe = FindSteamCmd();
            if (exe == null) exe = DownloadSteamCmd();
            if (exe == null) return null;

            string output = RunProcessCapture(exe, "+login anonymous +app_info_print " + appid + " +quit", 90000);
            if (string.IsNullOrEmpty(output)) return null;

            string block = ExtractVdfBlock(output, appid);
            if (block == null) return null;
            VdfNode root = ParseVdf(block);
            if (root == null) return null;
            VdfNode depots = root.Find("depots");
            if (depots == null) return null;

            AppMeta meta = new AppMeta();
            meta.AppId = appid;
            meta.Source = "steamcmd app_info_print";
            VdfNode common = root.Find("common");
            if (common != null) meta.Name = common.Get("name") ?? "";
            VdfNode cfg = root.Find("config");
            if (cfg != null) meta.InstallDir = cfg.Get("installdir") ?? "";

            VdfNode branches = depots.Find("branches");
            if (branches != null)
            {
                VdfNode pubBranch = branches.Find("public");
                if (pubBranch != null)
                {
                    string b = pubBranch.Get("buildid");
                    long bv;
                    if (b != null && long.TryParse(b, out bv)) meta.BuildId = bv;
                }
            }

            foreach (VdfNode d in depots.Children)
            {
                long dummy;
                if (!long.TryParse(d.Key, out dummy)) continue;   // 跳过 branches 等非 depot 节点
                DepotMeta dm = new DepotMeta();
                dm.Id = d.Key;
                VdfNode mans = d.Find("manifests");
                VdfNode pubMan = mans != null ? mans.Find("public") : null;
                if (pubMan != null)
                {
                    // app_info_print 里 manifests.public 既可能是字符串（老格式），
                    // 也可能是含 gid/size/download 的子表（新格式），两种都要认。
                    if (pubMan.HasValue) dm.Manifest = pubMan.Value ?? "";
                    else
                    {
                        dm.Manifest = pubMan.Get("gid") ?? "";
                        long v;
                        if (long.TryParse(pubMan.Get("size"), out v)) dm.Size = v;
                        if (long.TryParse(pubMan.Get("download"), out v)) dm.Download = v;
                    }
                }
                long maxsize;
                if (dm.Size < 0 && long.TryParse(d.Get("maxsize"), out maxsize) && maxsize > 0) dm.Size = maxsize;
                dm.DlcAppId = d.Get("dlcappid") ?? "";
                dm.Shared = d.Find("sharedinstall") != null;
                dm.SharedOwner = d.Get("depotfromapp") ?? "";
                dm.SystemDefined = d.Find("systemdefined") != null;
                VdfNode dcfg = d.Find("config");
                if (dcfg != null)
                {
                    dm.OsList = dcfg.Get("oslist") ?? "";
                    dm.Language = dcfg.Get("language") ?? "";
                }
                meta.Depots.Add(dm);
            }
            return meta.Depots.Count > 0 ? meta : null;
        }

        // 运行外部程序并同时读走 stdout/stderr。
        // 注意：两个流都必须读，否则 steamcmd 写满 stderr 缓冲区后不会退出，
        // 旧实现只读 stdout 再 ReadToEnd，遇到这种情况会永久卡死界面。
        private static string RunProcessCapture(string exe, string arguments, int timeoutMs)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = exe;
                psi.Arguments = arguments;
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.CreateNoWindow = true;
                using (Process p = Process.Start(psi))
                {
                    Task<string> outTask = p.StandardOutput.ReadToEndAsync();
                    Task<string> errTask = p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { p.Kill(); } catch { }
                        return null;
                    }
                    string stdout = outTask.Result;
                    try { errTask.Wait(3000); } catch { }
                    return stdout;
                }
            }
            catch { return null; }
        }

        private static bool HasUsableDepots(AppMeta meta)
        {
            if (meta == null) return false;
            foreach (DepotMeta d in meta.Depots) if (d.HasManifest) return true;
            return false;
        }

        /// <summary>
        /// 获取某个 AppID 的 depot/buildid 元数据。顺序：Steam 本地缓存 appinfo.vdf（离线，最贴合本机）
        /// → steamcmd app_info_print（联网，信息最全）→ content_log.txt（最近一次安装记录）。
        /// 成功返回元数据，失败返回 null 并给出 error。
        /// </summary>
        private static AppMeta ResolveAppMeta(string appid, out string error, out string log)
        {
            error = null;
            System.Text.StringBuilder sb = new System.Text.StringBuilder();

            AppMeta meta = AppInfoCache.Get(appid);
            if (HasUsableDepots(meta))
            {
                sb.AppendLine(Loc.T("log.depotSource", Loc.T("src.appinfo") + " (buildid=" + meta.BuildId + ")"));
                if (meta.BuildId <= 0)
                {
                    AppMeta sc = TryMetaFromSteamCmd(appid);
                    if (sc != null && sc.BuildId > 0)
                    {
                        meta.BuildId = sc.BuildId;
                        sb.AppendLine(Loc.T("log.depotSource", Loc.T("src.steamcmd", sc.BuildId)));
                    }
                }
                log = sb.ToString();
                return meta;
            }

            meta = TryMetaFromSteamCmd(appid);
            if (HasUsableDepots(meta))
            {
                sb.AppendLine(Loc.T("log.depotSource", Loc.T("src.steamcmd", meta.BuildId)));
                log = sb.ToString();
                return meta;
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
                sb.AppendLine(Loc.T("log.depotSource", Loc.T("src.contentLog")));
                log = sb.ToString();
                return fromLog;
            }

            log = sb.ToString();
            error = Loc.T("err.noDepotInfo", appid,
                AppInfoCache.Available ? "" : Loc.T("err.cacheUnavailableNote", AppInfoCache.LoadError));
            return null;
        }

        /// <summary>manifest GID 是否看起来有效（Steam 用的是 64 位无符号数，明显的占位值 0/1 视为无效）。</summary>
        private static bool LooksLikeManifest(string manifest)
        {
            long v;
            if (string.IsNullOrEmpty(manifest) || !long.TryParse(manifest, out v)) return false;
            return v > 1;
        }

        /// <summary>把 depot 体积未知的项按实测总体积分摊，避免 ACF 里出现 0。</summary>
        private static void FillUnknownSizes(List<DepotMeta> depots, long totalBytes)
        {
            long known = 0;
            int unknown = 0;
            foreach (DepotMeta d in depots)
            {
                if (d.Size > 0) known += d.Size;
                else unknown++;
            }
            if (unknown == 0) return;
            long rest = totalBytes - known;
            if (rest < 0) rest = 0;
            long each = rest / unknown;
            foreach (DepotMeta d in depots) if (d.Size <= 0) d.Size = each;
        }

        /// <summary>读取 Steam 界面语言（用于选择语言包 depot 与写入 ACF 的 language 字段）。</summary>
        public static string GetSteamLanguage()
        {
            try
            {
                object v = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "Language", null);
                if (v != null && v.ToString().Length > 0) return v.ToString();
            }
            catch { }
            try
            {
                string ud = Path.Combine(steamPath, "userdata");
                if (Directory.Exists(ud))
                {
                    foreach (string dir in Directory.GetDirectories(ud))
                    {
                        string cfg = Path.Combine(dir, "config", "localconfig.vdf");
                        if (!File.Exists(cfg)) continue;
                        VdfNode root = ParseVdf(File.ReadAllText(cfg));
                        if (root == null) continue;
                        VdfNode store = root.Find("UserLocalConfigStore");
                        if (store == null) continue;
                        VdfNode steam = store.Find("Steam");
                        if (steam == null) continue;
                        string lang = steam.Get("language");
                        if (!string.IsNullOrEmpty(lang)) return lang;
                    }
                }
            }
            catch { }
            return "schinese";
        }

        /// <summary>生成 ACF 文本。StateFlags=4 表示 Steam 认为“已完整安装”。</summary>
        private static AcfSpec MakeSpec(AcfInfo basis, AppMeta meta, List<DepotMeta> depots, long buildid, long sizeOnDisk, long stateFlags, string language)
        {
            AcfSpec spec = new AcfSpec();
            spec.AppId = basis.AppId;
            spec.Name = !string.IsNullOrEmpty(basis.Name) ? basis.Name
                      : (meta != null && meta.Name.Length > 0 ? meta.Name : basis.InstallDir);
            spec.InstallDir = basis.InstallDir;
            spec.LauncherPath = !string.IsNullOrEmpty(basis.LauncherPath) ? basis.LauncherPath
                              : Path.Combine(steamPath, "steam.exe");
            spec.LastOwner = !string.IsNullOrEmpty(basis.LastOwner) ? basis.LastOwner : GetCurrentSteamId();
            spec.Language = !string.IsNullOrEmpty(language) ? language : basis.Language;
            spec.StateFlags = stateFlags;
            spec.SizeOnDisk = sizeOnDisk;
            spec.BuildId = buildid;
            spec.TargetBuildId = buildid;
            spec.LastUpdated = AcfWriter.NowUnix();
            spec.Depots = depots;
            spec.SharedDepots = AcfWriter.SharedDepotsOf(meta, basis.SharedDepots);
            spec.InstallScripts = basis.InstallScripts;
            return spec;
        }

        // 修复已存在的（损坏的）ACF。成功返回 null，失败返回错误信息（预览见 LastPreview，日志见 LastReport）。
        public static string RepairExisting(string appid, bool dryRun)
        {
            LastReport = "";
            LastPreview = "";

            AcfInfo acf = FindAcf(appid);
            if (acf == null)
                return Loc.T("err.noAcfForAppId", appid);

            string err, log;
            AppMeta meta = ResolveAppMeta(appid, out err, out log);
            if (meta == null) return err;

            long buildid = meta.BuildId;
            if (buildid <= 0) buildid = acf.TargetBuildId > 0 ? acf.TargetBuildId : acf.BuildId;
            if (buildid <= 0)
                return Loc.T("err.noBuildId", appid) + "\n\n" + log;

            // 用目录内容校验决定 StateFlags：文件完整才敢写“已完整安装(4)”
            string folder = Path.Combine(acf.Library, "steamapps", "common", acf.InstallDir);
            ContentCheck chk = ContentVerifier.Check(folder, meta, acf.SizeOnDisk, Loc.T("src.oldAcf"));

            if (chk.Verdict != ContentVerdict.Complete)
            {
                return Loc.T("err.refuseRepair", chk.Reason, folder, acf.DamageReason) + "\n"
                     + Loc.T("err.repairHint");
            }

            // depot 组合沿用原 ACF 的集合（那才是这台机器实际装过的 depot/语言/DLC 组合）。
            // 关键：原 ACF 里记录的 manifest GID 是“磁盘上这份内容”的清单，可能比缓存里的
            // 当前 public 版本旧（游戏没更新过）。这种情况必须保留原值，否则等于告诉 Steam
            // 磁盘上是另一个版本，反而会触发重新下载。只有在原值缺失/无效时才用缓存补。
            List<string> notes = new List<string>();
            List<DepotMeta> depots = new List<DepotMeta>();
            int refreshed = 0, kept = 0;
            foreach (KeyValuePair<string, DepotInfo> kv in acf.Depots)
            {
                DepotMeta live = meta.FindDepot(kv.Key);
                long recordedSize;
                long.TryParse(kv.Value.Size, out recordedSize);
                bool manifestValid = LooksLikeManifest(kv.Value.Manifest);

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
                if (manifestValid)
                {
                    use.Manifest = kv.Value.Manifest;
                    use.Size = recordedSize;
                    kept++;
                    if (live != null && live.HasManifest && live.Manifest != kv.Value.Manifest)
                        notes.Add(Loc.T("log.depotOldManifest", kv.Key, kv.Value.Manifest, live.Manifest));
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
                    use.Size = recordedSize;
                    notes.Add(Loc.T("log.depotKeptInvalid", kv.Key));
                }
                depots.Add(use);
            }

            // 原 ACF 缺失、但缓存里该 AppID 有、且是本机当前需要的 depot（例如语言包）→ 不自动添加，
            // 避免凭空多出 Steam 未安装的 depot。只有当原 ACF 一个 depot 都没有时才整体重建。
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

            AcfSpec spec = MakeSpec(acf, meta, depots, buildid, chk.ActualBytes, 4, acf.Language);
            string content = AcfWriter.Build(spec);
            LastPreview = content;

            LastReport = Loc.T("log.appid", appid, spec.Name) + "\n"
                + Loc.T("log.folder", folder) + "\n"
                + Loc.T("log.contentCheck", chk.Reason) + "\n"
                + Loc.T("log.sizes", chk.ActualBytes, buildid, 4) + "\n"
                + string.Join("\n", notes.ToArray()) + "\n"
                + log
                + Loc.T("log.writeBackup", acf.Path, acf.Path + ".bak");

            if (dryRun) return null;

            string bak = acf.Path + ".bak";
            File.Copy(acf.Path, bak, true);
            File.WriteAllText(acf.Path, content, new UTF8Encoding(false));
            ExportCopy(acf.AppId, content);
            return null;
        }

        /// <summary>
        /// 为“确认文件完整、只缺 ACF”的游戏目录生成 ACF。
        /// 写入的是 Steam 本地缓存里当前 public 分支的 buildid 与每个 depot 的 manifest GID，
        /// 因此 Steam 校验时能对上号，不会因为 buildid/manifest 不对而重新下载。
        /// 成功返回 null；失败返回错误信息（预览见 LastPreview，日志见 LastReport）。
        /// </summary>
        public static string GenerateAcf(AcfInfo orphan, bool dryRun)
        {
            LastReport = "";
            LastPreview = "";

            string appid = orphan.AppId;
            if (string.IsNullOrEmpty(appid))
                return Loc.T("err.noAppId");

            string err, log;
            AppMeta meta = ResolveAppMeta(appid, out err, out log);
            if (meta == null) return err;

            // 先校验内容：只有“游戏文件确实完整”的目录才允许生成 ACF
            AcfInfo tpl = FindAcfByInstallDir(orphan.InstallDir);
            long reference = tpl != null && tpl.SizeOnDisk > 0 ? tpl.SizeOnDisk : 0;
            ContentCheck chk = ContentVerifier.Check(orphan.Path, meta, reference,
                reference > 0 ? Loc.T("src.templateAcf") : "");

            string folder = orphan.Path;
            if (chk.Verdict == ContentVerdict.Empty)
                return Loc.T("err.refuseEmpty", chk.Reason, folder);
            if (chk.Verdict == ContentVerdict.Incomplete)
                return Loc.T("err.refuseIncomplete", chk.Reason, folder);
            if (chk.Verdict == ContentVerdict.Unverified)
                return Loc.T("err.refuseUnverified", chk.Reason, folder);

            if (meta.BuildId <= 0)
                return Loc.T("err.noBuildId", appid) + "\n\n" + log;

            string acfPath = Path.Combine(orphan.Library, "steamapps", "appmanifest_" + appid + ".acf");
            if (File.Exists(acfPath))
                return Loc.T("err.targetAcfExists", acfPath);

            // 同一个 AppID 在别的库已经有 ACF：这不是“缺少 ACF”，而是重复/搬盘残留，必须人工确认
            AcfInfo elsewhere = FindAcfOfAppIdInOtherLibrary(appid, orphan.Library);
            if (elsewhere != null)
            {
                string otherFolder = Path.Combine(elsewhere.Library, "steamapps", "common", elsewhere.InstallDir);
                bool otherStillHasFiles = Directory.Exists(otherFolder);
                return Loc.T("err.duplicateOtherLib", appid, elsewhere.Library, elsewhere.Path,
                    otherFolder + (otherStillHasFiles ? Loc.T("err.duplicateFolderExists") : Loc.T("err.duplicateFolderMissing")));
            }

            List<string> notes = new List<string>();
            List<DepotMeta> depots;
            if (tpl != null && tpl.Depots.Count > 0)
            {
                depots = new List<DepotMeta>();
                foreach (KeyValuePair<string, DepotInfo> kv in tpl.Depots)
                {
                    DepotMeta live = meta.FindDepot(kv.Key);
                    if (live != null && live.HasManifest) depots.Add(live);
                    else
                    {
                        DepotMeta keep = new DepotMeta();
                        keep.Id = kv.Key;
                        keep.Manifest = kv.Value.Manifest;
                        long sz;
                        if (long.TryParse(kv.Value.Size, out sz)) keep.Size = sz;
                        keep.DlcAppId = kv.Value.DlcAppId;
                        depots.Add(keep);
                    }
                }
                notes.Add(Loc.T("log.depotFromTemplate", depots.Count));
            }
            else
            {
                depots = AcfWriter.PickInstalledDepots(meta, chk.ActualBytes, GetSteamLanguage(), notes);
                notes.Add(Loc.T("log.depotPicked", depots.Count));
            }
            if (depots.Count == 0)
                return Loc.T("err.noUsableDepots", appid);
            FillUnknownSizes(depots, chk.ActualBytes);

            AcfInfo basis = new AcfInfo();
            basis.AppId = appid;
            basis.InstallDir = orphan.InstallDir;
            basis.Library = orphan.Library;
            basis.Name = !string.IsNullOrEmpty(orphan.Name) ? orphan.Name : (meta.Name.Length > 0 ? meta.Name : orphan.InstallDir);
            basis.LauncherPath = Path.Combine(steamPath, "steam.exe");
            basis.LastOwner = GetCurrentSteamId();
            basis.Language = GetSteamLanguage();
            if (tpl != null)
            {
                if (!string.IsNullOrEmpty(tpl.Name)) basis.Name = tpl.Name;
                if (!string.IsNullOrEmpty(tpl.LastOwner)) basis.LastOwner = tpl.LastOwner;
                if (!string.IsNullOrEmpty(tpl.LauncherPath)) basis.LauncherPath = tpl.LauncherPath;
                basis.InstallScripts = tpl.InstallScripts;
            }

            AcfSpec spec = MakeSpec(basis, meta, depots, meta.BuildId, chk.ActualBytes, 4, basis.Language);
            string content = AcfWriter.Build(spec);
            LastPreview = content;

            LastReport = Loc.T("log.appid", appid, spec.Name) + "\n"
                + Loc.T("log.folder", folder) + "\n"
                + Loc.T("log.contentCheck", chk.Reason) + "\n"
                + Loc.T("log.sizes", chk.ActualBytes, meta.BuildId, 4) + "\n"
                + string.Join("\n", notes.ToArray()) + "\n"
                + log
                + Loc.T("log.writeNew", acfPath);

            if (dryRun) return null;

            File.WriteAllText(acfPath, content, new UTF8Encoding(false));
            ExportCopy(appid, content);
            return null;
        }

        // 统一入口：根据状态分发
        public static string RepairByInfo(AcfInfo g, bool dryRun)
        {
            if (g.Status == AcfStatus.MissingAcf || g.Status == AcfStatus.Unverified)
                return GenerateAcf(g, dryRun);
            if (g.Status == AcfStatus.Damaged)
                return RepairExisting(g.AppId, dryRun);

            LastReport = "";
            LastPreview = "";
            return Loc.T("err.notActionable", g.DamageReason);
        }


        

        public static string ExportDir
        {
            get
            {
                // 默认放在 exe 同目录的 export 下；可用环境变量覆盖（回归测试用它避免污染真实导出目录）
                string dir = Environment.GetEnvironmentVariable("STEAM_ACF_EXPORT_DIR");
                if (!string.IsNullOrEmpty(dir)) return dir;
                return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "export");
            }
        }

        private static void ExportCopy(string appid, string content)
        {
            string dir = ExportDir;
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "appmanifest_" + appid + ".acf"), content, new UTF8Encoding(false));
        }
    }

    // ---------------- GUI ----------------
    internal sealed class MainForm : Form
    {
        private DataGridView grid;
        private Label statusLabel;
        private Label steamLabel;
        private Label langLabel;
        private ComboBox langCombo;
        private Button rescanBtn;
        private Button repairBtn;
        private Button repairAllBtn;
        private Button exportBtn;
        private Button exportAllBtn;
        private Button openDirBtn;
        private List<AcfInfo> games = new List<AcfInfo>();
        private bool switchingLanguage;

        public MainForm()
        {
            Width = 1180;
            Height = 680;
            MinimumSize = new Size(980, 520);
            StartPosition = FormStartPosition.CenterScreen;

            BuildUi();
            ApplyLanguage();
            Load += delegate { Rescan(); };
        }

        private void BuildUi()
        {
            Panel top = new Panel();
            top.Dock = DockStyle.Top;
            top.Height = 46;
            top.Padding = new Padding(8, 8, 8, 4);

            steamLabel = new Label();
            steamLabel.AutoSize = true;
            steamLabel.Location = new Point(8, 14);
            steamLabel.Font = new Font("Microsoft YaHei", 9F);

            // 语言选择放在「重新扫描」的左边，紧挨着它
            rescanBtn = new Button();
            rescanBtn.Dock = DockStyle.Right;
            rescanBtn.Width = 110;
            rescanBtn.Click += delegate { Rescan(); };

            langCombo = new ComboBox();
            langCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            langCombo.Width = 130;
            langCombo.FlatStyle = FlatStyle.System;
            foreach (LanguageInfo li in Loc.Languages) langCombo.Items.Add(li.NativeName);
            // 注意：这里不设 SelectedIndex —— 交给 ApplyLanguage() 在“抑制变更”状态下设置，
            // 否则控件初始化会被当成用户切换语言，把当前语言误写进 settings.ini。
            langCombo.SelectedIndexChanged += delegate
            {
                if (switchingLanguage || langCombo.SelectedIndex < 0) return;
                Loc.SetLanguage(Loc.Languages[langCombo.SelectedIndex].Code);
                Loc.SaveLanguage();
                ApplyLanguage();
            };

            langLabel = new Label();
            langLabel.AutoSize = true;
            langLabel.Margin = new Padding(0, 6, 4, 0);
            langLabel.Font = new Font("Microsoft YaHei", 9F);

            FlowLayoutPanel topRight = new FlowLayoutPanel();
            topRight.Dock = DockStyle.Right;
            topRight.AutoSize = true;
            topRight.FlowDirection = FlowDirection.LeftToRight;
            topRight.WrapContents = false;
            topRight.Padding = new Padding(0, 6, 0, 0);
            topRight.Controls.Add(langLabel);
            topRight.Controls.Add(langCombo);
            topRight.Controls.Add(rescanBtn);

            // 「重新扫描」与左侧语言下拉框必须等高：ComboBox 的高度由字体决定（PreferredHeight，无法手动加高），
            // 所以以它为准把按钮设成同样高度，并用 MinimumSize 防止布局过程把按钮压扁
            // （压扁后中文字会贴边，看起来像被遮挡）。
            int rowHeight = langCombo.PreferredHeight;
            rescanBtn.AutoSize = false;
            rescanBtn.MinimumSize = new Size(0, rowHeight);
            rescanBtn.Height = rowHeight;
            rescanBtn.Margin = new Padding(0);
            langCombo.Margin = new Padding(0, 0, 6, 0);
            langLabel.Margin = new Padding(0, Math.Max(0, (rowHeight - langLabel.PreferredHeight) / 2 + 1), 4, 0);

            top.Controls.Add(steamLabel);
            top.Controls.Add(topRight);

            grid = new DataGridView();
            grid.Dock = DockStyle.Fill;
            grid.ReadOnly = true;
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.AllowUserToResizeRows = false;
            grid.RowHeadersVisible = false;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.MultiSelect = false;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            grid.BackgroundColor = Color.White;
            grid.Font = new Font("Microsoft YaHei", 9F);
            grid.ColumnHeadersHeight = 30;
            grid.ShowCellToolTips = true;

            grid.Columns.Add("colStatus", "");
            grid.Columns.Add("colAppId", "");
            grid.Columns.Add("colName", "");
            grid.Columns.Add("colLib", "");
            grid.Columns.Add("colFlags", "");
            grid.Columns.Add("colSize", "");
            grid.Columns.Add("colBuild", "");
            grid.Columns.Add("colNote", "");

            // StateFlags 现在显示成 "4 (FullyInstalled)" 这种带英文说明的形式，
            // 用 AllCells 让这一列按内容自动加宽，保证文字完整显示而不是被截断。
            grid.Columns["colStatus"].FillWeight = 80;
            grid.Columns["colAppId"].FillWeight = 55;
            grid.Columns["colAppId"].AutoSizeMode = DataGridViewAutoSizeColumnMode.AllCells;
            grid.Columns["colName"].FillWeight = 130;
            grid.Columns["colLib"].FillWeight = 60;
            grid.Columns["colLib"].AutoSizeMode = DataGridViewAutoSizeColumnMode.AllCells;
            grid.Columns["colFlags"].FillWeight = 130;
            grid.Columns["colFlags"].AutoSizeMode = DataGridViewAutoSizeColumnMode.AllCells;
            grid.Columns["colFlags"].MinimumWidth = 170;
            grid.Columns["colSize"].FillWeight = 60;
            grid.Columns["colSize"].AutoSizeMode = DataGridViewAutoSizeColumnMode.AllCells;
            grid.Columns["colBuild"].FillWeight = 55;
            grid.Columns["colBuild"].AutoSizeMode = DataGridViewAutoSizeColumnMode.AllCells;
            grid.Columns["colNote"].FillWeight = 210;

            Panel bottom = new Panel();
            bottom.Dock = DockStyle.Bottom;
            bottom.Height = 52;
            bottom.Padding = new Padding(8, 6, 8, 8);

            statusLabel = new Label();
            statusLabel.Dock = DockStyle.Fill;
            statusLabel.TextAlign = ContentAlignment.MiddleLeft;
            statusLabel.Font = new Font("Microsoft YaHei", 9F);

            repairBtn = MakeButton("");
            repairAllBtn = MakeButton("");
            exportBtn = MakeButton("");
            exportAllBtn = MakeButton("");
            openDirBtn = MakeButton("");

            repairBtn.Click += delegate { RepairSelected(); };
            repairAllBtn.Click += delegate { RepairAll(); };
            exportBtn.Click += delegate { ExportSelected(); };
            exportAllBtn.Click += delegate { ExportAllOk(); };
            openDirBtn.Click += delegate { OpenExportDir(); };

            FlowLayoutPanel btns = new FlowLayoutPanel();
            btns.Dock = DockStyle.Right;
            btns.AutoSize = true;
            btns.FlowDirection = FlowDirection.LeftToRight;
            btns.WrapContents = false;
            btns.Controls.Add(repairBtn);
            btns.Controls.Add(repairAllBtn);
            btns.Controls.Add(exportBtn);
            btns.Controls.Add(exportAllBtn);
            btns.Controls.Add(openDirBtn);

            bottom.Controls.Add(statusLabel);
            bottom.Controls.Add(btns);

            Controls.Add(grid);
            Controls.Add(bottom);
            Controls.Add(top);
        }

        /// <summary>把当前语言应用到界面上的所有文字（切换语言后调用）。</summary>
        private void ApplyLanguage()
        {
            switchingLanguage = true;
            try
            {
                Text = Loc.T("app.title");
                steamLabel.Text = Loc.T("ui.steamRoot", Core.steamPath) + "   |   " + Core.AppInfoCacheStatus;
                langLabel.Text = Loc.T("ui.language");
                rescanBtn.Text = Loc.T("ui.rescan");
                repairBtn.Text = Loc.T("ui.repairOne");
                repairAllBtn.Text = Loc.T("ui.repairAll");
                exportBtn.Text = Loc.T("ui.exportOne");
                exportAllBtn.Text = Loc.T("ui.exportAllOk");
                openDirBtn.Text = Loc.T("ui.openExport");
                if (statusLabel.Text.Length == 0 || !scanning) statusLabel.Text = Loc.T("ui.ready");

                grid.Columns["colStatus"].HeaderText = Loc.T("col.status");
                grid.Columns["colAppId"].HeaderText = Loc.T("col.appid");
                grid.Columns["colName"].HeaderText = Loc.T("col.name");
                grid.Columns["colLib"].HeaderText = Loc.T("col.library");
                grid.Columns["colFlags"].HeaderText = Loc.T("col.stateflags");
                grid.Columns["colSize"].HeaderText = Loc.T("col.size");
                grid.Columns["colBuild"].HeaderText = Loc.T("col.buildid");
                grid.Columns["colNote"].HeaderText = Loc.T("col.note");
                grid.Columns["colFlags"].HeaderCell.ToolTipText = Loc.StateFlagsLegend();
                grid.Columns["colStatus"].HeaderCell.ToolTipText = Loc.T("tip.stateflags.intro");

                int idx = Loc.IndexOf(Loc.CurrentCode);
                if (langCombo.SelectedIndex != idx) langCombo.SelectedIndex = idx;

                FillGrid();   // 状态与说明列的文字也要跟着换语言
            }
            finally { switchingLanguage = false; }
        }

        private Button MakeButton(string text)
        {
            Button b = new Button();
            b.Text = text;
            b.AutoSize = true;
            b.Height = 30;
            b.Margin = new Padding(0, 0, 6, 0);
            b.Font = new Font("Microsoft YaHei", 9F);
            return b;
        }

        private void SetBusy(bool busy)
        {
            repairBtn.Enabled = !busy;
            repairAllBtn.Enabled = !busy;
            exportBtn.Enabled = !busy;
            exportAllBtn.Enabled = !busy;
            rescanBtn.Enabled = !busy;
        }

        private bool scanning;

        private void Rescan()
        {
            if (scanning) return;
            scanning = true;
            SetBusy(true);
            statusLabel.Text = Loc.T("ui.scanning");
            Task.Run(delegate
            {
                List<AcfInfo> list = null;
                string errText = null;
                try { list = Core.ScanGames(); }
                catch (Exception ex) { errText = ex.Message; }
                try
                {
                    BeginInvoke((Action)delegate
                    {
                        scanning = false;
                        SetBusy(false);                        if (errText != null)
                        {
                            statusLabel.Text = Loc.T("ui.scanFailed", errText);
                            MessageBox.Show(Loc.T("ui.scanFailed", errText), Loc.T("dlg.failTitle"),
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
                            return;
                        }
                        games = list;
                        FillGrid();
                        statusLabel.Text = Loc.T("ui.summary", games.Count,
                            Count(AcfStatus.InstalledOk), Count(AcfStatus.Damaged), Count(AcfStatus.MissingAcf),
                            Count(AcfStatus.Unverified), Count(AcfStatus.ResidueFolder) + Count(AcfStatus.EmptyFolder));
                    });
                }
                catch { scanning = false; }   // 界面已关闭等情况：别把状态卡在“正在扫描”
            });
        }

        private int Count(AcfStatus st)
        {
            int n = 0;
            foreach (AcfInfo g in games) if (g.Status == st) n++;
            return n;
        }

        private static string StatusTextOf(AcfStatus st)
        {
            return Core.StatusTextOf(st);
        }

        private void FillGrid()
        {
            grid.Rows.Clear();
            foreach (AcfInfo g in games)
            {
                // 孤儿目录用“游戏名（目录 文件夹名）”显示，避免只看到游戏名却不知道对应哪个目录
                string nameText = g.Name;
                if (g.Folder.Length > 0 && !string.Equals(g.Name, g.InstallDir, StringComparison.OrdinalIgnoreCase))
                    nameText = Loc.T("ui.nameWithDir", g.Name, g.InstallDir);
                // StateFlags 只在真有 ACF 时显示，并带上英文含义：4 (FullyInstalled)
                bool hasAcf = g.Folder.Length == 0;   // 孤儿目录没有 ACF，不显示 StateFlags/buildid
                string flagsText = hasAcf ? Loc.StateFlagsText(g.StateFlags) : "";
                string buildText = hasAcf ? g.BuildId.ToString() : "";
                int idx = grid.Rows.Add(StatusTextOf(g.Status), g.AppId, nameText, Path.GetFileName(g.Library),
                    flagsText, FormatSize(g.SizeOnDisk), buildText, g.DamageReason);
                DataGridViewRow row = grid.Rows[idx];
                // 完整文本放进悬停提示，窗口很窄时也能看到没有截断的内容
                row.Cells["colName"].ToolTipText = nameText;
                row.Cells["colFlags"].ToolTipText = hasAcf
                    ? Loc.StateFlagsText(g.StateFlags) + "\n\n" + Loc.StateFlagsLegend()
                    : Loc.T("tip.stateflags.intro");
                row.Cells["colNote"].ToolTipText = g.DamageReason;
                switch (g.Status)
                {
                    case AcfStatus.InstalledOk:
                        row.DefaultCellStyle.BackColor = Color.FromArgb(235, 250, 235);
                        break;
                    case AcfStatus.Damaged:
                        row.DefaultCellStyle.BackColor = Color.FromArgb(255, 240, 230);
                        break;
                    case AcfStatus.MissingAcf:
                        row.DefaultCellStyle.BackColor = Color.FromArgb(255, 246, 220);
                        break;
                    case AcfStatus.Unverified:
                        row.DefaultCellStyle.BackColor = Color.FromArgb(232, 242, 255);
                        break;
                    default:
                        // 残留目录 / 空文件夹：不是待修复项，用灰色明确区分
                        row.DefaultCellStyle.BackColor = Color.FromArgb(242, 242, 242);
                        row.DefaultCellStyle.ForeColor = Color.FromArgb(120, 120, 120);
                        break;
                }
            }
            // 保证 StateFlags 列足够宽：按“当前语言下实际显示的文本”实测宽度（中文等全角字符比英文宽），
            // 这样切到任何语言都不会把 “6 (需要更新 | 已完整安装)” 之类截断。
            int need = TextRenderer.MeasureText("88888888 (" + Loc.T("col.stateflags") + ")", grid.Font).Width + 28;
            foreach (DataGridViewRow r in grid.Rows)
            {
                string cellText = r.Cells["colFlags"].Value as string;
                if (string.IsNullOrEmpty(cellText)) continue;
                int w = TextRenderer.MeasureText(cellText, grid.Font).Width + 22;
                if (w > need) need = w;
            }
            grid.Columns["colFlags"].MinimumWidth = Math.Max(150, need);
        }

        private string FormatSize(long bytes)
        {
            // 统一用 AcfFormat：小目录会显示 “40 B / 2.7 KB”，大目录显示 GB
            return AcfFormat.Bytes(bytes);
        }

        private AcfInfo Selected()
        {
            if (grid.CurrentRow == null || grid.CurrentRow.Index < 0) return null;
            int i = grid.CurrentRow.Index;
            if (i < 0 || i >= games.Count) return null;
            return games[i];
        }

        /// <summary>
        /// 自检钩子（命令行 gui-selftest 使用）：不显示窗口，只构建界面并用给定数据填充表格，
        /// 用来在无人值守的情况下确认界面代码不会崩。返回表格行数。
        /// </summary>
        public int SelfTest(List<AcfInfo> list)
        {
            ApplyLanguage();
            games = list != null ? list : new List<AcfInfo>();
            FillGrid();
            return grid.Rows.Count;
        }

        /// <summary>
        /// 自检钩子 2：走一遍真正的异步扫描流程（后台 Task + BeginInvoke 回填表格），
        /// 用 DoEvents 代替消息循环，用来确认“重新扫描”不会卡住或抛异常。
        /// </summary>
        public int SelfTestAsync(int timeoutMs)
        {
            IntPtr forceHandle = Handle;   // 没有窗口句柄时 BeginInvoke 会失败，先强制创建
            if (forceHandle == IntPtr.Zero) throw new InvalidOperationException("无法创建窗口句柄");
            Rescan();
            DateTime deadline = DateTime.Now.AddMilliseconds(timeoutMs);
            while (scanning && DateTime.Now < deadline)
            {
                Application.DoEvents();
                System.Threading.Thread.Sleep(20);
            }
            Application.DoEvents();
            return grid.Rows.Count;
        }

        private void RepairSelected()
        {
            AcfInfo g = Selected();
            if (g == null) { MessageBox.Show(Loc.T("dlg.selectRow")); return; }
            if (g.Status == AcfStatus.InstalledOk)
            {
                MessageBox.Show(Loc.T("dlg.acfOk", g.Name), Loc.T("dlg.noAppIdTitle"),
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (g.Status == AcfStatus.ResidueFolder || g.Status == AcfStatus.EmptyFolder)
            {
                MessageBox.Show(Loc.T("dlg.notActionableBody", g.DamageReason, g.Folder.Length > 0 ? g.Folder : g.Path),
                    Loc.T("dlg.nothingToDoTitle"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (g.Status == AcfStatus.Damaged && string.IsNullOrEmpty(g.AppId))
            {
                MessageBox.Show(Loc.T("dlg.acfNoAppId"), Loc.T("dlg.noAppIdTitle"),
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            string action = g.Status == AcfStatus.Damaged ? Loc.T("action.repair") : Loc.T("action.generate");
            if ((g.Status == AcfStatus.MissingAcf || g.Status == AcfStatus.Unverified) && string.IsNullOrEmpty(g.AppId))
            {
                // 按文件夹名（标准游戏名）搜索 Steam 商店确定 AppID
                statusLabel.Text = Loc.T("ui.searchingStore");
                Application.DoEvents();
                KeyValuePair<string, string> hit = Core.SearchSteamStore(g.InstallDir);
                statusLabel.Text = Loc.T("ui.ready");
                if (hit.Key.Length > 0)
                {
                    DialogResult dr = MessageBox.Show(
                        Loc.T("dlg.storeHit", g.InstallDir, hit.Value, hit.Key),
                        Loc.T("dlg.storeHitTitle"), MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                    if (dr == DialogResult.Yes) g.AppId = hit.Key;
                    else if (dr == DialogResult.Cancel) return;
                }
                if (string.IsNullOrEmpty(g.AppId))
                {
                    string input = Interaction.InputBox(
                        Loc.T("dlg.inputAppId", g.InstallDir), Loc.T("dlg.inputAppIdTitle"), "");
                    if (string.IsNullOrEmpty(input)) return;
                    input = input.Trim();
                    long tmp;
                    if (!long.TryParse(input, out tmp)) { MessageBox.Show(Loc.T("dlg.appIdNumeric")); return; }
                    g.AppId = input;
                }
            }

            string target = g.Status == AcfStatus.Damaged
                ? g.Path
                : Path.Combine(g.Library, "steamapps", "appmanifest_" + g.AppId + ".acf");
            string body = g.Status == AcfStatus.Damaged
                ? Loc.T("dlg.confirmRepair", g.Name, g.AppId, target)
                : Loc.T("dlg.confirmGenerate", g.Name, g.AppId, target);
            DialogResult r = MessageBox.Show(body, Loc.T("dlg.confirmTitle", action),
                MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
            if (r != DialogResult.OK) return;

            DoRepair(g);
        }

        private void RepairAll()
        {
            List<AcfInfo> targets = new List<AcfInfo>();
            List<string> needManual = new List<string>();
            int skipped = 0;
            foreach (AcfInfo g in games)
            {
                if (g.Status == AcfStatus.Damaged)
                {
                    if (string.IsNullOrEmpty(g.AppId)) needManual.Add(Loc.T("dlg.entryNoAppId", g.Path));
                    else if (g.Check != null && g.Check.Verdict != ContentVerdict.Complete)
                        needManual.Add(Loc.T("dlg.entryIncomplete", g.Name, g.Check.ActualText));
                    else targets.Add(g);
                }
                else if (g.Status == AcfStatus.MissingAcf)
                {
                    if (!string.IsNullOrEmpty(g.AppId)) targets.Add(g);
                    else needManual.Add(Loc.T("dlg.entryNoAppIdShort", g.InstallDir));
                }
                else if (g.Status == AcfStatus.Unverified || g.Status == AcfStatus.ResidueFolder || g.Status == AcfStatus.EmptyFolder)
                {
                    skipped++;
                }
            }

            if (targets.Count == 0 && needManual.Count == 0)
            {
                MessageBox.Show(Loc.T("dlg.batchNothing", skipped > 0 ? Loc.T("dlg.batchSkippedNote", skipped) : ""),
                    Loc.T("dlg.batchTitle"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string msg = Loc.T("dlg.batchConfirm", targets.Count);
            if (skipped > 0) msg += Loc.T("dlg.batchSkippedLine", skipped);
            if (needManual.Count > 0) msg += Loc.T("dlg.batchManualLine", needManual.Count, string.Join("\n", needManual.ToArray()));

            DialogResult r = MessageBox.Show(msg, Loc.T("dlg.batchTitle"), MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
            if (r != DialogResult.OK) return;

            int ok = 0, fail = 0;
            List<string> failures = new List<string>();
            foreach (AcfInfo g in targets)
            {
                string err = Core.RepairByInfo(g, false);
                if (err == null) ok++;
                else { fail++; failures.Add(g.Name + ": " + err.Split('\n')[0]); }
            }
            Rescan();
            string done = Loc.T("dlg.batchDone", ok, fail);
            if (failures.Count > 0) done += Loc.T("dlg.batchFailLine", string.Join("\n", failures.ToArray()));
            MessageBox.Show(done, Loc.T("dlg.batchTitle"), MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void DoRepair(AcfInfo g)
        {
            SetBusy(true);
            statusLabel.Text = Loc.T("ui.working", g.Name, g.AppId);
            AcfInfo copy = g;
            Task.Run(delegate
            {
                string err = Core.RepairByInfo(copy, false);
                string report = Core.LastReport;
                BeginInvoke((Action)delegate
                {
                    SetBusy(false);
                    Rescan();
                    if (err == null)
                        MessageBox.Show(Loc.T("dlg.done", report), Loc.T("dlg.doneTitle"),
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                    else
                        MessageBox.Show(Loc.T("dlg.failed", err), Loc.T("dlg.failTitle"),
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                });
            });
        }

        private void ExportSelected()
        {
            AcfInfo g = Selected();
            if (g == null) { MessageBox.Show(Loc.T("dlg.selectRow")); return; }
            if (string.IsNullOrEmpty(g.AppId)) { MessageBox.Show(Loc.T("dlg.exportNoAcf")); return; }
            if (!File.Exists(g.Path)) { MessageBox.Show(Loc.T("dlg.exportMissing", g.Path)); return; }

            using (SaveFileDialog sfd = new SaveFileDialog())
            {
                sfd.FileName = "appmanifest_" + g.AppId + ".acf";
                sfd.Filter = "ACF (*.acf)|*.acf|*.*|*.*";
                sfd.Title = Loc.T("dlg.exportTitle");
                if (sfd.ShowDialog() == DialogResult.OK)
                {
                    File.Copy(g.Path, sfd.FileName, true);
                    MessageBox.Show(Loc.T("dlg.exportDone", sfd.FileName), Loc.T("dlg.exportOkTitle"),
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
        }

        private void ExportAllOk()
        {
            int n = 0;
            foreach (AcfInfo g in games)
                if (g.Status == AcfStatus.InstalledOk && !string.IsNullOrEmpty(g.AppId)) n++;
            if (n == 0) { MessageBox.Show(Loc.T("dlg.exportNoOk")); return; }

            using (FolderBrowserDialog fbd = new FolderBrowserDialog())
            {
                fbd.Description = Loc.T("dlg.exportChooseDir", n);
                fbd.SelectedPath = Core.ExportDir;
                if (fbd.ShowDialog() == DialogResult.OK)
                {
                    Directory.CreateDirectory(fbd.SelectedPath);
                    int done = 0;
                    foreach (AcfInfo g in games)
                    {
                        if (g.Status == AcfStatus.InstalledOk && !string.IsNullOrEmpty(g.AppId))
                        {
                            File.Copy(g.Path, Path.Combine(fbd.SelectedPath, "appmanifest_" + g.AppId + ".acf"), true);
                            done++;
                        }
                    }
                    MessageBox.Show(Loc.T("dlg.exportAllDone", done, fbd.SelectedPath), Loc.T("dlg.exportOkTitle"),
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
        }

        private void OpenExportDir()
        {
            string dir = Core.ExportDir;
            Directory.CreateDirectory(dir);
            try { Process.Start("explorer.exe", dir); }
            catch { MessageBox.Show(Loc.T("dlg.openDirFailed", dir)); }
        }
    }

    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Loc.Initialize();          // 默认英文；可用 STEAM_ACF_LANG 或界面里的语言下拉框切换
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            if (args.Length > 0)
            {
                RunCli(args);
                return;
            }

            if (!Core.DetectSteam())
            {
                MessageBox.Show(Loc.T("cli.notFound"), Loc.T("app.title"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            Core.LoadLibraries();
            Application.Run(new MainForm());
        }

        // 命令行模式（便于测试 / 自动化）
        private static void RunCli(string[] args)
        {
            Loc.Initialize();
            if (!Core.DetectSteam()) { Console.WriteLine(Loc.T("cli.notFound")); return; }
            Core.LoadLibraries();
            string cmd = args[0].ToLowerInvariant();

            if (cmd == "scan")
            {
                Loc.EnsureConsoleEncoding();
                Console.WriteLine(Loc.T("cli.steamFolder", Core.steamPath));
                Console.WriteLine(Core.AppInfoCacheStatus);
                List<AcfInfo> list = Core.ScanGames();
                Console.WriteLine();
                Console.WriteLine(Loc.T("cli.scanTable"));
                foreach (AcfInfo g in list)
                {
                    bool hasAcf = g.Folder.Length == 0;   // 孤儿目录没有 ACF，不显示 StateFlags/buildid
                    Console.WriteLine(string.Format("{0}\t{1}\t{2}\t{3}\t{4}\t{5}\t{6}",
                        Core.StatusTextCliOf(g.Status), g.AppId, g.Name, Path.GetFileName(g.Library),
                        hasAcf ? Loc.StateFlagsText(g.StateFlags) : "",
                        g.SizeOnDisk > 0 ? AcfFormat.Bytes(g.SizeOnDisk) : "", g.DamageReason));
                }
                return;
            }
            if (cmd == "repair")
            {
                if (args.Length < 2) { Console.WriteLine(Loc.T("cli.usage")); return; }
                bool dry = args.Any(a => a == "--dry-run");
                string r = Core.RepairExisting(args[1], dry);
                if (r != null) { Console.WriteLine(Loc.T("cli.fail", r)); Environment.ExitCode = 1; return; }
                if (dry) Console.WriteLine(Loc.T("cli.previewHeader") + "\n" + Core.LastPreview + Loc.T("cli.previewFooter"));
                Console.WriteLine(Loc.T("cli.ok") + "\n" + Core.LastReport);
                return;
            }
            if (cmd == "generate")
            {
                // 用法: generate <installDir|文件夹名> [appid] [--dry-run]
                if (args.Length < 2) { Console.WriteLine(Loc.T("cli.usage")); return; }
                bool dry = args.Any(a => a == "--dry-run");
                string appid = null;
                foreach (string a in args) { long tmp; if (long.TryParse(a, out tmp) && a != args[0]) appid = a; }

                AcfInfo g = null;
                foreach (AcfInfo x in Core.ScanGames())
                    if ((x.Status == AcfStatus.MissingAcf || x.Status == AcfStatus.Unverified)
                        && (string.Equals(x.InstallDir, args[1], StringComparison.OrdinalIgnoreCase)
                            || string.Equals(x.Name, args[1], StringComparison.OrdinalIgnoreCase))) { g = x; break; }
                if (g == null) { Console.WriteLine(Loc.T("cli.orphanNotFound")); Environment.ExitCode = 1; return; }
                if (appid != null) g.AppId = appid;
                string r = Core.GenerateAcf(g, dry);
                if (r != null) { Console.WriteLine(Loc.T("cli.fail", r)); Environment.ExitCode = 1; return; }
                if (dry) Console.WriteLine(Loc.T("cli.previewHeader") + "\n" + Core.LastPreview + Loc.T("cli.previewFooter"));
                Console.WriteLine(Loc.T("cli.ok") + "\n" + Core.LastReport);
                return;
            }
            if (cmd == "gui-selftest")
            {
                // 构建界面并填充一次表格（不显示窗口），用于确认界面代码不会崩
                Application.EnableVisualStyles();
                List<AcfInfo> list = Core.ScanGames();
                using (MainForm f = new MainForm())
                {
                    int rows = f.SelfTest(list);
                    Console.WriteLine(Loc.T("selftest.form", rows, list.Count));
                    int rows2 = f.SelfTestAsync(60000);
                    Console.WriteLine(Loc.T("selftest.async", rows2));
                    Loc.EnsureConsoleEncoding();
                    List<string> missing = Loc.MissingTranslations();
                    Console.WriteLine(Loc.T("selftest.lang", Loc.CurrentCode, Loc.KeyCount,
                        missing.Count == 0 ? "0" : string.Join(",", missing.ToArray())));
                    // 纯 ASCII 摘要行，便于脚本断言（不受控制台代码页影响）
                    Console.WriteLine("i18n: lang=" + Loc.CurrentCode + " keys=" + Loc.KeyCount
                        + " missing=" + missing.Count + " enc=" + Console.OutputEncoding.CodePage
                        + " was=" + Loc.ConsoleSwitchFrom + " flags=" + Loc.StateFlagsText(6));
                }
                return;
            }
            Console.WriteLine(Loc.T("cli.usage"));
            Console.WriteLine(Loc.T("cli.hint"));
        }
    }
}
