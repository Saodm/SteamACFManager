// Localization.cs - 界面与提示语的多语言支持（图形版 / 命令行版共用）
//
// 默认语言为英文（English）。可在界面左上角的下拉框切换，选择会保存在
// %APPDATA%\SteamACFManager\settings.ini；也可用环境变量 STEAM_ACF_LANG 临时覆盖
// （例如 STEAM_ACF_LANG=zh-CN）。
//
// 新增语言只需在 LangOrder 里加一个语言代码，并在每行 Add(...) 末尾补一列译文；
// gui-selftest 会校验“每个键在每个语言下都有译文”，漏了会直接报出来。
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SteamACFManager
{
    internal sealed class LanguageInfo
    {
        public string Code = "";
        public string NativeName = "";
        public LanguageInfo(string code, string nativeName) { Code = code; NativeName = nativeName; }
    }

    internal static class Loc
    {
        // 语言顺序 = 每行 Add(...) 里译文的顺序
        public static readonly LanguageInfo[] Languages = new LanguageInfo[]
        {
            new LanguageInfo("en",    "English"),
            new LanguageInfo("zh-CN", "简体中文"),
            new LanguageInfo("zh-TW", "繁體中文"),
            new LanguageInfo("ja",    "日本語"),
            new LanguageInfo("ko",    "한국어"),
            new LanguageInfo("es",    "Español"),
            new LanguageInfo("de",    "Deutsch"),
            new LanguageInfo("ru",    "Русский")
        };

        private const string DefaultCode = "en";
        private static readonly Dictionary<string, string[]> Table = new Dictionary<string, string[]>(StringComparer.Ordinal);
        private static int current = 0;

        public static string CurrentCode { get { return Languages[current].Code; } }
        public static string CurrentNativeName { get { return Languages[current].NativeName; } }

        /// <summary>StateFlags 位含义（Steam 自己的标识符，始终用英文）</summary>
        private static readonly KeyValuePair<long, string>[] FlagBits = new KeyValuePair<long, string>[]
        {
            new KeyValuePair<long, string>(1L, "Uninstalled"),
            new KeyValuePair<long, string>(2L, "UpdateRequired"),
            new KeyValuePair<long, string>(4L, "FullyInstalled"),
            new KeyValuePair<long, string>(8L, "UpdateQueued"),
            new KeyValuePair<long, string>(16L, "UpdateOptional"),
            new KeyValuePair<long, string>(32L, "FilesMissing"),
            new KeyValuePair<long, string>(64L, "SharedOnly"),
            new KeyValuePair<long, string>(128L, "FilesCorrupt"),
            new KeyValuePair<long, string>(256L, "UpdateRunning"),
            new KeyValuePair<long, string>(512L, "UpdatePaused"),
            new KeyValuePair<long, string>(1024L, "UpdateStarted"),
            new KeyValuePair<long, string>(2048L, "Uninstalling"),
            new KeyValuePair<long, string>(4096L, "BackupRunning"),
            new KeyValuePair<long, string>(65536L, "Reconfiguring"),
            new KeyValuePair<long, string>(131072L, "Validating"),
            new KeyValuePair<long, string>(262144L, "AddingFiles"),
            new KeyValuePair<long, string>(524288L, "Preallocating"),
            new KeyValuePair<long, string>(1048576L, "Downloading"),
            new KeyValuePair<long, string>(2097152L, "Staging"),
            new KeyValuePair<long, string>(4194304L, "Committing"),
            new KeyValuePair<long, string>(8388608L, "UpdateStopping")
        };

        /// <summary>把 StateFlags 数字翻译成 “数字 (英文含义)”，例如 4 (FullyInstalled)、6 (UpdateRequired | FullyInstalled)。</summary>
        public static string StateFlagsText(long flags)
        {
            if (flags == 0) return "0 (NoFlags)";
            StringBuilder sb = new StringBuilder();
            foreach (KeyValuePair<long, string> kv in FlagBits)
            {
                if ((flags & kv.Key) == 0) continue;
                if (sb.Length > 0) sb.Append(" | ");
                sb.Append(kv.Value);
            }
            if (sb.Length == 0) return flags + " (Unknown)";
            long known = 0;
            foreach (KeyValuePair<long, string> kv in FlagBits) known |= kv.Key;
            long rest = flags & ~known;
            if (rest != 0) sb.Append(" | ?" + rest);
            return flags + " (" + sb + ")";
        }

        /// <summary>位含义表格（供提示气泡使用，标识符保持英文）。</summary>
        public static string StateFlagsLegend()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(T("tip.stateflags.intro"));
            sb.AppendLine();
            sb.AppendLine();
            int i = 0;
            foreach (KeyValuePair<long, string> kv in FlagBits)
            {
                sb.Append(kv.Key + "=" + kv.Value);
                i++;
                if (i % 4 == 0) sb.AppendLine(); else sb.Append("   ");
            }
            sb.AppendLine();
            sb.Append(T("tip.stateflags.footer"));
            return sb.ToString();
        }

        private static bool initialized;

        public static void Initialize()
        {
            if (initialized) return;   // 只初始化一次，避免覆盖用户在界面里选择的语言
            initialized = true;
            string code = Environment.GetEnvironmentVariable("STEAM_ACF_LANG");
            if (string.IsNullOrEmpty(code)) code = ReadSavedLanguage();
            if (string.IsNullOrEmpty(code)) code = DefaultCode;
            SetLanguage(code);
        }

        public static bool SetLanguage(string code)
        {
            for (int i = 0; i < Languages.Length; i++)
            {
                if (string.Equals(Languages[i].Code, code, StringComparison.OrdinalIgnoreCase))
                {
                    current = i;
                    return true;
                }
            }
            return false;
        }

        public static int IndexOf(string code)
        {
            for (int i = 0; i < Languages.Length; i++)
                if (string.Equals(Languages[i].Code, code, StringComparison.OrdinalIgnoreCase)) return i;
            return 0;
        }

        /// <summary>取当前语言的文本并套用参数。</summary>
        public static string T(string key, params object[] args)
        {
            string[] row;
            if (!Table.TryGetValue(key, out row)) return "!" + key + "!";
            string s = row[current];
            if (s == null || s.Length == 0) s = row[0];      // 缺译文时回退英文
            if (args == null || args.Length == 0) return s;
            try { return string.Format(s, args); }
            catch { return s; }
        }

        /// <summary>自检：返回缺少译文的 “键/语言” 列表。</summary>
        public static List<string> MissingTranslations()
        {
            List<string> missing = new List<string>();
            foreach (KeyValuePair<string, string[]> kv in Table)
            {
                for (int i = 0; i < Languages.Length; i++)
                {
                    if (kv.Value[i] == null || kv.Value[i].Length == 0)
                        missing.Add(kv.Key + "/" + Languages[i].Code);
                }
            }
            return missing;
        }

        public static int KeyCount { get { return Table.Count; } }

        // ---------------- 语言选择持久化 ----------------

        private static string SettingsPath()
        {
            string dir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrEmpty(dir)) dir = AppDomain.CurrentDomain.BaseDirectory;
            return Path.Combine(Path.Combine(dir, "SteamACFManager"), "settings.ini");
        }

        private static string ReadSavedLanguage()
        {
            try
            {
                string path = SettingsPath();
                if (!File.Exists(path)) return null;
                foreach (string line in File.ReadAllLines(path))
                {
                    string t = line.Trim();
                    if (t.StartsWith("lang=", StringComparison.OrdinalIgnoreCase)) return t.Substring(5).Trim();
                }
            }
            catch { }
            return null;
        }

        public static void SaveLanguage()
        {
            try
            {
                string path = SettingsPath();
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(path, "lang=" + CurrentCode + Environment.NewLine, new UTF8Encoding(false));
            }
            catch { }
        }

        // ---------------- 词条表 ----------------
        // 顺序：en, zh-CN, zh-TW, ja, ko, es, de, ru

        private static void Add(string key, string en, string zhCN, string zhTW, string ja, string ko, string es, string de, string ru)
        {
            Table[key] = new string[] { en, zhCN, zhTW, ja, ko, es, de, ru };
        }

        static Loc()
        {
            // ---- 界面骨架 ----
            Add("app.title", "Steam ACF Manager", "Steam ACF 管理器", "Steam ACF 管理器", "Steam ACF マネージャー", "Steam ACF 관리자", "Administrador de ACF de Steam", "Steam ACF-Manager", "Менеджер ACF Steam");
            Add("ui.steamRoot", "Steam: {0}", "Steam: {0}", "Steam: {0}", "Steam: {0}", "Steam: {0}", "Steam: {0}", "Steam: {0}", "Steam: {0}");
            Add("ui.cacheOk", "metadata cache appinfo.vdf: {0} apps", "本地元数据缓存 appinfo.vdf：已载入 {0} 个 App", "本機中繼資料快取 appinfo.vdf：已載入 {0} 個 App", "ローカルメタデータキャッシュ appinfo.vdf：{0} 件のアプリ", "로컬 메타데이터 캐시 appinfo.vdf: 앱 {0}개", "caché de metadatos appinfo.vdf: {0} aplicaciones", "Metadaten-Cache appinfo.vdf: {0} Apps", "кэш метаданных appinfo.vdf: приложений — {0}");
            Add("ui.cacheBad", "metadata cache appinfo.vdf: unavailable ({0})", "本地元数据缓存 appinfo.vdf：不可用（{0}）", "本機中繼資料快取 appinfo.vdf：無法使用（{0}）", "ローカルメタデータキャッシュ appinfo.vdf：利用不可（{0}）", "로컬 메타데이터 캐시 appinfo.vdf: 사용 불가({0})", "caché de metadatos appinfo.vdf: no disponible ({0})", "Metadaten-Cache appinfo.vdf: nicht verfügbar ({0})", "кэш метаданных appinfo.vdf: недоступен ({0})");
            Add("ui.language", "Language:", "语言：", "語言：", "言語：", "언어:", "Idioma:", "Sprache:", "Язык:");
            Add("ui.rescan", "Rescan", "重新扫描", "重新掃描", "再スキャン", "다시 검색", "Reanalizar", "Neu scannen", "Пересканировать");
            Add("ui.repairOne", "Repair / Generate Selected", "修复/生成选中项", "修復/產生選取項", "選択項目を修復/生成", "선택 항목 복구/생성", "Reparar / generar selección", "Auswahl reparieren/erzeugen", "Исправить/создать выбранное");
            Add("ui.repairAll", "Fix All Issues", "修复全部问题项", "修復全部問題項", "問題項目をすべて修復", "모든 문제 항목 복구", "Corregir todo", "Alle Probleme beheben", "Исправить все проблемы");
            Add("ui.exportOne", "Export Selected", "导出选中项", "匯出選取項", "選択項目を書き出し", "선택 항목 내보내기", "Exportar selección", "Auswahl exportieren", "Экспортировать выбранное");
            Add("ui.exportAllOk", "Export All OK", "导出全部正常项", "匯出全部正常項", "正常な項目をすべて書き出し", "정상 항목 모두 내보내기", "Exportar todo lo correcto", "Alle OK exportieren", "Экспортировать все исправные");
            Add("ui.openExport", "Open Export Folder", "打开导出目录", "開啟匯出目錄", "書き出しフォルダを開く", "내보내기 폴더 열기", "Abrir carpeta de exportación", "Exportordner öffnen", "Открыть папку экспорта");
            Add("ui.ready", "Ready", "就绪", "就緒", "準備完了", "준비 완료", "Listo", "Bereit", "Готово");
            Add("ui.scanning", "Scanning… measuring every game folder to verify file completeness, this can take a few seconds", "正在扫描… 会统计每个游戏目录的体积以校验文件是否完整，可能需要几秒", "正在掃描… 會統計每個遊戲目錄的容量以驗證檔案是否完整，可能需要幾秒", "スキャン中… フォルダ容量を測って完全性を検証します（数秒かかる場合があります）", "검사 중… 파일 완전성을 확인하기 위해 폴더 용량을 측정합니다(몇 초 걸릴 수 있음)", "Analizando… se mide cada carpeta para verificar la integridad (puede tardar unos segundos)", "Scanne… Ordnergrößen werden zur Prüfung gemessen (kann einige Sekunden dauern)", "Сканирование… измеряю размеры папок для проверки целостности (может занять несколько секунд)");
            Add("ui.scanFailed", "Scan failed: {0}", "扫描失败：{0}", "掃描失敗：{0}", "スキャンに失敗しました：{0}", "검사 실패: {0}", "Error al analizar: {0}", "Scan fehlgeschlagen: {0}", "Ошибка сканирования: {0}");
            Add("ui.summary", "Ready — {0} entries: {1} installed / {2} ACF damaged / {3} missing ACF / {4} unverified / {5} leftover folders",
                "就绪 — 共 {0} 个条目：{1} 个已安装 / {2} 个 ACF 损坏 / {3} 个确认缺 ACF / {4} 个未验证 / {5} 个残留目录",
                "就緒 — 共 {0} 個項目：{1} 個已安裝 / {2} 個 ACF 損毀 / {3} 個確認缺 ACF / {4} 個未驗證 / {5} 個殘留目錄",
                "準備完了 — {0} 件：インストール済み {1} / ACF 破損 {2} / ACF 欠落 {3} / 未検証 {4} / 残骸フォルダ {5}",
                "준비 완료 — 항목 {0}개: 설치됨 {1} / ACF 손상 {2} / ACF 누락 {3} / 미검증 {4} / 잔여 폴더 {5}",
                "Listo — {0} entradas: {1} instalados / {2} ACF dañados / {3} sin ACF / {4} sin verificar / {5} carpetas residuales",
                "Bereit — {0} Einträge: {1} installiert / {2} ACF beschädigt / {3} ACF fehlt / {4} ungeprüft / {5} Restordner",
                "Готово — записей: {0}; установлено {1} / ACF повреждён {2} / нет ACF {3} / не проверено {4} / остаточных папок {5}");
            Add("ui.working", "Working on {0} (AppID {1})…", "正在处理 {0}（AppID {1}）…", "正在處理 {0}（AppID {1}）…", "{0}（AppID {1}）を処理中…", "{0}(AppID {1}) 처리 중…", "Procesando {0} (AppID {1})…", "Verarbeite {0} (AppID {1})…", "Обработка {0} (AppID {1})…");
            Add("ui.searchingStore", "Searching the Steam Store by folder name…", "正在按文件夹名搜索 Steam 商店…", "正在依資料夾名稱搜尋 Steam 商店…", "フォルダ名で Steam ストアを検索中…", "폴더 이름으로 Steam 상점 검색 중…", "Buscando en la tienda de Steam por nombre de carpeta…", "Suche im Steam-Shop nach Ordnernamen…", "Поиск в магазине Steam по имени папки…");
            Add("ui.nameWithDir", "{0} (folder: {1})", "{0}（目录 {1}）", "{0}（資料夾 {1}）", "{0}（フォルダ {1}）", "{0}(폴더 {1})", "{0} (carpeta: {1})", "{0} (Ordner: {1})", "{0} (папка: {1})");

            // ---- 表格列 ----
            Add("col.status", "Status", "状态", "狀態", "状態", "상태", "Estado", "Status", "Статус");
            Add("col.appid", "AppID", "AppID", "AppID", "AppID", "AppID", "AppID", "AppID", "AppID");
            Add("col.name", "Name", "名称", "名稱", "名前", "이름", "Nombre", "Name", "Название");
            Add("col.library", "Library", "库", "庫", "ライブラリ", "라이브러리", "Biblioteca", "Bibliothek", "Библиотека");
            Add("col.stateflags", "StateFlags", "StateFlags", "StateFlags", "StateFlags", "StateFlags", "StateFlags", "StateFlags", "StateFlags");
            Add("col.size", "Size", "大小", "大小", "サイズ", "크기", "Tamaño", "Größe", "Размер");
            Add("col.buildid", "buildid", "buildid", "buildid", "buildid", "buildid", "buildid", "buildid", "buildid");
            Add("col.note", "Notes", "说明", "說明", "説明", "설명", "Notas", "Hinweise", "Примечание");
            Add("tip.stateflags.intro", "StateFlags is a bit mask reported by Steam. 4 means “fully installed”, which is what makes the Play button appear.",
                "StateFlags 是 Steam 的状态码（位掩码）。4 表示“已完整安装”，也就是能显示「开始游戏」的状态。",
                "StateFlags 是 Steam 的狀態碼（位元遮罩）。4 表示「已完整安裝」，也就是能顯示「開始遊戲」的狀態。",
                "StateFlags は Steam の状態コード（ビットマスク）です。4 は「完全インストール済み」で、再生ボタンが表示される状態です。",
                "StateFlags는 Steam의 상태 코드(비트 마스크)입니다. 4는 '완전 설치됨'으로 실행 버튼이 표시되는 상태입니다.",
                "StateFlags es una máscara de bits de Steam. 4 significa «instalado por completo», el estado que muestra el botón Jugar.",
                "StateFlags ist eine Bitmaske von Steam. 4 bedeutet „vollständig installiert“ – dann erscheint der Play-Button.",
                "StateFlags — битовая маска Steam. 4 означает «полностью установлено» — именно тогда появляется кнопка «Играть».");
            Add("tip.stateflags.footer", "Common values: 4 = installed (Play), 6 = installed but an update is pending, 2 = update required, 1024/1048576 = downloading.",
                "常见取值：4 = 已安装（可开始游戏）、6 = 已安装但有待更新、2 = 需要更新、1024/1048576 = 正在下载。",
                "常見值：4 = 已安裝（可開始遊戲）、6 = 已安裝但有待更新、2 = 需要更新、1024/1048576 = 正在下載。",
                "よくある値：4 = インストール済み（プレイ可）、6 = 更新あり、2 = 更新が必要、1024/1048576 = ダウンロード中。",
                "자주 쓰는 값: 4 = 설치됨(실행 가능), 6 = 업데이트 대기, 2 = 업데이트 필요, 1024/1048576 = 다운로드 중.",
                "Valores habituales: 4 = instalado (Jugar), 6 = instalado con actualización pendiente, 2 = requiere actualización, 1024/1048576 = descargando.",
                "Häufige Werte: 4 = installiert (Play), 6 = installiert, Update ausstehend, 2 = Update nötig, 1024/1048576 = wird geladen.",
                "Типичные значения: 4 — установлено («Играть»), 6 — установлено, ждёт обновления, 2 — нужно обновление, 1024/1048576 — загрузка.");

            // ---- 状态名称 ----
            Add("st.installed", "✅ Installed", "✅ 已安装", "✅ 已安裝", "✅ インストール済み", "✅ 설치됨", "✅ Instalado", "✅ Installiert", "✅ Установлено");
            Add("st.damaged", "⚠ Needs repair", "⚠ 需修复", "⚠ 需修復", "⚠ 要修復", "⚠ 복구 필요", "⚠ Requiere reparación", "⚠ Reparatur nötig", "⚠ Нужен ремонт");
            Add("st.missingAcf", "⚠ Missing ACF", "⚠ 缺 ACF", "⚠ 缺 ACF", "⚠ ACF 欠落", "⚠ ACF 누락", "⚠ Falta ACF", "⚠ ACF fehlt", "⚠ Нет ACF");
            Add("st.unverified", "？ Unverified", "？ 未验证", "？ 未驗證", "？ 未検証", "？ 미검증", "？ Sin verificar", "？ Ungeprüft", "？ Не проверено");
            Add("st.residue", "✖ Leftovers", "✖ 残留目录", "✖ 殘留目錄", "✖ 残骸フォルダ", "✖ 잔여 폴더", "✖ Restos", "✖ Restordner", "✖ Остатки");
            Add("st.empty", "✖ Empty folder", "✖ 空文件夹", "✖ 空資料夾", "✖ 空フォルダ", "✖ 빈 폴더", "✖ Carpeta vacía", "✖ Leerer Ordner", "✖ Пустая папка");
            // 命令行版用纯 ASCII 标记，避免在不支持 emoji 的终端里变成乱码
            Add("st.cli.installed", "[ OK ] Installed", "[ OK ] 已安装", "[ OK ] 已安裝", "[ OK ] インストール済み", "[ OK ] 설치됨", "[ OK ] Instalado", "[ OK ] Installiert", "[ OK ] Установлено");
            Add("st.cli.damaged", "[FIX ] Needs repair", "[修复] 需修复", "[修復] 需修復", "[修正] 要修復", "[복구] 복구 필요", "[REP] Requiere reparación", "[REP] Reparatur nötig", "[РЕМ] Нужен ремонт");
            Add("st.cli.missingAcf", "[GEN ] Missing ACF", "[生成] 缺 ACF", "[產生] 缺 ACF", "[生成] ACF 欠落", "[생성] ACF 누락", "[GEN] Falta ACF", "[GEN] ACF fehlt", "[СОЗ] Нет ACF");
            Add("st.cli.unverified", "[ ?? ] Unverified", "[ ?  ] 未验证", "[ ?  ] 未驗證", "[ ?? ] 未検証", "[ ?? ] 미검증", "[ ?? ] Sin verificar", "[ ?? ] Ungeprüft", "[ ?? ] Не проверено");
            Add("st.cli.residue", "[SKIP] Leftovers", "[跳过] 残留目录", "[跳過] 殘留目錄", "[除外] 残骸フォルダ", "[제외] 잔여 폴더", "[OMIT] Restos", "[ÜBER] Restordner", "[ПРОП] Остатки");
            Add("st.cli.empty", "[SKIP] Empty folder", "[跳过] 空文件夹", "[跳過] 空資料夾", "[除外] 空フォルダ", "[제외] 빈 폴더", "[OMIT] Carpeta vacía", "[ÜBER] Leerer Ordner", "[ПРОП] Пустая папка");

            // ---- 内容校验（AcfCore） ----
            Add("reason.empty", "Empty folder (0 files / 0 bytes): there are no game files at all. This is an uninstall leftover, not a missing ACF.",
                "空文件夹（0 个文件 / 0 字节）：没有任何游戏文件，属于卸载残留，不是「缺少 ACF」",
                "空資料夾（0 個檔案 / 0 位元組）：沒有任何遊戲檔案，屬於解除安裝殘留，不是「缺少 ACF」",
                "空フォルダ（0 ファイル / 0 バイト）：ゲームファイルが一切ありません。アンインストールの残骸で、ACF 欠落ではありません。",
                "빈 폴더(파일 0개 / 0바이트): 게임 파일이 전혀 없습니다. 제거 후 남은 잔여물이며 ACF 누락이 아닙니다.",
                "Carpeta vacía (0 archivos / 0 bytes): no hay archivos del juego. Es un resto de desinstalación, no un ACF faltante.",
                "Leerer Ordner (0 Dateien / 0 Bytes): keine Spieldateien vorhanden. Ein Deinstallationsrest, kein fehlendes ACF.",
                "Пустая папка (0 файлов / 0 байт): игровых файлов нет вовсе. Это остаток после удаления, а не отсутствие ACF.");
            Add("reason.tiny", "Incomplete: the whole folder is only {0} in {1} file(s), far smaller than any complete install — uninstall leftover.",
                "游戏文件不完整：整目录只有 {0}（{1} 个文件），远小于任何完整游戏安装，只是卸载残留",
                "遊戲檔案不完整：整個資料夾只有 {0}（{1} 個檔案），遠小於任何完整遊戲安裝，只是解除安裝殘留",
                "不完全：フォルダ全体で {0}（{1} ファイル）しかなく、完全なインストールとは考えられません（アンインストールの残骸）。",
                "불완전: 폴더 전체가 {0}(파일 {1}개)뿐이라 완전한 설치가 아닙니다. 제거 후 잔여물입니다.",
                "Incompleto: la carpeta entera ocupa solo {0} en {1} archivo(s), mucho menos que una instalación completa: resto de desinstalación.",
                "Unvollständig: der Ordner umfasst nur {0} in {1} Datei(en) — viel zu wenig für eine vollständige Installation (Deinstallationsrest).",
                "Неполно: вся папка занимает лишь {0} в {1} файл(ах) — это остаток после удаления, а не установка.");
            Add("reason.noBasis", "Cannot verify: there is no known full-size baseline for this game, so completeness cannot be confirmed.",
                "无法校验：缺少该游戏的完整体积基准，无法确认文件是否齐全",
                "無法驗證：缺少該遊戲的完整容量基準，無法確認檔案是否齊全",
                "検証できません：完全なサイズの基準がないため、ファイルの完全性を確認できません。",
                "검증할 수 없음: 이 게임의 완전한 용량 기준이 없어 파일 완전성을 확인할 수 없습니다.",
                "No se puede verificar: no hay una referencia de tamaño completo para este juego.",
                "Nicht prüfbar: keine bekannte Vollgröße für dieses Spiel, Vollständigkeit nicht bestätigbar.",
                "Не удаётся проверить: нет эталонного полного размера для этой игры.");
            Add("reason.numbers", "measured {0} / expected {1} ({2}), baseline: {3}",
                "实测 {0} / 预期 {1}（{2}），基准来源：{3}",
                "實測 {0} / 預期 {1}（{2}），基準來源：{3}",
                "実測 {0} / 予想 {1}（{2}）、基準：{3}",
                "측정 {0} / 예상 {1}({2}), 기준: {3}",
                "medido {0} / esperado {1} ({2}), referencia: {3}",
                "gemessen {0} / erwartet {1} ({2}), Basis: {3}",
                "измерено {0} / ожидается {1} ({2}), эталон: {3}");
            Add("reason.incompleteLower", "Game files incomplete: {0} (some depots have unknown size, so the real expectation is even larger).",
                "游戏文件不完整：{0}（还有 depot 体积未知，实际期望只会更大）",
                "遊戲檔案不完整：{0}（還有 depot 容量未知，實際期望只會更大）",
                "ゲームファイルが不完全です：{0}（サイズ不明の depot があり、実際の必要量はさらに大きくなります）。",
                "게임 파일이 불완전합니다: {0} (크기를 알 수 없는 depot이 있어 실제 필요량은 더 큽니다).",
                "Archivos del juego incompletos: {0} (hay depots de tamaño desconocido, así que lo esperado es aún mayor).",
                "Spieldateien unvollständig: {0} (einige Depots haben unbekannte Größe, der echte Bedarf ist größer).",
                "Игровые файлы неполны: {0} (размер части depot неизвестен, реальный объём больше).");
            Add("reason.lowerOnly", "Cannot confirm completeness: {0} (only a lower bound is known for this game).",
                "无法确认完整性：{0}（该游戏有 depot 体积未知，只能确认不低于下界）",
                "無法確認完整性：{0}（該遊戲有 depot 容量未知，只能確認不低於下界）",
                "完全性を確認できません：{0}（サイズ不明の depot があり、下限のみ確認できます）。",
                "완전성을 확인할 수 없습니다: {0} (크기를 알 수 없는 depot이 있어 하한만 확인 가능).",
                "No se puede confirmar la integridad: {0} (solo se conoce un límite inferior para este juego).",
                "Vollständigkeit nicht bestätigbar: {0} (nur eine Untergrenze bekannt).",
                "Целостность не подтверждена: {0} (известна только нижняя граница).");
            Add("reason.incomplete", "Game files incomplete: {0} — only saves/logs/mods/patches are left, so no ACF may be generated.",
                "游戏文件不完整：{0}：目录里只有存档 / 日志 / 模组 / 补丁等残留，不能生成 ACF",
                "遊戲檔案不完整：{0}：目錄裡只有存檔 / 日誌 / 模組 / 補丁等殘留，不能產生 ACF",
                "ゲームファイルが不完全です：{0} — セーブ／ログ／MOD／パッチなどの残骸しかなく、ACF を生成できません。",
                "게임 파일이 불완전합니다: {0} — 세이브/로그/모드/패치 등 잔여물만 있어 ACF를 생성할 수 없습니다.",
                "Archivos del juego incompletos: {0} — solo quedan partidas, registros, mods o parches; no se puede generar un ACF.",
                "Spieldateien unvollständig: {0} — nur Speicherstände/Logs/Mods/Patches übrig, kein ACF erzeugbar.",
                "Игровые файлы неполны: {0} — остались только сохранения/логи/моды/патчи, ACF создавать нельзя.");
            Add("reason.complete", "Files complete: {0}", "文件完整：{0}", "檔案完整：{0}", "ファイルは完全です：{0}", "파일 완전함: {0}", "Archivos completos: {0}", "Dateien vollständig: {0}", "Файлы целы: {0}");
            Add("reason.completeNoisy", " (much larger than expected — may include mods or extra files)", "（实测明显偏大，可能含模组或额外文件）", "（實測明顯偏大，可能含模組或額外檔案）", "（実測がかなり大きく、MOD や追加ファイルの可能性）", " (측정값이 훨씬 커 모드나 추가 파일 가능성)", " (bastante mayor de lo esperado: puede incluir mods o archivos extra)", " (deutlich größer als erwartet — evtl. Mods oder Zusatzdateien)", " (заметно больше ожидаемого — возможно, моды или лишние файлы)");
            Add("reason.onlyAcfMissing", ", only the ACF is missing (can be generated)", "，仅缺 ACF（可生成）", "，僅缺 ACF（可產生）", "、ACF だけが欠落しています（生成可能）", ", ACF만 누락되었습니다(생성 가능)", ", solo falta el ACF (se puede generar)", ", nur das ACF fehlt (kann erzeugt werden)", ", отсутствует только ACF (можно создать)");
            Add("reason.unverifiedHint", ". If you are sure this is a complete install, specify the AppID manually and scan again.", "。若确认这里是完整安装，请手动指定 AppID 后重新扫描/生成。", "。若確認這裡是完整安裝，請手動指定 AppID 後重新掃描/產生。", "。ここが完全なインストールだと確信できる場合は、AppID を手動指定して再スキャンしてください。", ". 이곳이 완전한 설치라고 확신하면 AppID를 직접 지정한 뒤 다시 검사하세요.", ". Si estás seguro de que es una instalación completa, indica el AppID manualmente y vuelve a analizar.", ". Wenn dies eine vollständige Installation ist, AppID manuell angeben und erneut scannen.", ". Если это точно полная установка, укажите AppID вручную и повторите сканирование.");
            Add("reason.dupStillThere", "This folder looks complete, but another library ({0}) already has an ACF for the same AppID and still contains the game — generating another ACF would make Steam see two installs.", "该目录文件完整，但同一个 AppID 在另一个库已经有 ACF（{0}）：那边也确实装着同一款游戏，再生成一份会让 Steam 认为是两份安装。", "該資料夾檔案完整，但同一個 AppID 在另一個庫已經有 ACF（{0}）：那邊確實也裝著同一款遊戲，再產生一份會讓 Steam 認為是兩份安裝。", "このフォルダは完全に見えますが、別のライブラリ（{0}）に同じ AppID の ACF があり、実際にゲームも入っています。もう一つ作ると Steam は二重インストールと認識します。", "이 폴더는 완전해 보이지만 다른 라이브러리({0})에 같은 AppID의 ACF가 있고 게임도 있습니다. 하나 더 만들면 Steam이 두 개의 설치로 인식합니다.", "Esta carpeta parece completa, pero otra biblioteca ({0}) ya tiene un ACF del mismo AppID y también el juego: generar otro ACF haría que Steam vea dos instalaciones.", "Der Ordner wirkt vollständig, aber eine andere Bibliothek ({0}) hat bereits ein ACF mit derselben AppID und enthält das Spiel — ein zweites ACF würde als Doppelinstallation gelten.", "Папка выглядит целой, но в другой библиотеке ({0}) уже есть ACF с этим AppID и сам игровой каталог — второй ACF будет воспринят как две установки.");
            Add("reason.dupStale", "This folder looks complete, but another library ({0}) has an ACF for the same AppID while its game folder is gone — usually a leftover ACF after moving the library. Delete that ACF first, then generate here.", "该目录文件完整，但同一个 AppID 在另一个库已经有 ACF（{0}）：而那个库里的游戏目录并不存在，多半是搬盘/复制后残留的旧 ACF；请先删除那个 ACF，再回来生成。", "該資料夾檔案完整，但同一個 AppID 在另一個庫已經有 ACF（{0}）：而那個庫裡的遊戲目錄並不存在，多半是搬移磁碟/複製後殘留的舊 ACF；請先刪除該 ACF，再回來產生。", "このフォルダは完全に見えますが、別のライブラリ（{0}）に同じ AppID の ACF があり、ゲームフォルダは存在しません。ライブラリ移動後の残骸と思われます。先にその ACF を削除してください。", "이 폴더는 완전해 보이지만 다른 라이브러리({0})에 같은 AppID의 ACF가 있고 게임 폴더는 없습니다. 라이브러리 이동 후 남은 ACF일 가능성이 큽니다. 먼저 그 ACF를 삭제하세요.", "Esta carpeta parece completa, pero otra biblioteca ({0}) tiene un ACF del mismo AppID y su carpeta de juego ya no existe: suele ser un ACF residual tras mover la biblioteca. Borra ese ACF y vuelve a generar.", "Der Ordner wirkt vollständig, aber eine andere Bibliothek ({0}) hat ein ACF mit derselben AppID, während der Spielordner fehlt — meist ein Rest nach dem Verschieben. Erst dieses ACF löschen.", "Папка выглядит целой, но в другой библиотеке ({0}) есть ACF с этим AppID, а самого каталога игры нет — вероятно, остаток после переноса. Сначала удалите тот ACF.");
            Add("src.appinfo", "Steam metadata cache appinfo.vdf (sum of this app's required depots)", "Steam 本地缓存 appinfo.vdf（该 AppID 本体 depot 体积合计）", "Steam 本機快取 appinfo.vdf（該 AppID 本體 depot 容量合計）", "Steam ローカルキャッシュ appinfo.vdf（必須 depot の合計サイズ）", "Steam 로컬 캐시 appinfo.vdf(필수 depot 합계)", "caché local de Steam appinfo.vdf (suma de depots necesarios)", "Steam-Cache appinfo.vdf (Summe der nötigen Depots)", "локальный кэш Steam appinfo.vdf (сумма обязательных depot)");
            Add("src.templateAcf", "SizeOnDisk recorded by an existing ACF of the same AppID", "同 AppID 现有 ACF 的 SizeOnDisk", "同 AppID 現有 ACF 的 SizeOnDisk", "同じ AppID の既存 ACF の SizeOnDisk", "같은 AppID 기존 ACF의 SizeOnDisk", "SizeOnDisk de un ACF existente del mismo AppID", "SizeOnDisk eines vorhandenen ACF mit gleicher AppID", "SizeOnDisk существующего ACF с тем же AppID");
            Add("src.oldAcf", "SizeOnDisk recorded in the current ACF", "原 ACF 记录的 SizeOnDisk", "原 ACF 記錄的 SizeOnDisk", "既存 ACF に記録された SizeOnDisk", "기존 ACF에 기록된 SizeOnDisk", "SizeOnDisk registrado en el ACF actual", "im aktuellen ACF gespeichertes SizeOnDisk", "SizeOnDisk из текущего ACF");
            Add("src.steamcmd", "steamcmd app_info_print (buildid={0})", "steamcmd app_info_print（buildid={0}）", "steamcmd app_info_print（buildid={0}）", "steamcmd app_info_print（buildid={0}）", "steamcmd app_info_print(buildid={0})", "steamcmd app_info_print (buildid={0})", "steamcmd app_info_print (buildid={0})", "steamcmd app_info_print (buildid={0})");
            Add("src.contentLog", "content_log.txt (no buildid and no per-depot sizes)", "content_log.txt（没有 buildid 与每 depot 体积）", "content_log.txt（沒有 buildid 與各 depot 容量）", "content_log.txt（buildid と depot 別サイズなし）", "content_log.txt(buildid 및 depot별 크기 없음)", "content_log.txt (sin buildid ni tamaños por depot)", "content_log.txt (keine buildid, keine Depot-Größen)", "content_log.txt (нет buildid и размеров depot)");
            Add("appid.suffix", " (AppID {0}, source: {1})", "（AppID {0}，来源：{1}）", "（AppID {0}，來源：{1}）", "（AppID {0}、取得元：{1}）", " (AppID {0}, 출처: {1})", " (AppID {0}, origen: {1})", " (AppID {0}, Quelle: {1})", " (AppID {0}, источник: {1})");
            Add("appid.unknown", " (AppID could not be determined)", "（未能确定 AppID）", "（未能確定 AppID）", "（AppID を特定できません）", " (AppID를 확인할 수 없음)", " (no se pudo determinar el AppID)", " (AppID nicht ermittelbar)", " (AppID не определён)");
            Add("appid.from.appidtxt", "steam_appid.txt inside the folder", "目录内的 steam_appid.txt", "資料夾內的 steam_appid.txt", "フォルダ内の steam_appid.txt", "폴더 안의 steam_appid.txt", "steam_appid.txt dentro de la carpeta", "steam_appid.txt im Ordner", "steam_appid.txt в папке");
            Add("appid.from.installdir", "installdir index of Steam's local cache", "Steam 本地缓存的 installdir 索引", "Steam 本機快取的 installdir 索引", "Steam ローカルキャッシュの installdir 索引", "Steam 로컬 캐시의 installdir 색인", "índice installdir de la caché local de Steam", "installdir-Index des Steam-Caches", "индекс installdir локального кэша Steam");
            Add("appid.from.name", "game name index of Steam's local cache", "Steam 本地缓存的游戏名索引", "Steam 本機快取的遊戲名稱索引", "Steam ローカルキャッシュのゲーム名索引", "Steam 로컬 캐시의 게임 이름 색인", "índice de nombres de la caché local de Steam", "Spielnamen-Index des Steam-Caches", "индекс имён игр локального кэша Steam");
            Add("appid.from.loose", "loose name match in Steam's local cache (spaces/punctuation ignored)", "Steam 本地缓存的宽松名称匹配（忽略空格/标点）", "Steam 本機快取的寬鬆名稱比對（忽略空格/標點）", "Steam ローカルキャッシュの緩い名前一致（空白・記号を無視）", "Steam 로컬 캐시의 느슨한 이름 일치(공백/기호 무시)", "coincidencia flexible de nombre en la caché local (ignora espacios y signos)", "unscharfer Namensabgleich im Steam-Cache (Leerzeichen/Satzzeichen ignoriert)", "нестрогое совпадение имени в кэше Steam (без пробелов и знаков)");
            Add("appid.from.otheracf", "ACF with the same installdir in another library", "其它库里同名 installdir 的 ACF", "其它庫裡同名 installdir 的 ACF", "別ライブラリの同じ installdir の ACF", "다른 라이브러리의 같은 installdir ACF", "ACF con el mismo installdir en otra biblioteca", "ACF mit gleichem installdir in anderer Bibliothek", "ACF с тем же installdir в другой библиотеке");
            Add("classify.noDepots", "InstalledDepots is empty", "InstalledDepots 为空", "InstalledDepots 為空", "InstalledDepots が空です", "InstalledDepots가 비어 있음", "InstalledDepots está vacío", "InstalledDepots ist leer", "InstalledDepots пуст");
            Add("classify.noInstalledBit", "StateFlags is missing the installed bit (4), current={0}", "StateFlags 缺少已安装位(4)，当前={0}", "StateFlags 缺少已安裝位(4)，目前={0}", "StateFlags にインストール済みビット(4)がありません（現在={0}）", "StateFlags에 설치 비트(4)가 없습니다. 현재={0}", "A StateFlags le falta el bit de instalado (4), actual={0}", "StateFlags ohne Installiert-Bit (4), aktuell={0}", "В StateFlags нет бита «установлено» (4), текущее={0}");
            Add("classify.sizeZero", "SizeOnDisk=0", "SizeOnDisk=0", "SizeOnDisk=0", "SizeOnDisk=0", "SizeOnDisk=0", "SizeOnDisk=0", "SizeOnDisk=0", "SizeOnDisk=0");
            Add("classify.buildZero", "buildid=0", "buildid=0", "buildid=0", "buildid=0", "buildid=0", "buildid=0", "buildid=0", "buildid=0");
            Add("classify.parseError", "ACF cannot be parsed: {0}", "ACF 无法解析：{0}", "ACF 無法解析：{0}", "ACF を解析できません：{0}", "ACF를 해석할 수 없습니다: {0}", "No se puede analizar el ACF: {0}", "ACF kann nicht gelesen werden: {0}", "Не удалось разобрать ACF: {0}");
            Add("classify.needsUpdate", "Steam marks this install as “update required” (StateFlags={0}); the ACF itself parses fine, just start Steam and apply the update.", "Steam 标记为「需要更新」（StateFlags={0}）：ACF 本身可正常解析，启动 Steam 后按提示更新即可", "Steam 標記為「需要更新」（StateFlags={0}）：ACF 本身可正常解析，啟動 Steam 後依提示更新即可", "Steam は「更新が必要」と認識しています（StateFlags={0}）。ACF 自体は正常です。Steam を起動して更新してください。", "Steam이 '업데이트 필요'로 표시함(StateFlags={0}). ACF 자체는 정상이며 Steam을 실행해 업데이트하면 됩니다.", "Steam marca esta instalación como «actualización pendiente» (StateFlags={0}); el ACF está bien, solo inicia Steam y actualiza.", "Steam meldet „Update erforderlich“ (StateFlags={0}); das ACF ist in Ordnung, einfach Steam starten und aktualisieren.", "Steam помечает установку как «требуется обновление» (StateFlags={0}); сам ACF корректен — запустите Steam и обновите.");

            // ---- depot 组合 / 处理日志 ----
            Add("log.depotSource", "depot list source: {0}", "depot 清单来源：{0}", "depot 清單來源：{0}", "depot リストの取得元：{0}", "depot 목록 출처: {0}", "origen de la lista de depots: {0}", "Quelle der Depot-Liste: {0}", "источник списка depot: {0}");
            Add("log.appid", "appid={0}  name={1}", "appid={0}  名称={1}", "appid={0}  名稱={1}", "appid={0}  名前={1}", "appid={0}  이름={1}", "appid={0}  nombre={1}", "appid={0}  Name={1}", "appid={0}  имя={1}");
            Add("log.folder", "folder={0}", "目录={0}", "資料夾={0}", "フォルダ={0}", "폴더={0}", "carpeta={0}", "Ordner={0}", "папка={0}");
            Add("log.contentCheck", "content check: {0}", "内容校验：{0}", "內容驗證：{0}", "内容チェック：{0}", "내용 검사: {0}", "verificación de contenido: {0}", "Inhaltsprüfung: {0}", "проверка содержимого: {0}");
            Add("log.sizes", "SizeOnDisk={0}  buildid={1}  StateFlags={2}", "SizeOnDisk={0}  buildid={1}  StateFlags={2}", "SizeOnDisk={0}  buildid={1}  StateFlags={2}", "SizeOnDisk={0}  buildid={1}  StateFlags={2}", "SizeOnDisk={0}  buildid={1}  StateFlags={2}", "SizeOnDisk={0}  buildid={1}  StateFlags={2}", "SizeOnDisk={0}  buildid={1}  StateFlags={2}", "SizeOnDisk={0}  buildid={1}  StateFlags={2}");
            Add("log.writeTarget", "write to: {0}", "写入位置：{0}", "寫入位置：{0}", "書き込み先：{0}", "기록 위치: {0}", "se escribirá en: {0}", "Schreibziel: {0}", "запись в: {0}");
            Add("log.writeBackup", "write to: {0} (original backed up as {1})", "写入位置：{0}（原文件备份为 {1}）", "寫入位置：{0}（原檔案備份為 {1}）", "書き込み先：{0}（元ファイルは {1} にバックアップ）", "기록 위치: {0} (원본은 {1}로 백업)", "se escribirá en: {0} (original guardado como {1})", "Schreibziel: {0} (Original gesichert als {1})", "запись в: {0} (оригинал сохранён как {1})");
            Add("log.writeNew", "write to: {0} (new file, no backup)", "写入位置：{0}（新文件，无备份）", "寫入位置：{0}（新檔案，無備份）", "書き込み先：{0}（新規ファイル、バックアップなし）", "기록 위치: {0} (새 파일, 백업 없음)", "se escribirá en: {0} (archivo nuevo, sin copia)", "Schreibziel: {0} (neue Datei, kein Backup)", "запись в: {0} (новый файл, без копии)");
            Add("log.depotKept", "depot set kept from the existing ACF ({0} depots: kept {1} / filled {2})", "depot 组合沿用原 ACF（{0} 个：保留 {1} 个 / 补全 {2} 个）", "depot 組合沿用原 ACF（{0} 個：保留 {1} 個 / 補全 {2} 個）", "depot 構成は既存 ACF を踏襲（{0} 個：保持 {1} / 補完 {2}）", "depot 구성은 기존 ACF 유지({0}개: 유지 {1} / 보완 {2})", "conjunto de depots tomado del ACF existente ({0}: conservados {1} / completados {2})", "Depot-Satz aus vorhandenem ACF ({0}: {1} behalten / {2} ergänzt)", "набор depot взят из существующего ACF ({0}: сохранено {1} / дополнено {2})");
            Add("log.depotRebuilt", "the existing ACF had no InstalledDepots; rebuilt from the current public branch ({0} depots)", "原 ACF 的 InstalledDepots 为空，已按当前 public 分支的 depot 重建（{0} 个）", "原 ACF 的 InstalledDepots 為空，已依目前 public 分支的 depot 重建（{0} 個）", "既存 ACF の InstalledDepots が空のため、現在の public ブランチから再構築（{0} 個）", "기존 ACF의 InstalledDepots가 비어 있어 현재 public 브랜치로 재구성({0}개)", "el ACF existente no tenía InstalledDepots; reconstruido desde la rama public actual ({0} depots)", "vorhandenes ACF ohne InstalledDepots; aus aktuellem public-Zweig neu aufgebaut ({0} Depots)", "в существующем ACF не было InstalledDepots; набор пересобран по ветке public ({0} depot)");
            Add("log.depotFromTemplate", "depot set taken from the existing ACF of the same AppID ({0} depots), manifests refreshed to the current public branch", "depot 组合沿用同 AppID 现有 ACF（{0} 个），manifest 刷新为当前 public 分支", "depot 組合沿用同 AppID 現有 ACF（{0} 個），manifest 更新為目前 public 分支", "同じ AppID の既存 ACF から depot 構成を採用（{0} 個）、マニフェストは現在の public に更新", "같은 AppID의 기존 ACF에서 depot 구성을 가져옴({0}개), 매니페스트는 현재 public으로 갱신", "conjunto de depots tomado del ACF existente del mismo AppID ({0}), manifiestos actualizados a la rama public actual", "Depot-Satz aus vorhandenem ACF derselben AppID ({0}), Manifeste auf aktuellen public-Zweig aktualisiert", "набор depot взят из существующего ACF того же AppID ({0}), манифесты обновлены до ветки public");
            Add("log.depotPicked", "depot set derived from the required depots in Steam's cache ({0} depots)", "depot 组合按 Steam 缓存的必需 depot 推导（{0} 个）", "depot 組合依 Steam 快取的必需 depot 推導（{0} 個）", "Steam キャッシュの必須 depot から構成を導出（{0} 個）", "Steam 캐시의 필수 depot으로 구성 도출({0}개)", "conjunto de depots derivado de los depots necesarios de la caché ({0})", "Depot-Satz aus den nötigen Depots des Steam-Caches abgeleitet ({0})", "набор depot выведен из обязательных depot кэша ({0})");
            Add("log.depotOldManifest", "depot {0}: the on-disk manifest ({1}) is older than the current public one ({2}); the on-disk version was kept and Steam will update when needed", "depot {0}：磁盘上是旧版本清单 {1}，缓存里当前 public 是 {2}；已保留磁盘版本，Steam 需要时再更新", "depot {0}：磁碟上是舊版本清單 {1}，快取裡目前 public 是 {2}；已保留磁碟版本，Steam 需要時再更新", "depot {0}：ディスク上のマニフェスト {1} は現在の public（{2}）より古いため、ディスク側を保持しました（Steam が必要時に更新します）", "depot {0}: 디스크의 매니페스트 {1}이 현재 public({2})보다 오래되어 디스크 버전을 유지했습니다(Steam이 필요 시 업데이트).", "depot {0}: el manifiesto en disco ({1}) es más antiguo que el public actual ({2}); se conservó el de disco y Steam actualizará si hace falta", "Depot {0}: Manifest auf der Platte ({1}) ist älter als das aktuelle public ({2}); Plattenversion behalten, Steam aktualisiert bei Bedarf", "depot {0}: манифест на диске ({1}) старее текущего public ({2}); оставлен дисковый вариант, Steam обновит при необходимости");
            Add("log.depotFixed", "depot {0}: the recorded manifest ({1}) was invalid, replaced with the current public version", "depot {0}：原 ACF 的 manifest 无效（{1}），已用当前 public 版本", "depot {0}：原 ACF 的 manifest 無效（{1}），已改用目前 public 版本", "depot {0}：記録されたマニフェスト（{1}）が無効のため、現在の public に置き換えました", "depot {0}: 기록된 매니페스트({1})가 유효하지 않아 현재 public으로 교체했습니다", "depot {0}: el manifiesto registrado ({1}) no era válido; sustituido por el public actual", "Depot {0}: gespeichertes Manifest ({1}) ungültig, durch aktuelles public ersetzt", "depot {0}: записанный манифест ({1}) недействителен, заменён текущим public");
            Add("log.depotKeptInvalid", "depot {0}: manifest invalid and not in the cache either; kept as-is (Steam will validate)", "depot {0}：manifest 无效且缓存里也没有，已保留原值（Steam 会自行校验）", "depot {0}：manifest 無效且快取裡也沒有，已保留原值（Steam 會自行驗證）", "depot {0}：マニフェストが無効でキャッシュにもないため、そのまま保持（Steam が検証します）", "depot {0}: 매니페스트가 유효하지 않고 캐시에도 없어 그대로 유지(Steam이 검증)", "depot {0}: manifiesto no válido y ausente en la caché; se mantiene (Steam lo verificará)", "Depot {0}: Manifest ungültig und nicht im Cache; unverändert übernommen (Steam prüft)", "depot {0}: манифест недействителен и отсутствует в кэше; оставлен как есть (Steam проверит)");
            Add("log.langPack", "language depot {0} ({1}, {2})", "语言包 depot {0}（{1}，{2}）", "語言包 depot {0}（{1}，{2}）", "言語 depot {0}（{1}、{2}）", "언어 depot {0}({1}, {2})", "depot de idioma {0} ({1}, {2})", "Sprach-Depot {0} ({1}, {2})", "языковой depot {0} ({1}, {2})");
            Add("log.dlcSkipped", "{0} DLC depots were not written into the ACF (ownership cannot be verified offline); Steam will validate/download them as needed", "{0} 个 DLC depot 未写入 ACF（无法离线确认是否拥有该 DLC），Steam 会在需要时自行校验/下载", "{0} 個 DLC depot 未寫入 ACF（無法離線確認是否擁有該 DLC），Steam 會在需要時自行驗證/下載", "{0} 個の DLC depot は ACF に書き込みませんでした（所有権をオフラインで確認できないため）。Steam が必要時に検証/取得します", "DLC depot {0}개는 ACF에 기록하지 않았습니다(오프라인에서 소유 확인 불가). Steam이 필요 시 검증/다운로드합니다", "{0} depots de DLC no se escribieron en el ACF (no se puede verificar su propiedad sin conexión); Steam los validará o descargará si hace falta", "{0} DLC-Depots wurden nicht ins ACF geschrieben (Besitz offline nicht prüfbar); Steam validiert/lädt bei Bedarf", "{0} DLC-depot не записаны в ACF (владение нельзя проверить офлайн); Steam проверит или загрузит их при необходимости");

            // ---- 拒绝/错误 ----
            Add("err.noAcfForAppId", "No ACF found for appid {0}.", "未找到 appid {0} 的 ACF 文件。", "找不到 appid {0} 的 ACF 檔案。", "appid {0} の ACF が見つかりません。", "appid {0}의 ACF 파일을 찾을 수 없습니다.", "No se encontró el ACF del appid {0}.", "Kein ACF für AppID {0} gefunden.", "ACF для appid {0} не найден.");
            Add("err.noBuildId", "Cannot determine the current public buildid for appid {0}; aborting so that Steam never sees an ACF it would reject.\n(A buildid of 0 makes Steam believe the install needs an update and re-download the whole game.)",
                "无法确定 AppID {0} 当前 public 分支的 buildid，为避免写出 Steam 不认的 ACF，已中止。\n（buildid=0 的 ACF 会让 Steam 认为该安装需要更新，进而重新下载整个游戏。）",
                "無法確定 AppID {0} 目前 public 分支的 buildid，為避免寫出 Steam 不認的 ACF，已中止。\n（buildid=0 的 ACF 會讓 Steam 認為該安裝需要更新，進而重新下載整個遊戲。）",
                "appid {0} の現在の public buildid を特定できないため中止しました（Steam が拒否する ACF を書かないため）。\n（buildid=0 の ACF は Steam に更新が必要と誤認させ、全体を再ダウンロードさせます。）",
                "appid {0}의 현재 public buildid를 확인할 수 없어 중단했습니다(Steam이 거부할 ACF를 쓰지 않기 위함).\n(buildid=0인 ACF는 Steam이 업데이트가 필요하다고 오인해 전체를 다시 받게 합니다.)",
                "No se puede determinar el buildid public actual del appid {0}; se cancela para no escribir un ACF que Steam rechace.\n(Un buildid=0 hace que Steam crea que falta una actualización y descargue todo de nuevo.)",
                "Der aktuelle public-buildid für AppID {0} ist nicht ermittelbar; Abbruch, damit Steam kein ungültiges ACF sieht.\n(Ein buildid=0 lässt Steam ein Update vermuten und das ganze Spiel neu laden.)",
                "Не удалось определить текущий public buildid для appid {0}; операция прервана, чтобы не создать ACF, который Steam отвергнет.\n(buildid=0 заставит Steam считать установку требующей обновления и заново скачать игру.)");
            Add("err.refuseRepair", "Repair refused: {0}\n\nFolder: {1}\n{2}\n\nOnly when the files are verified complete does the tool write “fully installed” into the ACF.",
                "拒绝修复：{0}\n\n目录：{1}\n{2}\n\n只有确认文件完整时才会把 ACF 写成「已完整安装」。",
                "拒絕修復：{0}\n\n資料夾：{1}\n{2}\n\n只有在確認檔案完整時才會把 ACF 寫成「已完整安裝」。",
                "修復を拒否しました：{0}\n\nフォルダ：{1}\n{2}\n\nファイルが完全であると確認できた場合のみ「完全インストール済み」として書き込みます。",
                "복구 거부: {0}\n\n폴더: {1}\n{2}\n\n파일이 완전하다고 확인된 경우에만 ACF에 '완전 설치됨'을 기록합니다.",
                "Reparación rechazada: {0}\n\nCarpeta: {1}\n{2}\n\nSolo se escribe «instalado por completo» cuando los archivos están verificados.",
                "Reparatur abgelehnt: {0}\n\nOrdner: {1}\n{2}\n\nNur bei geprüft vollständigen Dateien wird „vollständig installiert“ eingetragen.",
                "Ремонт отклонён: {0}\n\nПапка: {1}\n{2}\n\n«Полностью установлено» записывается только при подтверждённой целостности файлов.");
            Add("err.repairHint", "If this folder is really just leftovers, uninstall the game in Steam and delete the folder (or let Steam download it again) and retry.",
                "若这里其实只剩残留，请在 Steam 里卸载该游戏并删除该残留目录（或让 Steam 重新下载）后再处理。",
                "若這裡其實只剩殘留，請在 Steam 裡解除安裝該遊戲並刪除該殘留目錄（或讓 Steam 重新下載）後再處理。",
                "ここが実際には残骸だけの場合は、Steam でゲームをアンインストールしてこのフォルダを削除（または Steam に再取得させる）してから再試行してください。",
                "이곳이 사실 잔여물뿐이라면 Steam에서 게임을 제거하고 폴더를 삭제한 뒤(또는 Steam이 다시 받도록 한 뒤) 다시 시도하세요.",
                "Si esta carpeta solo contiene restos, desinstala el juego en Steam, borra la carpeta (o deja que Steam lo descargue de nuevo) y vuelve a intentarlo.",
                "Wenn hier nur Reste liegen: das Spiel in Steam deinstallieren, den Ordner löschen (oder Steam neu laden lassen) und erneut versuchen.",
                "Если это действительно остатки — удалите игру в Steam, удалите папку (или дайте Steam загрузить заново) и повторите.");
            Add("err.noAppId", "Cannot determine the AppID of this game folder.\n\nEither:\n1) put a steam_appid.txt containing the AppID into the folder, or\n2) type the AppID in the UI.",
                "无法确定该游戏文件夹的 AppID。\n\n可任选其一：\n1) 在游戏文件夹里放一个 steam_appid.txt，内容为 AppID；\n2) 在界面里手动输入 AppID。",
                "無法確定該遊戲資料夾的 AppID。\n\n可任選其一：\n1) 在遊戲資料夾裡放一個 steam_appid.txt，內容為 AppID；\n2) 在介面裡手動輸入 AppID。",
                "このゲームフォルダの AppID を特定できません。\n\nいずれかを行ってください：\n1) フォルダに AppID を書いた steam_appid.txt を置く\n2) 画面で AppID を手入力する",
                "이 게임 폴더의 AppID를 확인할 수 없습니다.\n\n다음 중 하나를 하세요:\n1) 폴더에 AppID를 적은 steam_appid.txt를 둡니다.\n2) 화면에서 AppID를 직접 입력합니다.",
                "No se puede determinar el AppID de esta carpeta.\n\nOpciones:\n1) colocar un steam_appid.txt con el AppID en la carpeta, o\n2) escribir el AppID en la interfaz.",
                "Die AppID dieses Ordners ist nicht ermittelbar.\n\nEntweder:\n1) eine steam_appid.txt mit der AppID in den Ordner legen oder\n2) die AppID in der Oberfläche eingeben.",
                "Не удалось определить AppID этой папки.\n\nВарианты:\n1) положить в папку steam_appid.txt с AppID;\n2) ввести AppID в интерфейсе.");
            Add("err.noDepotInfo", "Cannot obtain the depot list for appid {0}:\n1) it is not in Steam's local cache appinfo.vdf{1};\n2) steamcmd is unavailable or the fetch failed (the GUI can download steamcmd when online);\n3) there is no install record for it in content_log.txt either.\n\nTip: start Steam once so it refreshes appcache (or, when online, run “Verify integrity of game files” for that game in Steam) and retry.",
                "无法获取 AppID {0} 的 depot 清单：\n1) Steam 本地缓存 appinfo.vdf 中没有该 AppID{1}；\n2) steamcmd 不可用或拉取失败（图形版在联网时会尝试下载 steamcmd）；\n3) content_log.txt 里也没有该游戏的安装记录。\n\n建议：先启动一次 Steam 让它更新 appcache（或联网后在 Steam 里对该游戏点一次「验证游戏完整性」），再重试。",
                "無法取得 AppID {0} 的 depot 清單：\n1) Steam 本機快取 appinfo.vdf 中沒有該 AppID{1}；\n2) steamcmd 無法使用或取得失敗（圖形版在連線時會嘗試下載 steamcmd）；\n3) content_log.txt 裡也沒有該遊戲的安裝記錄。\n\n建議：先啟動一次 Steam 讓它更新 appcache（或連線後在 Steam 裡對該遊戲點一次「驗證遊戲檔案完整性」），再重試。",
                "appid {0} の depot リストを取得できません：\n1) Steam のローカルキャッシュ appinfo.vdf に該当 AppID がない{1}\n2) steamcmd が使えない、または取得に失敗（GUI 版はオンライン時に steamcmd を取得します）\n3) content_log.txt にもインストール記録がない\n\nヒント：Steam を一度起動して appcache を更新（またはオンラインで「ゲームファイルの整合性を確認」）してから再試行してください。",
                "appid {0}의 depot 목록을 가져올 수 없습니다:\n1) Steam 로컬 캐시 appinfo.vdf에 해당 AppID가 없음{1}\n2) steamcmd를 사용할 수 없거나 가져오기 실패(온라인 시 GUI가 steamcmd 다운로드 시도)\n3) content_log.txt에도 설치 기록이 없음\n\n팁: Steam을 한 번 실행해 appcache를 갱신한 뒤(또는 온라인에서 '게임 파일 무결성 확인') 다시 시도하세요.",
                "No se puede obtener la lista de depots del appid {0}:\n1) no está en la caché local appinfo.vdf{1};\n2) steamcmd no está disponible o falló la consulta (la versión con interfaz puede descargarlo si hay conexión);\n3) tampoco hay registro de instalación en content_log.txt.\n\nSugerencia: inicia Steam una vez para refrescar appcache (o, con conexión, usa «Verificar integridad de los archivos» en Steam) y reintenta.",
                "Depot-Liste für AppID {0} nicht verfügbar:\n1) nicht im lokalen Cache appinfo.vdf{1};\n2) steamcmd fehlt oder Abruf fehlgeschlagen (die GUI kann steamcmd online laden);\n3) auch in content_log.txt kein Installationsdatensatz.\n\nTipp: Steam einmal starten, damit appcache aktualisiert wird (oder online „Integrität der Spieldateien überprüfen“) und erneut versuchen.",
                "Не удалось получить список depot для appid {0}:\n1) его нет в локальном кэше appinfo.vdf{1};\n2) steamcmd недоступен или запрос не удался (GUI может скачать steamcmd при наличии сети);\n3) в content_log.txt тоже нет записи об установке.\n\nСовет: запустите Steam, чтобы обновить appcache (или онлайн — «Проверить целостность файлов игры») и повторите.");
            Add("err.cacheUnavailableNote", " (cache unavailable: {0})", "（缓存不可用：{0}）", "（快取無法使用：{0}）", "（キャッシュ利用不可：{0}）", " (캐시 사용 불가: {0})", " (caché no disponible: {0})", " (Cache nicht verfügbar: {0})", " (кэш недоступен: {0})");
            Add("err.targetAcfExists", "An ACF for this AppID already exists: {0}\n(This is not a “missing ACF” case — use Repair instead.)", "该 AppID 的 ACF 已经存在：{0}\n（说明这里并不是「缺少 ACF」，请改用「修复」功能。）", "該 AppID 的 ACF 已經存在：{0}\n（說明這裡並不是「缺少 ACF」，請改用「修復」功能。）", "この AppID の ACF は既に存在します：{0}\n（ACF 欠落ではありません。「修復」を使ってください。）", "이 AppID의 ACF가 이미 있습니다: {0}\n(ACF 누락이 아니므로 '복구'를 사용하세요.)", "Ya existe un ACF para este AppID: {0}\n(No es un caso de «falta ACF»; usa Reparar).", "Ein ACF für diese AppID existiert bereits: {0}\n(Kein „ACF fehlt“-Fall — bitte Reparieren verwenden.)", "ACF для этого AppID уже существует: {0}\n(Это не случай «нет ACF» — используйте «Исправить».)");
            Add("err.duplicateOtherLib", "Refusing to generate: an ACF for the same AppID ({0}) already exists in another library:\n  library: {1}\n  ACF: {2}\n  game folder there: {3}\n\nIf that library really still has the game, a second ACF would make Steam see two installs (delete this copy if it is duplicated).\nIf that ACF is a leftover after moving the library, delete it first and generate again here.",
                "拒绝生成：同一个 AppID（{0}）在另一个库已经存在 ACF：\n  库：{1}\n  ACF：{2}\n  那边的游戏目录：{3}\n\n那边确实装着同一款游戏时，再生成一份会让 Steam 认为是两份安装（若是复制出来的副本，请删除这里）。\n若那边的 ACF 是搬盘/复制后留下的过期文件，请先删除它，再回来生成。",
                "拒絕產生：同一個 AppID（{0}）在另一個庫已經存在 ACF：\n  庫：{1}\n  ACF：{2}\n  那邊的遊戲目錄：{3}\n\n那邊確實裝著同一款遊戲時，再產生一份會讓 Steam 認為是兩份安裝（若是複製出來的副本，請刪除這裡）。\n若那邊的 ACF 是搬移磁碟/複製後留下的過期檔案，請先刪除它，再回來產生。",
                "生成を拒否：同じ AppID（{0}）の ACF が別のライブラリに既にあります：\n  ライブラリ：{1}\n  ACF：{2}\n  そちらのゲームフォルダ：{3}\n\nそちらにも実体がある場合、二つ目を作ると Steam は二重インストールと認識します（複製ならここを削除してください）。\n移動後の残骸なら、先にその ACF を削除してから生成してください。",
                "생성 거부: 같은 AppID({0})의 ACF가 다른 라이브러리에 이미 있습니다:\n  라이브러리: {1}\n  ACF: {2}\n  그쪽 게임 폴더: {3}\n\n그쪽에도 게임이 있다면 하나 더 만들면 Steam이 두 개의 설치로 인식합니다(복사본이면 이쪽을 삭제하세요).\n이동 후 남은 ACF라면 먼저 삭제한 뒤 생성하세요.",
                "Generación rechazada: ya existe un ACF del mismo AppID ({0}) en otra biblioteca:\n  biblioteca: {1}\n  ACF: {2}\n  carpeta del juego allí: {3}\n\nSi allí también está el juego, un segundo ACF haría que Steam vea dos instalaciones (borra esta copia si es duplicada).\nSi ese ACF es un resto de mover la biblioteca, bórralo primero y genera de nuevo.",
                "Erzeugen abgelehnt: ein ACF mit derselben AppID ({0}) existiert bereits in einer anderen Bibliothek:\n  Bibliothek: {1}\n  ACF: {2}\n  Spielordner dort: {3}\n\nLiegt das Spiel dort wirklich, würde ein zweites ACF als Doppelinstallation gelten (diese Kopie löschen, falls dupliziert).\nIst jenes ACF ein Rest nach dem Verschieben, zuerst löschen und dann hier erzeugen.",
                "Создание отклонено: ACF с тем же AppID ({0}) уже есть в другой библиотеке:\n  библиотека: {1}\n  ACF: {2}\n  каталог игры там: {3}\n\nЕсли там действительно есть игра, второй ACF будет воспринят как две установки (удалите эту копию, если она дублирует).\nЕсли тот ACF — остаток после переноса, сначала удалите его, затем создавайте здесь.");
            Add("err.duplicateFolderExists", "(exists)", "（存在）", "（存在）", "（存在）", "(있음)", "(existe)", "(vorhanden)", "(есть)");
            Add("err.duplicateFolderMissing", "(missing)", "（不存在）", "（不存在）", "（存在しない）", "(없음)", "(no existe)", "(fehlt)", "(отсутствует)");
            Add("err.refuseEmpty", "Refusing to generate: {0}\n\nFolder: {1}", "拒绝生成 ACF：{0}\n\n目录：{1}", "拒絕產生 ACF：{0}\n\n資料夾：{1}", "生成を拒否：{0}\n\nフォルダ：{1}", "생성 거부: {0}\n\n폴더: {1}", "Generación rechazada: {0}\n\nCarpeta: {1}", "Erzeugen abgelehnt: {0}\n\nOrdner: {1}", "Создание отклонено: {0}\n\nПапка: {1}");
            Add("err.refuseIncomplete", "Refusing to generate: {0}\n\nFolder: {1}\n\nThis folder only holds uninstall leftovers (saves / logs / mods / patches), not a complete install.\nForcing an ACF would make Steam believe the game is installed and download the whole game again.",
                "拒绝生成 ACF：{0}\n\n目录：{1}\n\n说明：这个目录只是卸载残留（存档 / 日志 / 模组 / 补丁等），并不是完整的游戏安装。\n强行生成 ACF 只会让 Steam 认为游戏已安装，然后去重新下载整个游戏。",
                "拒絕產生 ACF：{0}\n\n資料夾：{1}\n\n說明：這個資料夾只是解除安裝殘留（存檔 / 日誌 / 模組 / 補丁等），並不是完整的遊戲安裝。\n強行產生 ACF 只會讓 Steam 認為遊戲已安裝，然後重新下載整個遊戲。",
                "生成を拒否：{0}\n\nフォルダ：{1}\n\nこのフォルダはアンインストールの残骸（セーブ／ログ／MOD／パッチ）だけで、完全なインストールではありません。\n無理に ACF を作ると Steam はインストール済みと誤認し、ゲーム全体を再ダウンロードします。",
                "생성 거부: {0}\n\n폴더: {1}\n\n이 폴더는 제거 후 남은 잔여물(세이브/로그/모드/패치)일 뿐이며 완전한 설치가 아닙니다.\n억지로 ACF를 만들면 Steam이 설치된 것으로 오인해 게임 전체를 다시 받습니다.",
                "Generación rechazada: {0}\n\nCarpeta: {1}\n\nEsta carpeta solo contiene restos de desinstalación (partidas, registros, mods, parches), no una instalación completa.\nForzar el ACF haría que Steam crea que el juego está instalado y lo descargue entero otra vez.",
                "Erzeugen abgelehnt: {0}\n\nOrdner: {1}\n\nDieser Ordner enthält nur Deinstallationsreste (Speicherstände, Logs, Mods, Patches), keine vollständige Installation.\nEin erzwungenes ACF ließe Steam das Spiel als installiert ansehen und alles neu laden.",
                "Создание отклонено: {0}\n\nПапка: {1}\n\nЗдесь только остатки после удаления (сохранения, логи, моды, патчи), а не полная установка.\nПринудительный ACF заставит Steam считать игру установленной и заново её скачать.");
            Add("err.refuseUnverified", "Refusing to generate: {0}\n\nFolder: {1}\n\nCompleteness could not be confirmed, so this cannot be treated as “only the ACF is missing”.",
                "拒绝生成 ACF：{0}\n\n目录：{1}\n\n说明：无法确认文件是否完整，就不能认定「只是缺 ACF」。",
                "拒絕產生 ACF：{0}\n\n資料夾：{1}\n\n說明：無法確認檔案是否完整，就不能認定「只是缺 ACF」。",
                "生成を拒否：{0}\n\nフォルダ：{1}\n\n完全性を確認できないため、「ACF だけが欠落」とは判断できません。",
                "생성 거부: {0}\n\n폴더: {1}\n\n완전성을 확인할 수 없어 'ACF만 누락'으로 볼 수 없습니다.",
                "Generación rechazada: {0}\n\nCarpeta: {1}\n\nNo se pudo confirmar la integridad, así que no puede tratarse como «solo falta el ACF».",
                "Erzeugen abgelehnt: {0}\n\nOrdner: {1}\n\nVollständigkeit nicht bestätigt — kein „nur das ACF fehlt“-Fall.",
                "Создание отклонено: {0}\n\nПапка: {1}\n\nЦелостность не подтверждена — это нельзя считать случаем «нет только ACF».");
            Add("err.noUsableDepots", "Refusing to generate: Steam's cache has no usable depot manifest for appid {0}.\nStart Steam once so it refreshes appcache and retry.", "拒绝生成 ACF：Steam 缓存里没有 AppID {0} 可用的 depot manifest。\n请先启动一次 Steam 让它更新 appcache，再重试。", "拒絕產生 ACF：Steam 快取裡沒有 AppID {0} 可用的 depot manifest。\n請先啟動一次 Steam 讓它更新 appcache，再重試。", "生成を拒否：Steam キャッシュに appid {0} の有効な depot マニフェストがありません。\nSteam を一度起動して appcache を更新してから再試行してください。", "생성 거부: Steam 캐시에 appid {0}의 사용 가능한 depot 매니페스트가 없습니다.\nSteam을 한 번 실행해 appcache를 갱신한 뒤 다시 시도하세요.", "Generación rechazada: la caché de Steam no tiene manifiestos de depot utilizables para el appid {0}.\nInicia Steam una vez para refrescar appcache y reintenta.", "Erzeugen abgelehnt: kein nutzbares Depot-Manifest für AppID {0} im Steam-Cache.\nSteam einmal starten (appcache aktualisieren) und erneut versuchen.", "Создание отклонено: в кэше Steam нет пригодного манифеста depot для appid {0}.\nЗапустите Steam для обновления appcache и повторите.");
            Add("err.notActionable", "This entry cannot be repaired (nothing to do):\n{0}", "该条目不是可修复的问题项（无需处理）：\n{0}", "該項目不是可修復的問題項（無需處理）：\n{0}", "この項目は修復対象ではありません（対応不要）：\n{0}", "이 항목은 복구 대상이 아닙니다(조치 불필요):\n{0}", "Esta entrada no es reparable (nada que hacer):\n{0}", "Dieser Eintrag ist nicht reparierbar (nichts zu tun):\n{0}", "Эта запись не подлежит ремонту (действий не требуется):\n{0}");

            // ---- 对话框 ----
            Add("dlg.selectRow", "Please select a row first.", "请先在列表里选中一行。", "請先在清單中選取一列。", "先に一覧から行を選択してください。", "먼저 목록에서 행을 선택하세요.", "Selecciona primero una fila.", "Bitte zuerst eine Zeile auswählen.", "Сначала выберите строку в списке.");
            Add("dlg.acfOk", "{0}: the ACF is fine, nothing to repair.", "{0}：ACF 正常，无需修复。", "{0}：ACF 正常，無需修復。", "{0}：ACF は正常です。修復は不要です。", "{0}: ACF가 정상이라 복구할 필요가 없습니다.", "{0}: el ACF está bien, no hay nada que reparar.", "{0}: ACF ist in Ordnung, nichts zu reparieren.", "{0}: ACF в порядке, ремонт не нужен.");
            Add("dlg.nothingToDoTitle", "Nothing to do", "无需处理", "無需處理", "対応不要", "조치 불필요", "Nada que hacer", "Nichts zu tun", "Действий не требуется");
            Add("dlg.notActionableBody", "This folder is not a repairable item, so no ACF will be generated.\n\n{0}\n\nFolder: {1}\n\nIt only contains uninstall leftovers (the game files are gone). Generating an ACF would make Steam believe the game is installed and download the whole game again. If you do not need it, you can simply delete the folder.",
                "这个目录不是待修复项，不能生成 ACF。\n\n{0}\n\n目录：{1}\n\n它只是卸载残留（游戏本体文件已经不在），生成 ACF 只会让 Steam 认为游戏已安装，然后重新下载整个游戏。确认无用后可以直接删除该目录。",
                "這個資料夾不是待修復項，不能產生 ACF。\n\n{0}\n\n資料夾：{1}\n\n它只是解除安裝殘留（遊戲本體檔案已經不在），產生 ACF 只會讓 Steam 認為遊戲已安裝，然後重新下載整個遊戲。確認無用後可以直接刪除該資料夾。",
                "このフォルダは修復対象ではないため、ACF を生成しません。\n\n{0}\n\nフォルダ：{1}\n\nアンインストールの残骸（本体ファイルは既にありません）です。ACF を作ると Steam はインストール済みと誤認し、ゲーム全体を再ダウンロードします。不要なら削除して構いません。",
                "이 폴더는 복구 대상이 아니므로 ACF를 생성하지 않습니다.\n\n{0}\n\n폴더: {1}\n\n제거 후 남은 잔여물입니다(본체 파일 없음). ACF를 만들면 Steam이 설치된 것으로 오인해 게임 전체를 다시 받습니다. 필요 없으면 삭제해도 됩니다.",
                "Esta carpeta no es reparable, no se generará ningún ACF.\n\n{0}\n\nCarpeta: {1}\n\nSolo contiene restos de desinstalación (los archivos del juego ya no están). Generar un ACF haría que Steam crea que está instalado y lo descargue entero. Si no lo necesitas, puedes borrar la carpeta.",
                "Dieser Ordner ist kein reparierbarer Eintrag, es wird kein ACF erzeugt.\n\n{0}\n\nOrdner: {1}\n\nEr enthält nur Deinstallationsreste (die Spieldateien fehlen). Ein ACF würde Steam glauben lassen, das Spiel sei installiert, und alles neu laden. Wenn nicht benötigt, kann der Ordner gelöscht werden.",
                "Эта папка не подлежит ремонту, ACF создаваться не будет.\n\n{0}\n\nПапка: {1}\n\nЗдесь только остатки после удаления (файлов игры нет). Создание ACF заставит Steam считать игру установленной и заново её скачать. Если не нужно — папку можно удалить.");
            Add("dlg.noAppIdTitle", "Notice", "提示", "提示", "お知らせ", "알림", "Aviso", "Hinweis", "Внимание");
            Add("dlg.acfNoAppId", "This ACF has no AppID, so it cannot be repaired.", "该 ACF 无法解析出 AppID，无法修复。", "該 ACF 無法解析出 AppID，無法修復。", "この ACF から AppID を取得できないため修復できません。", "이 ACF에서 AppID를 확인할 수 없어 복구할 수 없습니다.", "Este ACF no tiene AppID, no se puede reparar.", "Dieses ACF enthält keine AppID und kann nicht repariert werden.", "В этом ACF нет AppID — ремонт невозможен.");
            Add("action.repair", "repair", "修复", "修復", "修復", "복구", "reparar", "reparieren", "исправить");
            Add("action.generate", "generate", "生成", "產生", "生成", "생성", "generar", "erzeugen", "создать");
            Add("dlg.confirmTitle", "Confirm: {0}", "确认{0}", "確認{0}", "{0}の確認", "{0} 확인", "Confirmar: {0}", "Bestätigen: {0}", "Подтвердите: {0}");
            Add("dlg.confirmRepair", "Repair the ACF of {0} (AppID {1})?\n\nThe current file will first be backed up as {2}.bak.\nbuildid and depot manifests are taken from Steam's local cache (current public branch).\n\nMake sure Steam is closed.",
                "确定要修复 {0}（AppID {1}）的 ACF 吗？\n\n将先备份原文件为 {2}.bak，再写入修复后的 ACF。\n写出的 buildid 与 depot manifest 取自 Steam 本地缓存（当前 public 分支）。\n\n请确保 Steam 已关闭。",
                "確定要修復 {0}（AppID {1}）的 ACF 嗎？\n\n將先備份原檔案為 {2}.bak，再寫入修復後的 ACF。\n寫出的 buildid 與 depot manifest 取自 Steam 本機快取（目前 public 分支）。\n\n請確保 Steam 已關閉。",
                "{0}（AppID {1}）の ACF を修復しますか？\n\n元ファイルは {2}.bak としてバックアップされます。\nbuildid と depot マニフェストは Steam のローカルキャッシュ（現在の public ブランチ）から取得します。\n\nSteam を終了しておいてください。",
                "{0}(AppID {1})의 ACF를 복구할까요?\n\n기존 파일은 {2}.bak로 백업됩니다.\nbuildid와 depot 매니페스트는 Steam 로컬 캐시(현재 public 브랜치)에서 가져옵니다.\n\nSteam을 종료해 주세요.",
                "¿Reparar el ACF de {0} (AppID {1})?\n\nEl archivo actual se guardará como {2}.bak.\nEl buildid y los manifiestos de depot provienen de la caché local de Steam (rama public actual).\n\nAsegúrate de que Steam esté cerrado.",
                "ACF von {0} (AppID {1}) reparieren?\n\nDie aktuelle Datei wird zuerst als {2}.bak gesichert.\nbuildid und Depot-Manifeste stammen aus dem lokalen Steam-Cache (aktueller public-Zweig).\n\nSteam muss geschlossen sein.",
                "Исправить ACF для {0} (AppID {1})?\n\nТекущий файл будет сохранён как {2}.bak.\nbuildid и манифесты depot берутся из локального кэша Steam (ветка public).\n\nУбедитесь, что Steam закрыт.");
            Add("dlg.confirmGenerate", "Generate an ACF for {0} (AppID {1})?\n\nIt will be written to {2}.\nbuildid and depot manifests are taken from Steam's local cache (current public branch).\nIf the files on disk are an older build, Steam will verify and patch them on the next start.\n\nMake sure Steam is closed.",
                "确定要生成 {0}（AppID {1}）的 ACF 吗？\n\n将写入 {2}。\n写出的 buildid 与 depot manifest 取自 Steam 本地缓存（当前 public 分支）。\n若磁盘上的文件其实是旧版本，Steam 会在下次启动时自行校验并补齐，不影响使用。\n\n请确保 Steam 已关闭。",
                "確定要產生 {0}（AppID {1}）的 ACF 嗎？\n\n將寫入 {2}。\n寫出的 buildid 與 depot manifest 取自 Steam 本機快取（目前 public 分支）。\n若磁碟上的檔案其實是舊版本，Steam 會在下次啟動時自行驗證並補齊，不影響使用。\n\n請確保 Steam 已關閉。",
                "{0}（AppID {1}）の ACF を生成しますか？\n\n書き込み先：{2}\nbuildid と depot マニフェストは Steam のローカルキャッシュ（現在の public ブランチ）から取得します。\nディスク上のファイルが古いビルドの場合、Steam が次回起動時に検証・更新します。\n\nSteam を終了しておいてください。",
                "{0}(AppID {1})의 ACF를 생성할까요?\n\n기록 위치: {2}\nbuildid와 depot 매니페스트는 Steam 로컬 캐시(현재 public 브랜치)에서 가져옵니다.\n디스크의 파일이 오래된 빌드라면 Steam이 다음 실행 시 검증하고 보완합니다.\n\nSteam을 종료해 주세요.",
                "¿Generar un ACF para {0} (AppID {1})?\n\nSe escribirá en {2}.\nEl buildid y los manifiestos de depot provienen de la caché local de Steam (rama public actual).\nSi los archivos en disco son una versión anterior, Steam los verificará y completará al iniciarse.\n\nAsegúrate de que Steam esté cerrado.",
                "ACF für {0} (AppID {1}) erzeugen?\n\nSchreibziel: {2}\nbuildid und Depot-Manifeste stammen aus dem lokalen Steam-Cache (aktueller public-Zweig).\nSind die Dateien älter, prüft und ergänzt Steam sie beim nächsten Start.\n\nSteam muss geschlossen sein.",
                "Создать ACF для {0} (AppID {1})?\n\nФайл будет записан в {2}.\nbuildid и манифесты depot берутся из локального кэша Steam (ветка public).\nЕсли файлы на диске старее, Steam проверит и дополнит их при следующем запуске.\n\nУбедитесь, что Steam закрыт.");
            Add("dlg.storeHitTitle", "Confirm AppID", "确认 AppID", "確認 AppID", "AppID の確認", "AppID 확인", "Confirmar AppID", "AppID bestätigen", "Подтвердите AppID");
            Add("dlg.storeHit", "Folder name '{0}' matched this Steam Store entry:\n\n{1}\nAppID {2}\n\nUse this AppID?", "根据文件夹名 '{0}'，\nSteam 商店搜索到：\n\n{1}\nAppID {2}\n\n是否使用该 AppID？", "依資料夾名稱 '{0}'，\nSteam 商店搜尋到：\n\n{1}\nAppID {2}\n\n是否使用該 AppID？", "フォルダ名 '{0}' で Steam ストアを検索した結果：\n\n{1}\nAppID {2}\n\nこの AppID を使いますか？", "폴더 이름 '{0}'으로 Steam 상점 검색 결과:\n\n{1}\nAppID {2}\n\n이 AppID를 사용할까요?", "El nombre de carpeta '{0}' coincide con esta entrada de la tienda:\n\n{1}\nAppID {2}\n\n¿Usar este AppID?", "Ordnername '{0}' passt zu diesem Shop-Eintrag:\n\n{1}\nAppID {2}\n\nDiese AppID verwenden?", "Имя папки '{0}' совпало с записью в магазине:\n\n{1}\nAppID {2}\n\nИспользовать этот AppID?");
            Add("dlg.inputAppIdTitle", "Enter AppID", "输入 AppID", "輸入 AppID", "AppID の入力", "AppID 입력", "Introducir AppID", "AppID eingeben", "Введите AppID");
            Add("dlg.inputAppId", "Cannot determine the AppID automatically.\n\nType the AppID below (find it in the Steam store URL or on SteamDB, e.g. Cyberpunk 2077 = 1091500):\n\nGame folder: {0}", "无法自动确定 AppID。\n\n请在下方输入 AppID（可在 SteamDB 或 Steam 商店页 URL 里查到，例如《赛博朋克2077》是 1091500）：\n\n游戏文件夹: {0}", "無法自動確定 AppID。\n\n請在下方輸入 AppID（可在 SteamDB 或 Steam 商店頁 URL 裡查到，例如《電馭叛客2077》是 1091500）：\n\n遊戲資料夾: {0}", "AppID を自動で特定できません。\n\n下に AppID を入力してください（Steam ストアの URL や SteamDB で確認できます。例：Cyberpunk 2077 = 1091500）：\n\nゲームフォルダ: {0}", "AppID를 자동으로 확인할 수 없습니다.\n\n아래에 AppID를 입력하세요(Steam 상점 URL이나 SteamDB에서 확인 가능. 예: Cyberpunk 2077 = 1091500):\n\n게임 폴더: {0}", "No se puede determinar el AppID automáticamente.\n\nEscribe el AppID (está en la URL de la tienda o en SteamDB; p. ej. Cyberpunk 2077 = 1091500):\n\nCarpeta del juego: {0}", "Die AppID ist nicht automatisch ermittelbar.\n\nBitte unten eingeben (zu finden in der Shop-URL oder auf SteamDB, z. B. Cyberpunk 2077 = 1091500):\n\nSpielordner: {0}", "Не удалось определить AppID автоматически.\n\nВведите AppID (его видно в адресе страницы магазина или на SteamDB, напр. Cyberpunk 2077 = 1091500):\n\nПапка игры: {0}");
            Add("dlg.appIdNumeric", "The AppID must be a number.", "AppID 必须是数字。", "AppID 必須是數字。", "AppID は数字で入力してください。", "AppID는 숫자여야 합니다.", "El AppID debe ser un número.", "Die AppID muss eine Zahl sein.", "AppID должен быть числом.");
            Add("dlg.doneTitle", "Done", "完成", "完成", "完了", "완료", "Hecho", "Fertig", "Готово");
            Add("dlg.done", "Success!\n\nRestart Steam; the game should now show “Play”.\n\n(Repaired ACFs are backed up as .bak; generated ACFs were written into the library's steamapps folder and copied into the export folder.)\n\nDetails:\n{0}",
                "处理成功！\n\n请重启 Steam，该游戏应显示「开始游戏」。\n\n（修复的 ACF 已备份 .bak；生成的 ACF 已写入对应库的 steamapps 目录，副本也已导出到 export 目录）\n\n处理明细：\n{0}",
                "處理成功！\n\n請重新啟動 Steam，該遊戲應顯示「開始遊戲」。\n\n（修復的 ACF 已備份 .bak；產生的 ACF 已寫入對應庫的 steamapps 目錄，副本也已匯出到 export 目錄）\n\n處理明細：\n{0}",
                "成功しました。\n\nSteam を再起動すると「プレイ」が表示されるはずです。\n\n（修復した ACF は .bak としてバックアップ済み。生成した ACF は該当ライブラリの steamapps に書き込み、export にもコピーしました。）\n\n詳細：\n{0}",
                "성공했습니다.\n\nSteam을 다시 시작하면 '실행'이 표시됩니다.\n\n(복구된 ACF는 .bak로 백업되었고, 생성된 ACF는 해당 라이브러리 steamapps에 기록되고 export에도 복사되었습니다.)\n\n상세:\n{0}",
                "¡Correcto!\n\nReinicia Steam; el juego debería mostrar «Jugar».\n\n(Los ACF reparados se guardan como .bak; los generados se escribieron en steamapps de su biblioteca y se copiaron a la carpeta export.)\n\nDetalles:\n{0}",
                "Erfolg!\n\nSteam neu starten, das Spiel sollte nun „Play“ anzeigen.\n\n(Reparierte ACFs sind als .bak gesichert; erzeugte ACFs wurden in steamapps der Bibliothek geschrieben und nach export kopiert.)\n\nDetails:\n{0}",
                "Готово!\n\nПерезапустите Steam — игра должна показать «Играть».\n\n(Исправленные ACF сохранены как .bak; созданные записаны в steamapps библиотеки и скопированы в export.)\n\nПодробности:\n{0}");
            Add("dlg.failTitle", "Failed", "失败", "失敗", "失敗", "실패", "Error", "Fehlgeschlagen", "Ошибка");
            Add("dlg.failed", "Failed:\n\n{0}", "处理失败：\n\n{0}", "處理失敗：\n\n{0}", "失敗しました：\n\n{0}", "실패:\n\n{0}", "Error:\n\n{0}", "Fehlgeschlagen:\n\n{0}", "Ошибка:\n\n{0}");
            Add("dlg.batchTitle", "Batch processing", "批量处理", "批次處理", "一括処理", "일괄 처리", "Proceso por lotes", "Stapelverarbeitung", "Пакетная обработка");
            Add("dlg.batchNothing", "Nothing to repair.\n{0}", "没有需要修复的条目。\n{0}", "沒有需要修復的項目。\n{0}", "修復が必要な項目はありません。\n{0}", "복구할 항목이 없습니다.\n{0}", "No hay nada que reparar.\n{0}", "Nichts zu reparieren.\n{0}", "Нет записей для ремонта.\n{0}");
            Add("dlg.batchSkippedNote", "\n({0} leftover/empty folders were skipped: they are not complete installs, so no ACF is generated.)", "\n（已跳过 {0} 个残留/空目录：它们不是完整的游戏安装，不能生成 ACF）", "\n（已跳過 {0} 個殘留/空目錄：它們不是完整的遊戲安裝，不能產生 ACF）", "\n（残骸／空フォルダ {0} 件をスキップ：完全なインストールではないため ACF を生成しません）", "\n(잔여/빈 폴더 {0}개를 건너뛰었습니다: 완전한 설치가 아니어서 ACF를 생성하지 않습니다.)", "\n(se omitieron {0} carpetas residuales/vacías: no son instalaciones completas y no se genera ACF.)", "\n({0} Rest-/leere Ordner übersprungen: keine vollständigen Installationen, kein ACF.)", "\n(пропущено остаточных/пустых папок: {0} — это не полные установки, ACF не создаётся.)");
            Add("dlg.batchConfirm", "Process {0} entries (repair damaged ACFs and generate verified missing ACFs).\nMake sure Steam is closed. Continue?", "将处理 {0} 个条目（修复损坏 ACF + 生成确认缺失的 ACF）。\n请确保 Steam 已关闭。继续？", "將處理 {0} 個項目（修復損毀 ACF + 產生確認缺失的 ACF）。\n請確保 Steam 已關閉。繼續？", "{0} 件を処理します（破損 ACF の修復と、確認済みの欠落 ACF の生成）。\nSteam を終了しておいてください。続行しますか？", "항목 {0}개를 처리합니다(손상된 ACF 복구 + 확인된 누락 ACF 생성).\nSteam을 종료해 주세요. 계속할까요?", "Se procesarán {0} entradas (reparar ACF dañados y generar los que faltan verificados).\nAsegúrate de que Steam esté cerrado. ¿Continuar?", "{0} Einträge werden verarbeitet (beschädigte ACFs reparieren, geprüfte fehlende ACFs erzeugen).\nSteam muss geschlossen sein. Fortfahren?", "Будет обработано записей: {0} (ремонт повреждённых ACF и создание подтверждённых отсутствующих).\nУбедитесь, что Steam закрыт. Продолжить?");
            Add("dlg.batchSkippedLine", "\n\n{0} leftover/empty folders were skipped automatically (files incomplete).", "\n\n已自动跳过 {0} 个残留/空目录（文件不完整，生成 ACF 没有意义）。", "\n\n已自動跳過 {0} 個殘留/空目錄（檔案不完整，產生 ACF 沒有意義）。", "\n\n残骸／空フォルダ {0} 件は自動的にスキップしました（不完全なため）。", "\n\n잔여/빈 폴더 {0}개는 자동으로 건너뛰었습니다(파일 불완전).", "\n\nSe omitieron automáticamente {0} carpetas residuales/vacías (archivos incompletos).", "\n\n{0} Rest-/leere Ordner wurden automatisch übersprungen (unvollständig).", "\n\nАвтоматически пропущено остаточных/пустых папок: {0} (файлы неполны).");
            Add("dlg.batchManualLine", "\n\nThese {0} entries need manual confirmation and will be skipped:\n{1}", "\n\n以下 {0} 个条目需要人工确认，将被跳过：\n{1}", "\n\n以下 {0} 個項目需要人工確認，將被跳過：\n{1}", "\n\n次の {0} 件は手動確認が必要なためスキップします：\n{1}", "\n\n다음 {0}개 항목은 수동 확인이 필요해 건너뜁니다:\n{1}", "\n\nEstas {0} entradas requieren confirmación manual y se omitirán:\n{1}", "\n\nDiese {0} Einträge brauchen manuelle Bestätigung und werden übersprungen:\n{1}", "\n\nЭти записи ({0}) требуют ручного подтверждения и будут пропущены:\n{1}");
            Add("dlg.batchDone", "Batch finished: {0} succeeded, {1} failed.", "批量处理完成：成功 {0} 个，失败 {1} 个。", "批次處理完成：成功 {0} 個，失敗 {1} 個。", "一括処理が完了しました：成功 {0} 件、失敗 {1} 件。", "일괄 처리 완료: 성공 {0}개, 실패 {1}개.", "Proceso por lotes terminado: {0} correctos, {1} fallidos.", "Stapelverarbeitung beendet: {0} erfolgreich, {1} fehlgeschlagen.", "Пакетная обработка завершена: успешно {0}, с ошибкой {1}.");
            Add("dlg.batchFailLine", "\n\nFailures:\n{0}", "\n\n失败明细：\n{0}", "\n\n失敗明細：\n{0}", "\n\n失敗の詳細：\n{0}", "\n\n실패 내역:\n{0}", "\n\nFallos:\n{0}", "\n\nFehler:\n{0}", "\n\nОшибки:\n{0}");
            Add("dlg.entryNoAppId", "(ACF without AppID) {0}", "（ACF 无法解析 AppID）{0}", "（ACF 無法解析 AppID）{0}", "（AppID 不明の ACF）{0}", "(AppID 없는 ACF) {0}", "(ACF sin AppID) {0}", "(ACF ohne AppID) {0}", "(ACF без AppID) {0}");
            Add("dlg.entryIncomplete", "{0} (folder content incomplete: {1})", "{0}（目录内容不完整：{1}）", "{0}（資料夾內容不完整：{1}）", "{0}（フォルダ内容が不完全：{1}）", "{0}(폴더 내용 불완전: {1})", "{0} (contenido incompleto: {1})", "{0} (Inhalt unvollständig: {1})", "{0} (содержимое неполно: {1})");
            Add("dlg.entryNoAppIdShort", "{0} (no AppID)", "{0}（缺 AppID）", "{0}（缺 AppID）", "{0}（AppID なし）", "{0}(AppID 없음)", "{0} (sin AppID)", "{0} (keine AppID)", "{0} (нет AppID)");
            Add("dlg.exportNoAcf", "This entry has no ACF file, so it cannot be exported.", "该条目没有 ACF 文件，无法导出。", "該項目沒有 ACF 檔案，無法匯出。", "この項目には ACF がないため書き出せません。", "이 항목에는 ACF 파일이 없어 내보낼 수 없습니다.", "Esta entrada no tiene ACF, no se puede exportar.", "Dieser Eintrag hat kein ACF und kann nicht exportiert werden.", "У этой записи нет ACF — экспорт невозможен.");
            Add("dlg.exportMissing", "ACF file does not exist:\n{0}", "ACF 文件不存在：\n{0}", "ACF 檔案不存在：\n{0}", "ACF ファイルが存在しません：\n{0}", "ACF 파일이 없습니다:\n{0}", "El archivo ACF no existe:\n{0}", "ACF-Datei existiert nicht:\n{0}", "Файл ACF не существует:\n{0}");
            Add("dlg.exportTitle", "Export ACF", "导出 ACF", "匯出 ACF", "ACF の書き出し", "ACF 내보내기", "Exportar ACF", "ACF exportieren", "Экспорт ACF");
            Add("dlg.exportDone", "Exported to:\n{0}", "已导出到：\n{0}", "已匯出到：\n{0}", "書き出し先：\n{0}", "내보낸 위치:\n{0}", "Exportado a:\n{0}", "Exportiert nach:\n{0}", "Экспортировано в:\n{0}");
            Add("dlg.exportOkTitle", "Export complete", "导出成功", "匯出成功", "書き出し完了", "내보내기 완료", "Exportación completada", "Export abgeschlossen", "Экспорт завершён");
            Add("dlg.exportNoOk", "There is no “installed and OK” game to export.", "没有「已安装正常」的游戏可导出。", "沒有「已安裝正常」的遊戲可匯出。", "書き出せる「正常にインストール済み」のゲームがありません。", "'설치 및 정상' 상태의 게임이 없습니다.", "No hay ningún juego «instalado y correcto» que exportar.", "Kein „installiert und OK“-Spiel zum Exportieren.", "Нет игр в состоянии «установлено и исправно» для экспорта.");
            Add("dlg.exportChooseDir", "Choose a folder (will export {0} ACF files)", "选择保存目录（将导出 {0} 个 ACF）", "選擇儲存目錄（將匯出 {0} 個 ACF）", "保存先を選択（{0} 個の ACF を書き出します）", "저장 폴더 선택(ACF {0}개 내보내기)", "Elige una carpeta (se exportarán {0} ACF)", "Ordner wählen ({0} ACFs werden exportiert)", "Выберите папку (будет экспортировано ACF: {0})");
            Add("dlg.exportAllDone", "Exported {0} ACF files to:\n{1}", "已导出 {0} 个 ACF 到：\n{1}", "已匯出 {0} 個 ACF 到：\n{1}", "{0} 個の ACF を書き出しました：\n{1}", "ACF {0}개를 내보냈습니다:\n{1}", "Se exportaron {0} ACF a:\n{1}", "{0} ACFs exportiert nach:\n{1}", "Экспортировано ACF: {0} в:\n{1}");
            Add("dlg.openDirFailed", "Cannot open the folder:\n{0}", "无法打开目录：\n{0}", "無法開啟目錄：\n{0}", "フォルダを開けません：\n{0}", "폴더를 열 수 없습니다:\n{0}", "No se puede abrir la carpeta:\n{0}", "Ordner kann nicht geöffnet werden:\n{0}", "Не удалось открыть папку:\n{0}");

            // ---- 命令行 ----
            Add("cli.usage", "Usage: scan | repair <appid> [--dry-run] | generate <installDir> [appid] [--dry-run] | gui-selftest", "用法: scan | repair <appid> [--dry-run] | generate <installDir> [appid] [--dry-run] | gui-selftest", "用法: scan | repair <appid> [--dry-run] | generate <installDir> [appid] [--dry-run] | gui-selftest", "使い方: scan | repair <appid> [--dry-run] | generate <installDir> [appid] [--dry-run] | gui-selftest", "사용법: scan | repair <appid> [--dry-run] | generate <installDir> [appid] [--dry-run] | gui-selftest", "Uso: scan | repair <appid> [--dry-run] | generate <installDir> [appid] [--dry-run] | gui-selftest", "Verwendung: scan | repair <appid> [--dry-run] | generate <installDir> [appid] [--dry-run] | gui-selftest", "Использование: scan | repair <appid> [--dry-run] | generate <installDir> [appid] [--dry-run] | gui-selftest");
            Add("cli.hint", "Tip: set STEAM_ACF_ROOT to use another Steam root, STEAM_ACF_LANG to force a UI language.", "提示: 设置环境变量 STEAM_ACF_ROOT 可指定 Steam 根目录，STEAM_ACF_LANG 可强制界面语言。", "提示: 設定環境變數 STEAM_ACF_ROOT 可指定 Steam 根目錄，STEAM_ACF_LANG 可強制介面語言。", "ヒント: 環境変数 STEAM_ACF_ROOT で Steam ルート、STEAM_ACF_LANG で表示言語を指定できます。", "팁: 환경 변수 STEAM_ACF_ROOT로 Steam 루트를, STEAM_ACF_LANG으로 UI 언어를 지정할 수 있습니다.", "Consejo: define STEAM_ACF_ROOT para otro directorio de Steam y STEAM_ACF_LANG para forzar el idioma.", "Tipp: STEAM_ACF_ROOT für ein anderes Steam-Verzeichnis, STEAM_ACF_LANG für die Sprache setzen.", "Совет: переменная STEAM_ACF_ROOT задаёт каталог Steam, STEAM_ACF_LANG — язык интерфейса.");
            Add("cli.steamFolder", "Steam folder: {0}", "Steam 目录: {0}", "Steam 目錄: {0}", "Steam フォルダ: {0}", "Steam 폴더: {0}", "Carpeta de Steam: {0}", "Steam-Ordner: {0}", "Каталог Steam: {0}");
            Add("cli.notFound", "Steam installation not found. Please make sure Steam is installed.", "未检测到 Steam 安装目录。请确认 Steam 已安装。", "未偵測到 Steam 安裝目錄。請確認 Steam 已安裝。", "Steam のインストール先が見つかりません。Steam がインストールされているか確認してください。", "Steam 설치 폴더를 찾을 수 없습니다. Steam이 설치되어 있는지 확인하세요.", "No se encontró la instalación de Steam. Comprueba que Steam esté instalado.", "Steam-Installation nicht gefunden. Bitte prüfen, ob Steam installiert ist.", "Установка Steam не найдена. Убедитесь, что Steam установлен.");
            Add("cli.ok", "OK", "OK", "OK", "OK", "OK", "OK", "OK", "OK");
            Add("cli.fail", "FAIL: {0}", "失败: {0}", "失敗: {0}", "失敗: {0}", "실패: {0}", "FALLO: {0}", "FEHLER: {0}", "ОШИБКА: {0}");
            Add("cli.previewHeader", "----- ACF preview (dry run, nothing written) -----", "----- ACF 预览（dry-run，未写入）-----", "----- ACF 預覽（dry-run，未寫入）-----", "----- ACF プレビュー（dry-run、未書き込み）-----", "----- ACF 미리보기(dry-run, 기록 안 함) -----", "----- Vista previa del ACF (dry run, sin escribir) -----", "----- ACF-Vorschau (dry run, nichts geschrieben) -----", "----- Предпросмотр ACF (dry run, без записи) -----");
            Add("cli.previewFooter", "-----------------------------", "-----------------------------", "-----------------------------", "-----------------------------", "-----------------------------", "-----------------------------", "-----------------------------", "-----------------------------");
            Add("cli.orphanNotFound", "Orphan folder not found (or it was classified as leftovers/empty and cannot be generated).", "未找到该孤儿文件夹（或它已被判定为残留/空目录，无法生成）。", "找不到該孤兒資料夾（或它已被判定為殘留/空目錄，無法產生）。", "対象フォルダが見つかりません（残骸／空と判定され生成できない可能性があります）。", "해당 폴더를 찾을 수 없습니다(잔여/빈 폴더로 판정되어 생성할 수 없음).", "No se encontró la carpeta huérfana (o se clasificó como restos/vacía y no se puede generar).", "Verwaister Ordner nicht gefunden (oder als Rest/leer eingestuft und nicht erzeugbar).", "Папка не найдена (или определена как остатки/пустая — создание невозможно).");
            Add("cli.scanTable", "Status\tAppID\tName\tLibrary\tFolder size\tNotes", "状态\tAppID\t名称\t库\t实测体积\t说明", "狀態\tAppID\t名稱\t庫\t實測容量\t說明", "状態\tAppID\t名前\tライブラリ\t実測サイズ\t説明", "상태\tAppID\t이름\t라이브러리\t측정 크기\t설명", "Estado\tAppID\tNombre\tBiblioteca\tTamaño\tNotas", "Status\tAppID\tName\tBibliothek\tGröße\tHinweise", "Статус\tAppID\tНазвание\tБиблиотека\tРазмер\tПримечание");
            Add("cli.totalEntries", "Scanned {0} entries:", "共扫描到 {0} 个条目:", "共掃描到 {0} 個項目:", "{0} 件をスキャンしました:", "항목 {0}개를 검사했습니다:", "Se analizaron {0} entradas:", "{0} Einträge gefunden:", "Просканировано записей: {0}:");
            Add("menu.title", "Actions:", "操作:", "操作:", "操作:", "작업:", "Acciones:", "Aktionen:", "Действия:");
            Add("menu.repairOne", "1  Repair a damaged ACF (enter appid)", "1  修复一个损坏的 ACF（输入 appid）", "1  修復一個損毀的 ACF（輸入 appid）", "1  破損した ACF を修復（appid を入力）", "1  손상된 ACF 복구(appid 입력)", "1  Reparar un ACF dañado (introducir appid)", "1  Beschädigtes ACF reparieren (AppID eingeben)", "1  Исправить повреждённый ACF (введите appid)");
            Add("menu.exportOne", "2  Export the ACF of a game", "2  导出某个游戏的 ACF 文件", "2  匯出某個遊戲的 ACF 檔案", "2  ゲームの ACF を書き出す", "2  게임의 ACF 내보내기", "2  Exportar el ACF de un juego", "2  ACF eines Spiels exportieren", "2  Экспортировать ACF игры");
            Add("menu.exportAll", "3  Export all ACFs that are OK", "3  导出所有「已安装正常」的 ACF 文件", "3  匯出所有「已安裝正常」的 ACF 檔案", "3  正常な ACF をすべて書き出す", "3  정상 ACF 모두 내보내기", "3  Exportar todos los ACF correctos", "3  Alle OK-ACFs exportieren", "3  Экспортировать все исправные ACF");
            Add("menu.repairAll", "4  Fix all issues (repair + generate)", "4  修复所有问题项（修复 + 生成）", "4  修復所有問題項（修復 + 產生）", "4  すべての問題を修正（修復＋生成）", "4  모든 문제 항목 복구(복구+생성)", "4  Corregir todo (reparar + generar)", "4  Alle Probleme beheben (reparieren + erzeugen)", "4  Исправить всё (ремонт + создание)");
            Add("menu.rescan", "5  Rescan", "5  重新扫描", "5  重新掃描", "5  再スキャン", "5  다시 검사", "5  Reanalizar", "5  Neu scannen", "5  Пересканировать");
            Add("menu.exit", "0  Exit", "0  退出", "0  結束", "0  終了", "0  종료", "0  Salir", "0  Beenden", "0  Выход");
            Add("menu.prompt", "Enter appid: ", "请输入 appid: ", "請輸入 appid: ", "appid を入力: ", "appid 입력: ", "Introduce el appid: ", "AppID eingeben: ", "Введите appid: ");
            Add("menu.invalid", "Invalid choice.", "无效选择。", "無效選擇。", "無効な選択です。", "잘못된 선택입니다.", "Opción no válida.", "Ungültige Auswahl.", "Неверный выбор.");
            Add("menu.pressEnter", "> ", "> ", "> ", "> ", "> ", "> ", "> ", "> ");
            Add("cli.processDone", "Finished: {0} succeeded, {1} failed, {2} skipped (leftovers/empty/unverified).", "处理完成：成功 {0} 个，失败 {1} 个，跳过（残留/空目录/无法验证）{2} 个。", "處理完成：成功 {0} 個，失敗 {1} 個，跳過（殘留/空目錄/無法驗證）{2} 個。", "完了：成功 {0} 件、失敗 {1} 件、スキップ {2} 件（残骸／空／未検証）。", "완료: 성공 {0}개, 실패 {1}개, 건너뜀 {2}개(잔여/빈/미검증).", "Terminado: {0} correctos, {1} fallidos, {2} omitidos (restos/vacías/sin verificar).", "Fertig: {0} erfolgreich, {1} fehlgeschlagen, {2} übersprungen (Reste/leer/ungeprüft).", "Готово: успешно {0}, ошибок {1}, пропущено {2} (остатки/пустые/не проверено).");
            Add("cli.exportedCopy", "[export] copy: {0}", "[导出] 副本: {0}", "[匯出] 副本: {0}", "[書き出し] コピー: {0}", "[내보내기] 사본: {0}", "[exportar] copia: {0}", "[Export] Kopie: {0}", "[экспорт] копия: {0}");
            Add("cli.repaired", "[repair] written back: {0} (original backed up as {1})", "[修复] 已写回: {0}（原文件备份: {1}）", "[修復] 已寫回: {0}（原檔案備份: {1}）", "[修復] 書き戻し: {0}（元ファイル: {1}）", "[복구] 기록 완료: {0} (원본 백업: {1})", "[reparar] escrito: {0} (original guardado como {1})", "[Reparatur] geschrieben: {0} (Original: {1})", "[ремонт] записано: {0} (оригинал: {1})");
            Add("cli.generated", "[generate] written: {0}", "[生成] 已写入: {0}", "[產生] 已寫入: {0}", "[生成] 書き込み: {0}", "[생성] 기록: {0}", "[generar] escrito: {0}", "[Erzeugen] geschrieben: {0}", "[создание] записано: {0}");
            Add("cli.restartSteam", "Done. Restart Steam to see the result.", "完成。请重启 Steam 查看结果。", "完成。請重新啟動 Steam 查看結果。", "完了。Steam を再起動して確認してください。", "완료. Steam을 다시 시작해 확인하세요.", "Hecho. Reinicia Steam para ver el resultado.", "Fertig. Steam neu starten, um das Ergebnis zu sehen.", "Готово. Перезапустите Steam, чтобы увидеть результат.");
            Add("err.noDepotInfoCli", "Cannot obtain the depot list for appid {0}: it is not in Steam's local cache appinfo.vdf and there is no install record in content_log.txt.\n(The command-line build has no network access; use the GUI build, which also tries steamcmd, or start Steam once to refresh appcache and retry.)",
                "无法获取 AppID {0} 的 depot 清单：Steam 本地缓存 appinfo.vdf 里没有它，content_log.txt 里也没有安装记录。\n（命令行版不联网；可改用图形界面版，它会额外尝试 steamcmd，或先启动一次 Steam 让它更新 appcache 后重试。）",
                "無法取得 AppID {0} 的 depot 清單：Steam 本機快取 appinfo.vdf 裡沒有它，content_log.txt 裡也沒有安裝記錄。\n（命令列版不連網；可改用圖形介面版，它會額外嘗試 steamcmd，或先啟動一次 Steam 讓它更新 appcache 後重試。）",
                "appid {0} の depot リストを取得できません：Steam ローカルキャッシュ appinfo.vdf になく、content_log.txt にも記録がありません。\n（CLI 版はネットワーク非対応。GUI 版は steamcmd も試します。または Steam を一度起動して appcache を更新してください。）",
                "appid {0}의 depot 목록을 가져올 수 없습니다: Steam 로컬 캐시 appinfo.vdf에 없고 content_log.txt에도 기록이 없습니다.\n(CLI 버전은 네트워크를 사용하지 않습니다. GUI 버전은 steamcmd도 시도합니다. 또는 Steam을 한 번 실행해 appcache를 갱신하세요.)",
                "No se puede obtener la lista de depots del appid {0}: no está en la caché local appinfo.vdf ni hay registro en content_log.txt.\n(La versión de consola no usa red; la versión con interfaz también prueba steamcmd, o inicia Steam una vez para refrescar appcache.)",
                "Depot-Liste für AppID {0} nicht verfügbar: nicht im lokalen Cache appinfo.vdf und kein Eintrag in content_log.txt.\n(Die CLI-Version hat keinen Netzzugriff; die GUI-Version versucht zusätzlich steamcmd, oder Steam einmal starten.)",
                "Не удалось получить список depot для appid {0}: нет в локальном кэше appinfo.vdf и нет записи в content_log.txt.\n(Версия CLI без сети; в версии с интерфейсом дополнительно используется steamcmd, либо запустите Steam для обновления appcache.)");
            Add("cli.repairArgs", "Usage: repair <appid> [--dry-run]", "用法: repair <appid> [--dry-run]", "用法: repair <appid> [--dry-run]", "使い方: repair <appid> [--dry-run]", "사용법: repair <appid> [--dry-run]", "Uso: repair <appid> [--dry-run]", "Verwendung: repair <appid> [--dry-run]", "Использование: repair <appid> [--dry-run]");
            Add("cli.generateArgs", "Usage: generate <installDir> [appid] [--dry-run]", "用法: generate <installDir> [appid] [--dry-run]", "用法: generate <installDir> [appid] [--dry-run]", "使い方: generate <installDir> [appid] [--dry-run]", "사용법: generate <installDir> [appid] [--dry-run]", "Uso: generate <installDir> [appid] [--dry-run]", "Verwendung: generate <installDir> [appid] [--dry-run]", "Использование: generate <installDir> [appid] [--dry-run]");
            Add("cli.exportArgs", "Usage: export <appid> [output dir]", "用法: export <appid> [输出目录]", "用法: export <appid> [輸出目錄]", "使い方: export <appid> [出力先]", "사용법: export <appid> [출력 폴더]", "Uso: export <appid> [carpeta de salida]", "Verwendung: export <appid> [Zielordner]", "Использование: export <appid> [каталог]");
            Add("cli.exportAllArgs", "Usage: export-all [output dir]", "用法: export-all [输出目录]", "用法: export-all [輸出目錄]", "使い方: export-all [出力先]", "사용법: export-all [출력 폴더]", "Uso: export-all [carpeta de salida]", "Verwendung: export-all [Zielordner]", "Использование: export-all [каталог]");
            Add("cli.unknownCmd", "Unknown command. Available commands:", "未知命令。可用命令:", "未知命令。可用命令:", "不明なコマンドです。使用可能なコマンド:", "알 수 없는 명령입니다. 사용 가능한 명령:", "Comando desconocido. Comandos disponibles:", "Unbekannter Befehl. Verfügbare Befehle:", "Неизвестная команда. Доступные команды:");
            Add("cli.exportNotFound", "No ACF found for appid {0}.", "未找到 appid {0} 的 ACF。", "找不到 appid {0} 的 ACF。", "appid {0} の ACF が見つかりません。", "appid {0}의 ACF를 찾을 수 없습니다.", "No se encontró ACF para el appid {0}.", "Kein ACF für AppID {0} gefunden.", "ACF для appid {0} не найден.");
            Add("cli.exportTo", "Exported: {0}", "已导出: {0}", "已匯出: {0}", "書き出し: {0}", "내보냄: {0}", "Exportado: {0}", "Exportiert: {0}", "Экспортировано: {0}");
            Add("cli.exportItem", "[export] {0}  {1}", "[导出] {0}  {1}", "[匯出] {0}  {1}", "[書き出し] {0}  {1}", "[내보내기] {0}  {1}", "[exportar] {0}  {1}", "[Export] {0}  {1}", "[экспорт] {0}  {1}");
            Add("cli.exportAllDone", "[export] {0} ACF files exported to: {1}", "[导出] 共导出 {0} 个 ACF 到: {1}", "[匯出] 共匯出 {0} 個 ACF 到: {1}", "[書き出し] {0} 個の ACF を書き出しました: {1}", "[내보내기] ACF {0}개를 내보냈습니다: {1}", "[exportar] {0} ACF exportados a: {1}", "[Export] {0} ACFs exportiert nach: {1}", "[экспорт] экспортировано ACF: {0} в {1}");

            // ---- 自检 ----
            Add("selftest.form", "MainForm built successfully, grid rows={0} (scan returned {1} entries)", "MainForm 构建成功，表格行数={0}（扫描结果 {1} 条）", "MainForm 建構成功，表格列數={0}（掃描結果 {1} 筆）", "MainForm の構築に成功、行数={0}（スキャン結果 {1} 件）", "MainForm 생성 성공, 행 수={0}(검사 결과 {1}개)", "MainForm creado correctamente, filas={0} (entradas {1})", "MainForm erfolgreich erstellt, Zeilen={0} (Einträge {1})", "MainForm создан, строк={0} (записей {1})");
            Add("selftest.async", "Async rescan finished, grid rows={0}", "异步重新扫描完成，表格行数={0}", "非同步重新掃描完成，表格列數={0}", "非同期再スキャン完了、行数={0}", "비동기 재검사 완료, 행 수={0}", "Reanálisis asíncrono terminado, filas={0}", "Asynchroner Scan beendet, Zeilen={0}", "Асинхронное сканирование завершено, строк={0}");
            Add("selftest.lang", "language={0}, keys={1}, missing translations={2}", "当前语言={0}，词条数={1}，缺译文={2}", "目前語言={0}，詞條數={1}，缺譯文={2}", "言語={0}、キー数={1}、未訳={2}", "언어={0}, 키 {1}개, 미번역 {2}", "idioma={0}, claves={1}, sin traducir={2}", "Sprache={0}, Keys={1}, fehlende Übersetzungen={2}", "язык={0}, ключей={1}, без перевода={2}");
        }
    }
}
