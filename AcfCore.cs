// AcfCore.cs - 共享核心：Steam 本地元数据解析 + 游戏文件夹内容校验 + ACF 文本生成
//
// 本文件同时被 SteamACFManagerGUI.cs（图形界面版，编译为 SteamACFManager.exe）
// 与 SteamACFManager.cs（命令行版）编译，因此只使用 .NET Framework 基础库。
//
// 关键结论（决定了“缺少 ACF”该怎么判定）：
//   1) ACF 只是 Steam 的“安装登记表”。真正判断某个 steamapps\common\<目录> 是不是
//      一个完整游戏安装，必须看目录里有没有该游戏的完整文件，而不是看目录是否存在。
//   2) Steam 卸载游戏时会删掉受 manifest 管理的文件，但会留下不受管理的残留：
//      存档、配置、崩溃转储、汉化补丁、模组、游戏自己下载的 DLC 补丁……这些残留
//      会让目录存在且体积不为 0（例如 2.4 GB 的 DLC 补丁），但游戏本体其实并不存在。
//   3) 判定依据来自 Steam 自己的本地缓存 appcache\appinfo.vdf：里面有每个 AppID 的
//      installdir、name、public 分支 buildid、每个 depot 的 manifest GID / 体积 /
//      平台(oslist) / 语言标签 / 是否 DLC / 是否共享库。用它可以离线算出
//      “完整安装应该有多大”，并与实测体积比对，从而判断文件是否完整。
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SteamACFManager
{
    // ---------------- 元数据模型 ----------------

    /// <summary>Steam 本地缓存里的单个 depot 信息。</summary>
    internal sealed class DepotMeta
    {
        public string Id = "";
        public string Manifest = "";      // public 分支的 manifest GID
        public long Size = -1;            // 该 depot 在磁盘上的体积；-1 = 缓存里没有
        public long Download = -1;        // 压缩下载体积；-1 = 未知
        public string DlcAppId = "";      // 非空表示这是 DLC 的 depot
        public bool Shared;               // 共享 depot（如 Steamworks 运行库）
        public string SharedOwner = "";   // 共享 depot 的归属 AppID（depotfromapp）
        public string OsList = "";        // "windows" / "windows,macos" / 空 = 全平台
        public string Language = "";      // 语言包标签；空 = 本体内容
        public bool SystemDefined;

        public bool WindowsReady
        {
            get
            {
                return OsList.Length == 0
                    || OsList.IndexOf("windows", StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }

        public bool IsDlc { get { return DlcAppId.Length > 0; } }
        public bool HasManifest { get { return Manifest.Length > 0; } }
    }

    /// <summary>某个 AppID 的完整本地元数据。</summary>
    internal sealed class AppMeta
    {
        public string AppId = "";
        public string Name = "";
        public string InstallDir = "";    // 来自 config.installdir（离线识别目录归属的关键）
        public long BuildId;
        public string Source = "";
        public List<DepotMeta> Depots = new List<DepotMeta>();

        public DepotMeta FindDepot(string id)
        {
            foreach (DepotMeta d in Depots) if (d.Id == id) return d;
            return null;
        }
    }

    /// <summary>按角色汇总的期望体积。</summary>
    internal sealed class ExpectedSizes
    {
        public long Required;             // 本体必需 depot（含默认语言）
        public long LanguagePacks;        // 可选语言包 depot
        public long Dlc;                  // DLC depot
        public bool RequiredUnknown;      // 必需 depot 中存在体积未知项
        public bool AnyKnown;

        public long Max
        {
            get { return Required + LanguagePacks + Dlc; }
        }
    }

    internal enum ContentVerdict
    {
        Empty,        // 目录里没有任何文件
        Incomplete,   // 已确认缺少游戏文件（含只剩残留的情况）
        Complete,     // 已确认文件完整
        Unverified    // 无法判定（缺少 AppID 或缺少体积基准）
    }

    /// <summary>一次目录内容校验的完整结果。</summary>
    internal sealed class ContentCheck
    {
        public ContentVerdict Verdict = ContentVerdict.Unverified;
        public long ActualBytes;
        public int FileCount;
        public long ExpectedBytes = -1;
        public string ExpectedSource = "";
        public bool ExpectedIsLowerBound;

        /// <summary>体积基准来源的词条键（src.appinfo / src.templateAcf …）与参数，渲染时才取当前语言。</summary>
        public string SourceKey = "";
        public object[] SourceArgs = new object[0];

        /// <summary>结论种类（empty / tiny / noBasis / incomplete / incompleteLower / lowerOnly / complete）。</summary>
        public string ReasonKind = "";

        public bool Actionable { get { return Verdict == ContentVerdict.Complete; } }

        public string ActualText { get { return AcfFormat.Bytes(ActualBytes); } }

        /// <summary>基准来源文字（按当前语言）。</summary>
        public string SourceText
        {
            get { return string.IsNullOrEmpty(SourceKey) ? "" : Loc.T(SourceKey, SourceArgs); }
        }

        /// <summary>
        /// 说明文字。注意这是“每次访问都按当前语言重新渲染”的属性：
        /// 旧实现把它在扫描时拼好存起来，导致切换语言后说明栏仍是旧语言。
        /// </summary>
        public string Reason
        {
            get
            {
                switch (ReasonKind)
                {
                    case "empty": return Loc.T("reason.empty");
                    case "tiny": return Loc.T("reason.tiny", AcfFormat.Bytes(ActualBytes), FileCount);
                    case "noBasis": return Loc.T("reason.noBasis");
                    case "incomplete": return Loc.T("reason.incomplete", NumbersText);
                    case "incompleteLower": return Loc.T("reason.incompleteLower", NumbersText);
                    case "lowerOnly": return Loc.T("reason.lowerOnly", NumbersText);
                    case "complete":
                        return Loc.T("reason.complete", NumbersText)
                            + (ActualBytes > ExpectedBytes * 1.5 ? Loc.T("reason.completeNoisy") : "");
                    default: return "";
                }
            }
        }

        private string NumbersText
        {
            get
            {
                return Loc.T("reason.numbers", AcfFormat.Bytes(ActualBytes), AcfFormat.Bytes(ExpectedBytes),
                    AcfFormat.Percent(ActualBytes, ExpectedBytes), SourceText);
            }
        }
    }

    /// <summary>
    /// 说明栏里的一段内容：可以是一段词条（按当前语言渲染）、一段目录校验结果（延迟渲染），
    /// 或一段固定文字。AcfInfo 保存的是这些“零件”而不是拼好的字符串，
    /// 这样切换界面语言时整列说明都会跟着切换。
    /// </summary>
    internal sealed class ReasonPiece
    {
        public string Key;
        public object[] Args;
        public ContentCheck Check;
        public string Literal;

        public static ReasonPiece Of(string key, params object[] args)
        {
            ReasonPiece p = new ReasonPiece();
            p.Key = key;
            p.Args = args;
            return p;
        }

        public static ReasonPiece OfCheck(ContentCheck check)
        {
            ReasonPiece p = new ReasonPiece();
            p.Check = check;
            return p;
        }

        public static ReasonPiece OfLiteral(string text)
        {
            ReasonPiece p = new ReasonPiece();
            p.Literal = text;
            return p;
        }

        public string Render()
        {
            if (Check != null) return Check.Reason;
            if (!string.IsNullOrEmpty(Key)) return Loc.T(Key, Args);
            return Literal ?? "";
        }

        /// <summary>
        /// 让一段内容可以直接当作另一词条（"{0}"）的参数：string.Format 会调用 ToString()，
        /// 这样嵌套进去的那一段也是“渲染时才翻译”，不会停留在扫描时的语言。
        /// </summary>
        public override string ToString()
        {
            return Render();
        }
    }

    internal static class ReasonRenderer
    {
        /// <summary>把若干段拼成一行说明（空段自动跳过）。</summary>
        public static string Render(List<ReasonPiece> parts)
        {
            if (parts == null || parts.Count == 0) return "";
            StringBuilder sb = new StringBuilder();
            foreach (ReasonPiece p in parts)
            {
                if (p == null) continue;
                string s = p.Render();
                if (string.IsNullOrEmpty(s)) continue;
                sb.Append(s);
            }
            return sb.ToString();
        }
    }

    internal static class AcfFormat
    {
        public static string Bytes(long b)
        {
            if (b < 0) return "未知";
            if (b == 0) return "0 B";
            if (b < 1024) return b + " B";
            double kb = b / 1024.0;
            if (kb < 1024) return kb.ToString("0.0") + " KB";
            double mb = kb / 1024.0;
            if (mb < 1024) return mb.ToString("0.0") + " MB";
            double gb = mb / 1024.0;
            return gb.ToString("0.00") + " GB";
        }

        public static string Percent(long actual, long expected)
        {
            if (expected <= 0) return "-";
            return (actual * 100.0 / expected).ToString("0.#") + "%";
        }
    }

    // ---------------- appinfo.vdf（Steam 本地 App 元数据缓存） ----------------
    //
    // 文件格式（版本 0x07564429，即 cache 版本 29）：
    //   头：u32 magic(0x07564429) | u32 universe | u64 字符串表偏移
    //   记录（连续排列到字符串表偏移）：
    //     u32 appid | u32 数据长度 | u32 infoState | u32 lastUpdated | u64 token
    //     44 字节摘要 | 二进制 KV（根节点 "appinfo"）
    //   字符串表：u32 条数 + 若干以 0 结尾的字符串；KV 里的 key 用索引引用该表。
    //   二进制 KV 节点：u8 类型 | u32 key索引 | 负载
    //     0x00 = 对象开始（子节点直到 0x08）  0x01 = 字符串（0 结尾）
    //     0x02 = int32                       0x07 = uint64
    //     0x08 = 对象结束（无负载）
    // 记录之间可能有若干 0x08 填充字节，故用有界前瞻定位下一条记录。
    internal static class AppInfoCache
    {
        private const uint MagicV29 = 0x07564429;

        private sealed class BinNode
        {
            public string Key = "";
            public int Type;
            public string Str;
            public long Int;
            public List<BinNode> Kids = new List<BinNode>();
            public BinNode Find(string k) { foreach (BinNode n in Kids) if (n.Key == k) return n; return null; }
            public string Get(string k) { BinNode n = Find(k); return n != null && n.Type == 1 ? n.Str : null; }

            /// <summary>取值：缓存里同一字段有时是整数节点，有时是字符串节点，两种都要认。</summary>
            public long GetNum(string k, long def)
            {
                BinNode n = Find(k);
                if (n == null) return def;
                if (n.Type == 2 || n.Type == 7) return n.Int;
                if (n.Type == 1 && n.Str != null)
                {
                    long v;
                    if (long.TryParse(n.Str, out v)) return v;
                }
                return def;
            }

            /// <summary>取字符串字段：整数节点也转成字符串（如 gid / buildid）。</summary>
            public string GetStr(string k)
            {
                BinNode n = Find(k);
                if (n == null) return null;
                if (n.Type == 1) return n.Str;
                if (n.Type == 2 || n.Type == 7) return n.Int.ToString();
                return null;
            }

            public long GetInt(string k, long def) { return GetNum(k, def); }
        }

        private static readonly Dictionary<string, AppMeta> byAppId =
            new Dictionary<string, AppMeta>(StringComparer.Ordinal);
        private static readonly Dictionary<string, string> byInstallDir =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> byName =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // 归一化名称索引：忽略空格、标点、大小写（例如 "DEATH STRANDING DIRECTOR'S CUT" 与目录
        // "DEATH STRANDING DIRECTORS CUT"），用于在缓存里兜底找人
        private static readonly Dictionary<string, string> byLooseName =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Dictionary<string, string>> manifestToAppId =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);

        private static bool loaded;
        private static string loadedFrom = "";
        private static string loadError = "";
        private static byte[] data;
        private static List<string> table = new List<string>();

        /// <summary>是否已成功载入缓存。</summary>
        public static bool Available { get { return loaded && byAppId.Count > 0; } }
        public static string LoadedFrom { get { return loadedFrom; } }
        public static string LoadError { get { return loadError; } }
        public static int AppCount { get { return byAppId.Count; } }

        public static void EnsureLoaded(string steamPath)
        {
            string path = Path.Combine(steamPath, "appcache", "appinfo.vdf");
            if (loaded && string.Equals(path, loadedFrom, StringComparison.OrdinalIgnoreCase)) return;

            loaded = true;
            loadedFrom = path;
            loadError = "";
            byAppId.Clear();
            byInstallDir.Clear();
            byName.Clear();
            byLooseName.Clear();

            if (!File.Exists(path)) { loadError = "未找到 " + path; return; }
            try
            {
                data = File.ReadAllBytes(path);
                TryReadPrologue();
            }
            catch (Exception ex)
            {
                loadError = ex.Message;
            }
        }

        private static void TryReadPrologue()
        {
            if (data.Length < 16) { loadError = "文件过小"; return; }
            uint magic = BitConverter.ToUInt32(data, 0);
            if (magic != MagicV29)
            {
                loadError = "appinfo.vdf 版本 0x" + magic.ToString("x8") + " 暂不支持（本工具支持 0x07564429）";
                return;
            }
            ulong tableOffset = BitConverter.ToUInt64(data, 8);
            if (tableOffset < 16 || tableOffset > (ulong)data.Length) { loadError = "字符串表偏移异常"; return; }

            table = new List<string>();
            int count = BitConverter.ToInt32(data, (int)tableOffset);
            int pos = (int)tableOffset + 4;
            for (int i = 0; i < count && pos < data.Length; i++)
            {
                int end = pos;
                while (end < data.Length && data[end] != 0) end++;
                table.Add(Encoding.UTF8.GetString(data, pos, end - pos));
                pos = end + 1;
            }

            int limit = (int)tableOffset;
            int p = 16;
            int guard = 0;
            while (p + 68 < limit && guard++ < 200000)
            {
                int appid;
                BinNode root;
                int end;
                if (!TryRecord(p, limit, out appid, out root, out end))
                {
                    // 单条记录损坏不应影响整体：向后找下一条能解析的记录
                    int recovered = -1;
                    for (int cand = p + 1; cand < limit - 68 && cand < p + 4096; cand++)
                    {
                        int a2, e2;
                        BinNode r2;
                        if (TryRecord(cand, limit, out a2, out r2, out e2)) { recovered = cand; break; }
                    }
                    if (recovered < 0) break;
                    p = recovered;
                    continue;
                }
                AppMeta meta = ToMeta(appid, root);
                if (meta != null)
                {
                    byAppId[meta.AppId] = meta;
                    if (meta.InstallDir.Length > 0 && !byInstallDir.ContainsKey(meta.InstallDir))
                        byInstallDir[meta.InstallDir] = meta.AppId;
                    if (meta.Name.Length > 0 && !byName.ContainsKey(meta.Name))
                        byName[meta.Name] = meta.AppId;
                    string looseDir = Loose(meta.InstallDir);
                    if (looseDir.Length > 0 && !byLooseName.ContainsKey(looseDir))
                        byLooseName[looseDir] = meta.AppId;
                    string looseName = Loose(meta.Name);
                    if (looseName.Length > 0 && !byLooseName.ContainsKey(looseName))
                        byLooseName[looseName] = meta.AppId;
                }

                int next = -1;
                for (int cand = end; cand <= end + 16 && cand + 68 < limit; cand++)
                {
                    int a2, e2;
                    BinNode r2;
                    if (TryRecord(cand, limit, out a2, out r2, out e2)) { next = cand; break; }
                }
                if (next < 0) break;
                p = next;
            }

            if (byAppId.Count == 0 && loadError.Length == 0) loadError = "未从缓存中解析出任何 App 记录";
        }

        private static bool TryRecord(int pos, int limit, out int appid, out BinNode root, out int end)
        {
            appid = 0;
            root = null;
            end = 0;
            if (pos + 68 >= limit) return false;
            appid = BitConverter.ToInt32(data, pos);
            if (appid <= 0) return false;
            if (data[pos + 68] != 0) return false;   // KV 根节点必须是对象
            try { root = ParseNode(pos + 68, out end); }
            catch { return false; }
            BinNode a = root.Find("appid");
            return a != null && a.Int == appid;
        }

        private static BinNode ParseNode(int pos, out int next)
        {
            int type = data[pos];
            if (type == 8) { next = pos + 1; return null; }
            int keyIdx = BitConverter.ToInt32(data, pos + 1);
            pos += 5;
            BinNode n = new BinNode();
            n.Type = type;
            n.Key = (keyIdx >= 0 && keyIdx < table.Count) ? table[keyIdx] : "";
            if (type == 0)
            {
                while (true)
                {
                    if (pos >= data.Length) throw new EndOfStreamException();
                    if (data[pos] == 8) { pos++; break; }
                    int childNext;
                    BinNode c = ParseNode(pos, out childNext);
                    if (c != null) n.Kids.Add(c);
                    pos = childNext;
                }
            }
            else if (type == 1)
            {
                int end = pos;
                while (end < data.Length && data[end] != 0) end++;
                if (end >= data.Length) throw new EndOfStreamException();
                n.Str = Encoding.UTF8.GetString(data, pos, end - pos);
                pos = end + 1;
            }
            else if (type == 2)
            {
                if (pos + 4 > data.Length) throw new EndOfStreamException();
                n.Int = BitConverter.ToInt32(data, pos);
                pos += 4;
            }
            else if (type == 7)
            {
                if (pos + 8 > data.Length) throw new EndOfStreamException();
                n.Int = BitConverter.ToInt64(data, pos);
                pos += 8;
            }
            else throw new InvalidDataException("未知 KV 类型 " + type);
            next = pos;
            return n;
        }

        private static AppMeta ToMeta(int appid, BinNode root)
        {
            AppMeta m = new AppMeta();
            m.AppId = appid.ToString();
            m.Source = "appinfo.vdf";
            BinNode common = root.Find("common");
            if (common != null) m.Name = common.Get("name") ?? "";
            BinNode cfg = root.Find("config");
            if (cfg != null) m.InstallDir = cfg.Get("installdir") ?? "";

            BinNode depots = root.Find("depots");
            if (depots == null) return m;

            BinNode branches = depots.Find("branches");
            if (branches != null)
            {
                BinNode pub = branches.Find("public");
                if (pub != null) m.BuildId = pub.GetInt("buildid", 0);
            }

            foreach (BinNode d in depots.Kids)
            {
                long dummy;
                if (!long.TryParse(d.Key, out dummy)) continue;   // 跳过 "branches" 等非 depot 节点
                DepotMeta dm = new DepotMeta();
                dm.Id = d.Key;
                BinNode mans = d.Find("manifests");
                BinNode pubMan = mans != null ? mans.Find("public") : null;
                if (pubMan != null)
                {
                    if (pubMan.Type == 1) dm.Manifest = pubMan.Str ?? "";
                    else
                    {
                        dm.Manifest = pubMan.GetStr("gid") ?? "";
                        dm.Size = pubMan.GetNum("size", -1);
                        dm.Download = pubMan.GetNum("download", -1);
                    }
                }
                else
                {
                    // 老格式里 manifest 直接放在 depot 节点上
                    string gid = d.GetStr("manifest");
                    if (!string.IsNullOrEmpty(gid)) dm.Manifest = gid;
                }
                long msz = d.GetNum("maxsize", -1);
                if (dm.Size < 0 && msz > 0) dm.Size = msz;
                dm.DlcAppId = d.GetStr("dlcappid") ?? "";
                dm.Shared = d.Find("sharedinstall") != null;
                dm.SharedOwner = d.GetStr("depotfromapp") ?? "";
                dm.SystemDefined = d.Find("systemdefined") != null;
                BinNode dcfg = d.Find("config");
                if (dcfg != null)
                {
                    dm.OsList = dcfg.Get("oslist") ?? "";
                    dm.Language = dcfg.Get("language") ?? "";
                }
                m.Depots.Add(dm);
            }
            return m;
        }

        // ---------------- 查询 ----------------

        public static AppMeta Get(string appid)
        {
            if (string.IsNullOrEmpty(appid)) return null;
            AppMeta m;
            if (byAppId.TryGetValue(appid, out m)) return m;
            return null;
        }

        /// <summary>用文件夹名（= installdir）离线反查 AppID。</summary>
        public static string FindAppIdByInstallDir(string installDir)
        {
            if (string.IsNullOrEmpty(installDir)) return "";
            string id;
            if (byInstallDir.TryGetValue(installDir, out id)) return id;
            return "";
        }

        /// <summary>用游戏名离线反查 AppID（目录名常常就是游戏名）。</summary>
        public static string FindAppIdByName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            string id;
            if (byName.TryGetValue(name, out id)) return id;
            return "";
        }

        /// <summary>归一化：只保留字母与数字并转小写，用于忽略空格/标点/大小写的匹配。</summary>
        public static string Loose(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            StringBuilder sb = new StringBuilder(s.Length);
            foreach (char c in s)
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }

        /// <summary>
        /// 宽松匹配 installdir / 游戏名（忽略空格与标点），例如目录
        /// "DEATH STRANDING DIRECTORS CUT" 能对上缓存里的 "DEATH STRANDING DIRECTOR'S CUT"。
        /// </summary>
        public static string FindAppIdByLooseName(string name)
        {
            string key = Loose(name);
            if (key.Length == 0) return "";
            string id;
            if (byLooseName.TryGetValue(key, out id)) return id;
            return "";
        }

        /// <summary>某个 AppID 是否是另一个 App 的 DLC（用于说明“只有 DLC 文件”）。</summary>
        public static string FindParentAppIdOfDlc(string dlcAppId)
        {
            foreach (KeyValuePair<string, AppMeta> kv in byAppId)
                foreach (DepotMeta d in kv.Value.Depots)
                    if (d.DlcAppId == dlcAppId) return kv.Key;
            return "";
        }

        /// <summary>用 depot id + manifest GID 反查所属 AppID（离线识别遗留安装的兜底）。</summary>
        public static string FindAppIdByDepot(string depotId, string manifestGid)
        {
            string key = depotId + "_" + manifestGid;
            if (manifestToAppId.ContainsKey(key)) return manifestToAppId[key][""];
            foreach (KeyValuePair<string, AppMeta> kv in byAppId)
            {
                DepotMeta d = kv.Value.FindDepot(depotId);
                if (d != null && manifestGid.Length > 0 && d.Manifest == manifestGid)
                {
                    Dictionary<string, string> slot = new Dictionary<string, string>();
                    slot[""] = kv.Key;
                    manifestToAppId[key] = slot;
                    return kv.Key;
                }
            }
            return "";
        }
    }

    // ---------------- 目录内容探测 ----------------

    internal sealed class FolderStats
    {
        public long Bytes;
        public int Files;
        public int Dirs;
    }

    internal static class FolderProbe
    {
        /// <summary>统计目录内所有文件的总字节数与文件数（跳过符号链接/联接点以免递归成环）。</summary>
        public static FolderStats Measure(string dir)
        {
            FolderStats st = new FolderStats();
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return st;
            Stack<string> queue = new Stack<string>();
            queue.Push(dir);
            int guard = 0;
            while (queue.Count > 0 && guard++ < 200000)
            {
                string cur = queue.Pop();
                string[] files;
                try { files = Directory.GetFiles(cur); }
                catch { files = new string[0]; }
                foreach (string f in files)
                {
                    try
                    {
                        FileInfo fi = new FileInfo(f);
                        if ((fi.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                        st.Bytes += fi.Length;
                        st.Files++;
                    }
                    catch { }
                }
                string[] subs;
                try { subs = Directory.GetDirectories(cur); }
                catch { subs = new string[0]; }
                foreach (string s in subs)
                {
                    try
                    {
                        if ((File.GetAttributes(s) & FileAttributes.ReparsePoint) != 0) continue;
                    }
                    catch { continue; }
                    st.Dirs++;
                    queue.Push(s);
                }
            }
            return st;
        }

        /// <summary>目录里最新的文件写入时间（判断是否刚刚动过，用于提示）。</summary>
        public static DateTime NewestWrite(string dir)
        {
            DateTime newest = DateTime.MinValue;
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return newest;
            try
            {
                foreach (string f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        DateTime t = File.GetLastWriteTimeUtc(f);
                        if (t > newest) newest = t;
                    }
                    catch { }
                }
            }
            catch { }
            return newest;
        }
    }

    // ---------------- 完整性校验 ----------------

    internal static class ContentVerifier
    {
        /// <summary>判定“文件完整”的下限比例：实测体积不低于期望体积的该比例即视为完整。</summary>
        public const double CompleteRatio = 0.90;
        /// <summary>实测体积超过期望的该倍数时提示可能含模组或匹配错 AppID。</summary>
        private const double NoisyRatio = 1.5;
        /// <summary>低于该体积（1 MB）的目录不可能是完整游戏安装，即使拿不到体积基准也能判定。</summary>
        public const long MinPlausibleGameBytes = 1024 * 1024;

        public static ExpectedSizes ComputeExpected(AppMeta meta)
        {
            ExpectedSizes e = new ExpectedSizes();
            if (meta == null) return e;
            foreach (DepotMeta d in meta.Depots)
            {
                if (d.Shared) continue;
                if (!d.WindowsReady) continue;
                if (d.IsDlc)
                {
                    if (d.Size > 0) { e.Dlc += d.Size; e.AnyKnown = true; }
                    continue;
                }
                if (d.Language.Length > 0)
                {
                    if (d.Size > 0) { e.LanguagePacks += d.Size; e.AnyKnown = true; }
                    continue;
                }
                if (d.Size > 0) { e.Required += d.Size; e.AnyKnown = true; }
                else e.RequiredUnknown = true;
            }
            return e;
        }

        /// <summary>
        /// 校验目录内容是否构成“完整安装”。
        /// referenceSize/referenceSourceKey：优先使用的体积基准（例如同名 AppID 在别的库里的
        /// ACF 中 Steam 自己记录的 SizeOnDisk），为空则用 appinfo 缓存推算。
        /// 注意 referenceSourceKey 传的是词条键（如 src.templateAcf），不是已经翻译好的文字，
        /// 这样说明栏在切换界面语言时才能跟着变。
        /// </summary>
        public static ContentCheck Check(string folder, AppMeta meta, long referenceSize,
            string referenceSourceKey, params object[] referenceSourceArgs)
        {
            ContentCheck r = new ContentCheck();
            FolderStats st = FolderProbe.Measure(folder);
            r.ActualBytes = st.Bytes;
            r.FileCount = st.Files;

            if (st.Files == 0 || st.Bytes == 0)
            {
                r.Verdict = ContentVerdict.Empty;
                r.ReasonKind = "empty";
                return r;
            }

            ExpectedSizes exp = ComputeExpected(meta);
            bool haveReference = referenceSize > 0;
            if (haveReference)
            {
                r.ExpectedBytes = referenceSize;
                r.SourceKey = referenceSourceKey ?? "";
                r.SourceArgs = referenceSourceArgs ?? new object[0];
                r.ExpectedIsLowerBound = false;
            }
            else if (exp.AnyKnown && exp.Required > 0)
            {
                r.ExpectedBytes = exp.Required;
                r.SourceKey = "src.appinfo";
                r.ExpectedIsLowerBound = exp.RequiredUnknown;
            }

            if (r.ExpectedBytes <= 0)
            {
                // 拿不到体积基准。但小到 1 MB 以下的目录不可能装下任何完整 Steam 游戏，
                // 这类目录（只剩几十字节的崩溃转储/配置文件）可以直接判定为残留。
                if (st.Bytes < MinPlausibleGameBytes)
                {
                    r.Verdict = ContentVerdict.Incomplete;
                    r.ReasonKind = "tiny";
                    return r;
                }
                r.Verdict = ContentVerdict.Unverified;
                r.ReasonKind = "noBasis";
                return r;
            }

            if (r.ExpectedIsLowerBound)
            {
                // 期望值只是“下界”（有 depot 体积未知）：只有明显偏小才能确定不完整
                if (st.Bytes < r.ExpectedBytes * CompleteRatio)
                {
                    r.Verdict = ContentVerdict.Incomplete;
                    r.ReasonKind = "incompleteLower";
                }
                else
                {
                    r.Verdict = ContentVerdict.Unverified;
                    r.ReasonKind = "lowerOnly";
                }
                return r;
            }

            if (st.Bytes < r.ExpectedBytes * CompleteRatio)
            {
                r.Verdict = ContentVerdict.Incomplete;
                r.ReasonKind = "incomplete";
                return r;
            }

            r.Verdict = ContentVerdict.Complete;
            r.ReasonKind = "complete";
            return r;
        }
    }

    // ---------------- ACF 生成 ----------------

    internal sealed class AcfSpec
    {
        public string AppId = "";
        public string Name = "";
        public string InstallDir = "";
        public string LauncherPath = "";
        public string LastOwner = "";
        public string Language = "schinese";
        public long StateFlags = 4;
        public long SizeOnDisk;
        public long BuildId;
        public long LastPlayed;
        public long LastUpdated;
        public long TargetBuildId;
        public long DownloadType = 1;
        public List<DepotMeta> Depots = new List<DepotMeta>();
        public List<KeyValuePair<string, string>> SharedDepots = new List<KeyValuePair<string, string>>();
        public List<KeyValuePair<string, string>> InstallScripts = new List<KeyValuePair<string, string>>();
    }

    internal static class AcfWriter
    {
        public static long NowUnix()
        {
            return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        }

        public static string Escape(string s)
        {
            if (s == null) return "";
            StringBuilder sb = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                if (c == '\\' || c == '"') sb.Append('\\');
                sb.Append(c);
            }
            return sb.ToString();
        }

        public static string Build(AcfSpec s)
        {
            if (s.LastUpdated <= 0) s.LastUpdated = NowUnix();
            if (s.TargetBuildId <= 0) s.TargetBuildId = s.BuildId;
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("\"AppState\"");
            sb.AppendLine("{");
            sb.AppendLine("\t\"appid\"\t\t\"" + s.AppId + "\"");
            sb.AppendLine("\t\"Universe\"\t\t\"1\"");
            sb.AppendLine("\t\"LauncherPath\"\t\t\"" + Escape(s.LauncherPath) + "\"");
            sb.AppendLine("\t\"name\"\t\t\"" + Escape(s.Name) + "\"");
            sb.AppendLine("\t\"StateFlags\"\t\t\"" + s.StateFlags + "\"");
            sb.AppendLine("\t\"installdir\"\t\t\"" + Escape(s.InstallDir) + "\"");
            sb.AppendLine("\t\"LastUpdated\"\t\t\"" + s.LastUpdated + "\"");
            sb.AppendLine("\t\"LastPlayed\"\t\t\"" + s.LastPlayed + "\"");
            sb.AppendLine("\t\"SizeOnDisk\"\t\t\"" + s.SizeOnDisk + "\"");
            sb.AppendLine("\t\"StagingSize\"\t\t\"0\"");
            sb.AppendLine("\t\"buildid\"\t\t\"" + s.BuildId + "\"");
            sb.AppendLine("\t\"LastOwner\"\t\t\"" + s.LastOwner + "\"");
            sb.AppendLine("\t\"DownloadType\"\t\t\"" + s.DownloadType + "\"");
            sb.AppendLine("\t\"UpdateResult\"\t\t\"0\"");
            // 与 Steam 下载完成后的记录方式保持一致：下载/暂存字节数即磁盘占用
            sb.AppendLine("\t\"BytesToDownload\"\t\t\"" + s.SizeOnDisk + "\"");
            sb.AppendLine("\t\"BytesDownloaded\"\t\t\"" + s.SizeOnDisk + "\"");
            sb.AppendLine("\t\"BytesToStage\"\t\t\"" + s.SizeOnDisk + "\"");
            sb.AppendLine("\t\"BytesStaged\"\t\t\"" + s.SizeOnDisk + "\"");
            sb.AppendLine("\t\"TargetBuildID\"\t\t\"" + s.TargetBuildId + "\"");
            sb.AppendLine("\t\"AutoUpdateBehavior\"\t\t\"0\"");
            sb.AppendLine("\t\"AllowOtherDownloadsWhileRunning\"\t\t\"0\"");
            sb.AppendLine("\t\"ScheduledAutoUpdate\"\t\t\"0\"");

            sb.AppendLine("\t\"InstalledDepots\"");
            sb.AppendLine("\t{");
            List<DepotMeta> depots = new List<DepotMeta>(s.Depots);
            depots.Sort(delegate(DepotMeta a, DepotMeta b)
            {
                long x, y;
                bool ax = long.TryParse(a.Id, out x);
                bool by = long.TryParse(b.Id, out y);
                if (ax && by) return x.CompareTo(y);
                return string.Compare(a.Id, b.Id, StringComparison.Ordinal);
            });
            foreach (DepotMeta d in depots)
            {
                sb.AppendLine("\t\t\"" + d.Id + "\"");
                sb.AppendLine("\t\t{");
                sb.AppendLine("\t\t\t\"manifest\"\t\t\"" + d.Manifest + "\"");
                sb.AppendLine("\t\t\t\"size\"\t\t\"" + (d.Size > 0 ? d.Size : 0) + "\"");
                if (d.IsDlc) sb.AppendLine("\t\t\t\"dlcappid\"\t\t\"" + d.DlcAppId + "\"");
                sb.AppendLine("\t\t}");
            }
            sb.AppendLine("\t}");

            if (s.InstallScripts.Count > 0)
            {
                sb.AppendLine("\t\"InstallScripts\"");
                sb.AppendLine("\t{");
                foreach (KeyValuePair<string, string> kv in s.InstallScripts)
                    sb.AppendLine("\t\t\"" + kv.Key + "\"\t\t\"" + Escape(kv.Value) + "\"");
                sb.AppendLine("\t}");
            }

            if (s.SharedDepots.Count > 0)
            {
                sb.AppendLine("\t\"SharedDepots\"");
                sb.AppendLine("\t{");
                foreach (KeyValuePair<string, string> kv in s.SharedDepots)
                    sb.AppendLine("\t\t\"" + kv.Key + "\"\t\t\"" + kv.Value + "\"");
                sb.AppendLine("\t}");
            }

            sb.AppendLine("\t\"UserConfig\"");
            sb.AppendLine("\t{");
            sb.AppendLine("\t\t\"language\"\t\t\"" + Escape(s.Language) + "\"");
            sb.AppendLine("\t}");
            sb.AppendLine("\t\"MountedConfig\"");
            sb.AppendLine("\t{");
            sb.AppendLine("\t\t\"language\"\t\t\"" + Escape(s.Language) + "\"");
            sb.AppendLine("\t}");
            sb.AppendLine("}");
            return sb.ToString();
        }

        /// <summary>
        /// 从某个 App 的元数据里挑出“应当写进 ACF 的已安装 depot”。
        /// actualBytes 用于把体积证据反映到语言包选择上：只有体积上放得下的语言包才认为装在盘上。
        /// 默认不写 DLC depot（无法离线确认是否拥有该 DLC，写错会让 Steam 去补下 DLC）。
        /// </summary>
        public static List<DepotMeta> PickInstalledDepots(AppMeta meta, long actualBytes, string userLanguage, List<string> notes)
        {
            List<DepotMeta> picked = new List<DepotMeta>();
            if (meta == null) return picked;
            long required = 0;
            List<DepotMeta> languages = new List<DepotMeta>();
            foreach (DepotMeta d in meta.Depots)
            {
                if (d.Shared || d.IsDlc || !d.WindowsReady || !d.HasManifest) continue;
                if (d.Language.Length > 0) { languages.Add(d); continue; }
                picked.Add(d);
                if (d.Size > 0) required += d.Size;
            }

            long slack = actualBytes - required;
            if (slack > 0 && languages.Count > 0)
            {
                // 用户语言优先，其余按体积从大到小，只要放得下就认为在盘上
                languages.Sort(delegate(DepotMeta a, DepotMeta b)
                {
                    bool al = a.Language.Equals(userLanguage, StringComparison.OrdinalIgnoreCase);
                    bool bl = b.Language.Equals(userLanguage, StringComparison.OrdinalIgnoreCase);
                    if (al != bl) return al ? -1 : 1;
                    return b.Size.CompareTo(a.Size);
                });
                foreach (DepotMeta d in languages)
                {
                    if (d.Size > 0 && d.Size <= slack * 1.1)
                    {
                        picked.Add(d);
                        slack -= d.Size;
                        if (notes != null) notes.Add("语言包 depot " + d.Id + "（" + d.Language + "，" + AcfFormat.Bytes(d.Size) + "）");
                    }
                }
            }

            List<DepotMeta> dlcs = new List<DepotMeta>();
            foreach (DepotMeta d in meta.Depots)
            {
                if (d.IsDlc && d.HasManifest && d.WindowsReady) dlcs.Add(d);
            }
            if (dlcs.Count > 0 && notes != null)
                notes.Add("该游戏有 " + dlcs.Count + " 个 DLC depot 未写入 ACF（无法离线确认是否拥有），Steam 会在需要时自行校验/下载");

            picked.Sort(delegate(DepotMeta a, DepotMeta b)
            {
                long x, y;
                bool ax = long.TryParse(a.Id, out x);
                bool by = long.TryParse(b.Id, out y);
                if (ax && by) return x.CompareTo(y);
                return string.Compare(a.Id, b.Id, StringComparison.Ordinal);
            });
            return picked;
        }

        /// <summary>
        /// 计算要写入 ACF 的 SharedDepots（共享运行库 depot → 归属 AppID）。
        /// 优先沿用原 ACF 里已有的列表（Steam 实际登记过的那份），只在原 ACF 没有时才按缓存推导；
        /// 归属 AppID 缺失时用缓存里的 depotfromapp 补上。
        /// </summary>
        public static List<KeyValuePair<string, string>> SharedDepotsOf(AppMeta meta, List<KeyValuePair<string, string>> template)
        {
            List<KeyValuePair<string, string>> list = new List<KeyValuePair<string, string>>();
            if (template != null && template.Count > 0)
            {
                foreach (KeyValuePair<string, string> kv in template)
                {
                    string owner = kv.Value;
                    if (string.IsNullOrEmpty(owner) && meta != null)
                    {
                        DepotMeta d = meta.FindDepot(kv.Key);
                        if (d != null) owner = d.SharedOwner;
                    }
                    list.Add(new KeyValuePair<string, string>(kv.Key, owner));
                }
                return list;
            }
            if (meta != null)
            {
                foreach (DepotMeta d in meta.Depots)
                {
                    if (!d.Shared) continue;
                    list.Add(new KeyValuePair<string, string>(d.Id, d.SharedOwner));
                }
            }
            return list;
        }

    }
}
