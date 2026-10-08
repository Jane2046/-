// 文字清洗工具 —— 原生 WinForms 单文件程序
// 交互：复制要清洗的文字 -> 双击程序 -> 自动删除指定段落 -> 结果写回剪贴板 -> 自动关闭
//
// 排版原则：除了被删除的段落之外，原文其余内容（换行、空行、缩进等）一律保持原样。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace CleanTextTool
{
    internal static class Program
    {
        [STAThread]
        internal static void Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--selftest") { SelfTest.Run(); return; }
            if (args.Length > 3 && args[0] == "--clean")     // 无界面：清洗文件，便于自动化测试
            {
                string input = File.ReadAllText(args[1], Encoding.UTF8);
                CleanResult cr = Cleaner.Clean(input, args[2], args[3]);
                File.WriteAllText(args[1] + ".out", cr.Text, new UTF8Encoding(false));
                File.WriteAllText(args[1] + ".meta", "blocks=" + cr.Blocks + " startFound=" + cr.StartFound
                    + " endFound=" + cr.EndFound + " endMissing=" + cr.EndMissing, new UTF8Encoding(false));
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }

    // ---------------- 设置 ----------------
    internal sealed class AppSettings
    {
        public string Start = "You are a helpful AI assistant";
        public string End = "Do you understand?";
        public int Preset = 0;   // 0 = 自定义（即上面两项），1 / 2 = 内置组合

        public static readonly string[][] Presets = new string[][]
        {
            new string[] { "You are a helpful AI assistant", "Do you understand?" },
            new string[] { "To uphold Coursera", "related topics." }
        };

        private static string PrimaryPath()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "文字清洗工具");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "settings.json");
        }

        private static string FallbackPath()
        {
            return Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "settings.json");
        }

        public static AppSettings Load()
        {
            string[] candidates;
            try { candidates = new string[] { PrimaryPath(), FallbackPath() }; }
            catch { candidates = new string[] { FallbackPath() }; }

            foreach (string p in candidates)
            {
                try
                {
                    if (!File.Exists(p)) continue;
                    byte[] raw = File.ReadAllBytes(p);
                    string text;
                    try { text = Encoding.UTF8.GetString(ProtectedData.Unprotect(raw, null, DataProtectionScope.CurrentUser)); }
                    catch { text = Encoding.UTF8.GetString(raw); }

                    AppSettings s = new JavaScriptSerializer().Deserialize<AppSettings>(text);
                    if (s != null)
                    {
                        if (s.Start == null) s.Start = Presets[0][0];
                        if (s.End == null) s.End = Presets[0][1];
                        if (s.Preset < 0 || s.Preset > 2) s.Preset = 0;
                        return s;
                    }
                }
                catch { }
            }
            return new AppSettings();
        }

        /// <summary>保存设置；返回 null 表示成功，否则返回出错原因。</summary>
        public string Save()
        {
            string json = new JavaScriptSerializer().Serialize(this);
            byte[] data;
            try { data = ProtectedData.Protect(Encoding.UTF8.GetBytes(json), null, DataProtectionScope.CurrentUser); }
            catch { data = Encoding.UTF8.GetBytes(json); }

            string primary = null;
            try
            {
                primary = PrimaryPath();
                File.WriteAllBytes(primary, data);
                return null;
            }
            catch (Exception ex)
            {
                try
                {
                    File.WriteAllBytes(FallbackPath(), data);
                    return null;
                }
                catch (Exception ex2)
                {
                    return "无法写入设置文件。" + Environment.NewLine
                        + "位置1：" + (primary ?? "（无法创建目录）") + Environment.NewLine
                        + "原因：" + ex.Message + Environment.NewLine
                        + "位置2：" + FallbackPath() + Environment.NewLine
                        + "原因：" + ex2.Message;
                }
            }
        }

        public string EffectiveStart { get { return Preset == 0 ? Start : Presets[Preset - 1][0]; } }
        public string EffectiveEnd { get { return Preset == 0 ? End : Presets[Preset - 1][1]; } }

        public string Summary
        {
            get
            {
                string a = EffectiveStart, b = EffectiveEnd;
                if (string.IsNullOrEmpty(a) && string.IsNullOrEmpty(b)) return "未设置删除标记";
                return "从 “" + a + "” 到 “" + b + "”";
            }
        }
    }

    // ---------------- 清洗算法 ----------------
    internal sealed class CleanResult
    {
        public string Text = "";
        public int Blocks;
        public bool StartFound;
        public bool EndFound;
        public bool EndMissing;     // 找到起始标记但没找到结束标记：为安全起见不改动原文
        public string StartMarker = "";
        public string EndMarker = "";
    }

    internal static class Cleaner
    {
        /// <summary>删除「起始文字」到「结束文字」之间的内容；其余原文一字不动。</summary>
        public static CleanResult Clean(string text, string start, string end)
        {
            CleanResult r = new CleanResult();
            r.StartMarker = NormalizeMarker(start);
            r.EndMarker = NormalizeMarker(end);
            if (text == null) text = "";

            string s = text;
            int startLen = 0, endLen = 0;

            if (r.StartMarker.Length == 0 && r.EndMarker.Length == 0)
            {
                r.Text = text;          // 没设置标记：原样返回
                return r;
            }

            for (int i = 0; i < 500; i++)
            {
                int a, b;

                if (r.StartMarker.Length == 0)
                {
                    if (!FindEnd(s, r.EndMarker, 0, out a, ref endLen)) break;
                    b = a + endLen;
                    r.EndFound = true;
                }
                else
                {
                    if (!FindStart(s, r.StartMarker, 0, out a, ref startLen)) break;
                    r.StartFound = true;
                    b = a + startLen;

                    if (r.EndMarker.Length > 0)
                    {
                        int e2, e2len = 0;
                        if (FindEnd(s, r.EndMarker, b, out e2, ref e2len))
                        {
                            r.EndFound = true;
                            b = e2 + e2len;
                        }
                        else
                        {
                            // 找不到结束标记：不动原文，仅提示（避免误删）
                            r.EndMissing = true;
                            break;
                        }
                    }
                    else
                    {
                        b = s.Length;
                    }
                }

                if (a < 0 || b <= a || b > s.Length) break;
                s = TrimBoundary(s, a, b);
                r.Blocks++;
                if (r.Blocks >= 1 && r.StartMarker.Length == 0 && r.EndMarker.Length == 0) break;
            }

            r.Text = s;   // 只做删除，不改动任何其他内容
            return r;
        }

        /// <summary>
        /// 删除 [a,b)：把紧贴删除区的孤立括号/引号、行尾空格与所在行的换行一并清掉，
        /// 并在删除位置留一个空行（前后都有内容时）。
        /// 其余换行、缩进、行尾空格等一律保持原样。
        /// </summary>
        private static string TrimBoundary(string s, int a, int b)
        {
            // 1) 吃掉紧贴删除区的孤立标点 / 引号（标记被括号或引号包住时的残留）
            int guardA = a, guardB = b;
            while (a > guardA - 4 && a > 0 && IsOrphanPunct(s[a - 1])) a--;
            while (b < guardB + 4 && b < s.Length && IsOrphanPunct(s[b])) b++;

            // 2) 左侧：跳过删除区前的行尾空格，再吃掉一个换行（若存在）
            int la = a;
            while (la > 0 && (s[la - 1] == ' ' || s[la - 1] == '\t')) la--;
            bool leftNl = false;
            if (la > 0 && (s[la - 1] == '\n' || s[la - 1] == '\r'))
            {
                leftNl = true;
                while (la > 0 && (s[la - 1] == '\n' || s[la - 1] == '\r')) la--;
            }

            // 3) 右侧：吃掉一个换行（若存在）
            int rb = b;
            bool rightNl = false;
            if (rb < s.Length && (s[rb] == '\n' || s[rb] == '\r'))
            {
                rightNl = true;
                while (rb < s.Length && (s[rb] == '\n' || s[rb] == '\r')) rb++;
            }

            bool leftText = la > 0;
            bool rightText = rb < s.Length;
            if (!leftText || !rightText)
            {
                if (leftText) return s.Substring(0, la);   // 只剩左侧：结尾不留空行
                if (rightText) return s.Substring(rb);     // 只剩右侧：开头不留空行
                return "";
            }

            // 4) 判断被删掉的内容是不是“整块”（含换行）。
            //    整块删除 → 删除位置留一个空行；行内删除 → 用空格衔接，不额外造行。
            bool blockDeleted = false;
            for (int k = la; k < rb; k++)
                if (s[k] == '\n' || s[k] == '\r') { blockDeleted = true; break; }

            return s.Substring(0, la) + (blockDeleted ? "\r\n\r\n" : " ") + s.Substring(rb);
        }

        /// <summary>删除区旁常见的孤立标点 / 引号（如标记被括号或引号包住时残留的那个）。</summary>
        private static bool IsOrphanPunct(char c)
        {
            return c == ')' || c == ']' || c == '}' || c == '>'
                || c == '\u3009' || c == '\u300b' || c == '\u300d' || c == '\u300f'
                || c == '\uff09' || c == '\u3011' || c == '\uff3d' || c == '\u3015'
                || c == '\u201c' || c == '\u201d' || c == '\u2018' || c == '\u2019'
                || c == '"' || c == '\'' || c == '\u300c' || c == '\u300e';
        }

        /// <summary>定位起始标记；若标记文字两侧带引号，则连同引号一起去掉。</summary>
        public static bool FindStart(string text, string marker, int from, out int index, ref int len)
        {
            index = -1; len = marker.Length;
            if (marker.Length == 0 || text == null || from >= text.Length) return false;

            int a;
            if (FindFlexible(text, marker, from, out a))
            {
                index = a; len = marker.Length;
                return true;
            }

            int qs, qe;
            if (FindQuoted(text, marker, from, out qs, out qe, false))
            {
                index = qs; len = qe - qs;
                return true;
            }
            return false;
        }

        /// <summary>定位结束标记；若标记文字两侧带引号，则连同引号与句末标点一起去掉。</summary>
        public static bool FindEnd(string text, string marker, int from, out int index, ref int len)
        {
            index = -1; len = marker.Length;
            if (marker.Length == 0 || text == null || from >= text.Length) return false;

            int a;
            bool hasPlain = FindFlexible(text, marker, from, out a);
            int plainLen = marker.Length;
            if (hasPlain)
            {
                int b = a + marker.Length;
                while (b < text.Length && IsTrailingPunct(text[b])) b++;   // 末尾句末标点一起吃掉
                plainLen = b - a;
            }

            int qs, qe;
            if (FindQuoted(text, marker, from, out qs, out qe, true))
            {
                if (!hasPlain || qs < a) { index = qs; len = qe - qs; return true; }
            }

            if (!hasPlain) return false;
            index = a; len = plainLen;
            return true;
        }

        private static bool IsTrailingPunct(char c)
        {
            return c == '.' || c == '?' || c == '!' || c == '。' || c == '？' || c == '！';
        }

        /// <summary>匹配被引号（或书名号 / 英文引号）包裹的标记。</summary>
        private static bool FindQuoted(string text, string marker, int from, out int start, out int end, bool allowTrailingPunct)
        {
            start = -1; end = -1;
            string qs = "[\"'\u201c\u201d\u2018\u2019\u300c\u300d\u300e\u300f]";
            string pattern = qs + "\\s*" + BuildPattern(marker) + "\\s*" + qs;
            if (allowTrailingPunct) pattern += @"[\.""'!?\u3002\uff1f\uff01]*";

            Match m = Regex.Match(text.Substring(from), pattern,
                RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);
            if (!m.Success) return false;
            if (m.Length < marker.Length + 2) return false;
            start = from + m.Index;
            end = start + m.Length;
            return true;
        }

        /// <summary>判断是否空白（含全角空格）。</summary>
        private static bool IsSpace(char c)
        {
            return c == ' ' || c == '\t' || c == '\r' || c == '\n' || c == '\u3000' || c == '\u00a0';
        }

        /// <summary>
        /// 查找标记，容忍空白差异：标记里的一个空格可匹配原文里的任意一段空白（含换行）。
        /// </summary>
        public static bool FindFlexible(string text, string marker, int from, out int index)
        {
            index = -1;
            if (marker.Length == 0 || text == null || from >= text.Length) return false;

            string lit = marker.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (lit.Length == 0) return false;

            int start = from;
            while (start < text.Length)
            {
                int i = text.IndexOf(lit[0].ToString(), start, StringComparison.OrdinalIgnoreCase);
                if (i < 0) return false;
                start = i + 1;

                int p = i;
                int k = 0;
                bool ok = true;
                while (k < lit.Length)
                {
                    if (lit[k] == ' ')
                    {
                        while (k < lit.Length && lit[k] == ' ') k++;
                        if (k >= lit.Length) break;              // 标记以空白结尾：忽略末尾空白
                        if (p >= text.Length || !IsSpace(text[p])) { ok = false; break; }
                        while (p < text.Length && IsSpace(text[p])) p++;
                    }
                    else
                    {
                        if (p >= text.Length) { ok = false; break; }
                        if (char.ToUpperInvariant(text[p]) != char.ToUpperInvariant(lit[k])) { ok = false; break; }
                        p++; k++;
                    }
                }
                if (ok) { index = i; return true; }
            }
            return false;
        }

        /// <summary>
        /// 标记预处理：去掉首尾的空白、引号与括号（保留句末的问号/叹号，因为标记里常带它们），
        /// 把内部换行与连续空格压成单个空格。
        /// </summary>
        public static string NormalizeMarker(string marker)
        {
            if (marker == null) return "";
            char[] edge = new char[]
            {
                ' ', '\t', '\u3000', '\r', '\n', '\u00a0',
                '\u201c', '\u201d', '"', '\'', '\u2018', '\u2019', '\u300c', '\u300d', '\u300e', '\u300f',
                '\u3008', '\u3009', '\u300a', '\u300b', '\uff08', '\uff09', '(', ')', '[', ']', '{', '}',
                '\u3002', '.', ',', ':', ';', '\uff0c', '\uff1a', '\uff1b',
                '\u2014', '-'
            };
            string m = marker.Replace('\r', ' ').Replace('\n', ' ').Trim(edge);
            m = Regex.Replace(m, @"\s+", " ");
            return m.Trim(edge);
        }

        /// <summary>把标记转成正则：空白处允许任意空白（含换行）。</summary>
        private static string BuildPattern(string marker)
        {
            string[] tokens = Regex.Split(marker.Trim(), @"\s+");
            var sb = new StringBuilder();
            for (int i = 0; i < tokens.Length; i++)
            {
                if (i > 0) sb.Append(@"\s+");
                sb.Append(Regex.Escape(tokens[i]));
            }
            return sb.ToString();
        }
    }

    // ---------------- 剪贴板 ----------------
    internal static class ClipboardSafe
    {
        public static string GetText()
        {
            for (int i = 0; i < 12; i++)
            {
                try
                {
                    if (Clipboard.ContainsText()) return Clipboard.GetText();
                    return null;
                }
                catch { Thread.Sleep(40); }
            }
            return null;
        }

        public static bool SetText(string text)
        {
            for (int i = 0; i < 12; i++)
            {
                try
                {
                    if (text == null) text = "";
                    Clipboard.SetDataObject(text, true, 8, 60);
                    return true;
                }
                catch { Thread.Sleep(60); }
            }
            return false;
        }
    }

    // ---------------- 界面工具 ----------------
    internal static class UI
    {
        public static readonly Color Bg = Color.FromArgb(244, 247, 251);
        public static readonly Color Card = Color.White;
        public static readonly Color Border = Color.FromArgb(219, 226, 236);
        public static readonly Color Primary = Color.FromArgb(22, 119, 255);
        public static readonly Color PrimaryHover = Color.FromArgb(58, 142, 255);
        public static readonly Color PrimaryDown = Color.FromArgb(13, 96, 214);
        public static readonly Color GhostHover = Color.FromArgb(238, 243, 250);
        public static readonly Color TextMain = Color.FromArgb(28, 37, 52);
        public static readonly Color TextSub = Color.FromArgb(122, 136, 156);
        public static readonly Color Ok = Color.FromArgb(22, 163, 74);
        public static readonly Color Warn = Color.FromArgb(217, 119, 6);

        public static Font F(float size, FontStyle style)
        {
            try { return new Font("Microsoft YaHei UI", size, style); }
            catch { return new Font(FontFamily.GenericSansSerif, size, style); }
        }

        public static GraphicsPath Round(Rectangle r, int radius)
        {
            GraphicsPath p = new GraphicsPath();
            int d = Math.Max(2, radius * 2);
            if (d > r.Height) d = r.Height;
            if (d > r.Width) d = r.Width;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void SetRounded(Control c, int radius)
        {
            using (GraphicsPath p = Round(new Rectangle(0, 0, c.Width, c.Height), radius))
                c.Region = new Region(p);
        }

        public static Button Flat(string text, int w, int h, bool primary)
        {
            Button b = new Button();
            b.Text = text;
            b.Size = new Size(w, h);
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 0;
            b.Font = F(10f, FontStyle.Regular);
            b.Cursor = Cursors.Hand;
            b.UseVisualStyleBackColor = false;
            b.TabStop = false;
            b.Paint += (s, e) => PaintButton((Button)s, e, primary);
            b.Resize += (s, e) => SetRounded((Control)s, 8);
            b.MouseEnter += (s, e) => { b.Tag = "hover"; b.Invalidate(); };
            b.MouseLeave += (s, e) => { b.Tag = null; b.Invalidate(); };
            b.MouseDown += (s, e) => { b.Tag = "down"; b.Invalidate(); };
            b.MouseUp += (s, e) => { b.Tag = "hover"; b.Invalidate(); };
            return b;
        }

        private static void PaintButton(Button b, PaintEventArgs e, bool primary)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.Clear(b.Parent != null ? b.Parent.BackColor : Bg);
            Rectangle r = new Rectangle(0, 0, b.Width - 1, b.Height - 1);
            string state = b.Tag as string;
            bool hover = state == "hover" || state == "down";
            bool down = state == "down";

            using (GraphicsPath p = Round(r, 8))
            {
                if (primary)
                {
                    Color fill = down ? PrimaryDown : (hover ? PrimaryHover : Primary);
                    using (SolidBrush br = new SolidBrush(fill)) e.Graphics.FillPath(br, p);
                }
                else
                {
                    using (SolidBrush br = new SolidBrush(hover ? GhostHover : Card)) e.Graphics.FillPath(br, p);
                    using (Pen pen = new Pen(Border)) e.Graphics.DrawPath(pen, p);
                }
            }
            Color fg = primary ? Color.White : TextMain;
            TextRenderer.DrawText(e.Graphics, b.Text, b.Font, r, fg,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }

        public static Label Text(string s, float size, FontStyle style, Color color)
        {
            Label l = new Label();
            l.Text = s;
            l.Font = F(size, style);
            l.ForeColor = color;
            l.AutoSize = false;
            l.BackColor = Color.Transparent;
            return l;
        }

        public static TextBox Input(string value)
        {
            TextBox t = new TextBox();
            t.Text = value ?? "";
            t.Font = F(10.5f, FontStyle.Regular);
            t.BorderStyle = BorderStyle.FixedSingle;
            t.BackColor = Card;
            t.ForeColor = TextMain;
            return t;
        }

        public static CheckBox Radio(string text)
        {
            CheckBox c = new CheckBox();
            c.Text = text;
            c.AutoSize = true;
            c.FlatStyle = FlatStyle.System;
            c.Font = F(10f, FontStyle.Regular);
            c.ForeColor = TextMain;
            c.BackColor = Color.Transparent;
            c.Cursor = Cursors.Hand;
            return c;
        }
    }

    // ---------------- 主窗口 ----------------
    internal sealed class MainForm : Form
    {
        private readonly AppSettings _settings = AppSettings.Load();

        private Label _stat;
        private Label _rule;
        private TextBox _preview;
        private Button _btnClean, _btnSettings, _btnClose;
        private System.Windows.Forms.Timer _autoClose;
        private int _autoLeft;
        private string _src = "";
        private bool _loading = true;
        private bool _internalPreview;
        private int _lastSrcChars;

        public MainForm()
        {
            Text = "文字清洗工具";
            ClientSize = new Size(620, 486);
            MinimumSize = new Size(560, 420);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = UI.Bg;
            Font = UI.F(9.5f, FontStyle.Regular);
            KeyPreview = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            BuildUi();

            Shown += (s, e) => Boot();
            KeyDown += OnKeyDown;
        }

        private void BuildUi()
        {
            Label title = UI.Text("文字清洗工具", 15f, FontStyle.Bold, UI.TextMain);
            title.SetBounds(24, 18, 260, 32);
            title.TextAlign = ContentAlignment.MiddleLeft;

            _btnSettings = UI.Flat("设置", 82, 32, false);
            _btnSettings.SetBounds(ClientSize.Width - 24 - 82, 20, 82, 32);
            _btnSettings.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _btnSettings.Font = UI.F(9.5f, FontStyle.Regular);
            _btnSettings.Click += (s, e) => OpenSettings();
            ToolTip tip = new ToolTip();
            tip.SetToolTip(_btnSettings, "自定义要删除的起始 / 结束文字（Ctrl+S）");

            _stat = UI.Text("", 10.5f, FontStyle.Bold, UI.TextSub);
            _stat.SetBounds(24, 54, ClientSize.Width - 48, 24);
            _stat.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            _rule = UI.Text("", 9f, FontStyle.Regular, UI.TextSub);
            _rule.SetBounds(24, 80, ClientSize.Width - 48, 20);
            _rule.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            Panel card = new Panel();
            card.BackColor = UI.Card;
            card.SetBounds(24, 106, ClientSize.Width - 48, ClientSize.Height - 106 - 76);
            card.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            card.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (GraphicsPath p = UI.Round(new Rectangle(0, 0, card.Width - 1, card.Height - 1), 8))
                {
                    using (SolidBrush br = new SolidBrush(UI.Card)) e.Graphics.FillPath(br, p);
                    using (Pen pen = new Pen(UI.Border)) e.Graphics.DrawPath(pen, p);
                }
            };

            _preview = new TextBox();
            _preview.Multiline = true;
            _preview.ReadOnly = true;
            _preview.ScrollBars = ScrollBars.Vertical;
            _preview.WordWrap = true;
            _preview.BorderStyle = BorderStyle.None;
            _preview.BackColor = UI.Card;
            _preview.ForeColor = UI.TextMain;
            _preview.Font = UI.F(10f, FontStyle.Regular);
            _preview.SetBounds(12, 10, card.Width - 30, card.Height - 20);
            _preview.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            _preview.TextChanged += PreviewChanged;
            card.Controls.Add(_preview);

            _btnClean = UI.Flat("重新清洗", 116, 40, true);
            _btnClose = UI.Flat("关闭", 88, 40, false);
            _btnClean.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            _btnClose.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            _btnClean.SetBounds(24, ClientSize.Height - 58, 116, 40);
            _btnClose.SetBounds(ClientSize.Width - 24 - 88, ClientSize.Height - 58, 88, 40);
            _btnClean.Click += (s, e) => RunClean(true);
            _btnClose.Click += (s, e) => Close();

            Controls.AddRange(new Control[] { title, _btnSettings, _stat, _rule, card, _btnClean, _btnClose });

            _autoClose = new System.Windows.Forms.Timer();
            _autoClose.Interval = 700;
            _autoClose.Tick += AutoCloseTick;
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) { Close(); return; }
            if (e.KeyCode == Keys.F5 || (e.Control && e.KeyCode == Keys.Enter)) { RunClean(true); e.SuppressKeyPress = true; return; }
            if (e.Control && (e.KeyCode == Keys.S || e.KeyCode == Keys.Oemcomma)) { OpenSettings(); e.SuppressKeyPress = true; return; }
            if (e.Control && e.KeyCode == Keys.V)
            {
                string t = ClipboardSafe.GetText();
                if (!string.IsNullOrEmpty(t))
                {
                    _src = t;
                    _lastSrcChars = t.Length;
                    _internalPreview = true;
                    _preview.Text = t;
                    _internalPreview = false;
                    BeginInvoke(new Action(() => RunClean(true)));
                    e.SuppressKeyPress = true;
                }
            }
        }

        private void Boot()
        {
            UpdateRule();
            string t = ClipboardSafe.GetText();
            if (t != null && t.Trim().Length > 0)
            {
                _src = t;
                _lastSrcChars = t.Length;
                RunClean(true);
            }
            else
            {
                SetStat("剪贴板里没有文字", UI.Warn);
                _preview.Text = "① 先复制要清洗的文字（Ctrl+C）\r\n② 再打开本程序，它会自动删除指定段落并把结果放回剪贴板\r\n\r\n也可以直接在这里 Ctrl+V 粘贴，然后按回车清洗。";
                _btnClean.Text = "读取剪贴板";
            }
            _loading = false;
        }

        private void UpdateRule()
        {
            string a = _settings.EffectiveStart, b = _settings.EffectiveEnd;
            if (a.Length == 0 && b.Length == 0)
                _rule.Text = "当前设置：未设置删除标记（点击右上角「设置」添加）";
            else
                _rule.Text = "当前设置：删除 “" + a + "” 到 “" + b + "” 之间的内容（其余内容保持原样）";
        }

        private void SetStat(string text, Color color)
        {
            _stat.Text = text;
            _stat.ForeColor = color;
        }

        private void PreviewChanged(object sender, EventArgs e)
        {
            if (_internalPreview || _loading) return;
            if (_preview.TextLength != _lastSrcChars)
            {
                _src = _preview.Text;
                _lastSrcChars = _src.Length;
            }
        }

        private bool RunClean(bool allowAutoClose)
        {
            if (_btnClean.Text == "读取剪贴板")
            {
                string t0 = ClipboardSafe.GetText();
                if (string.IsNullOrEmpty(t0) || t0.Trim().Length == 0) { SetStat("剪贴板里没有文字", UI.Warn); return false; }
                _src = t0;
                _lastSrcChars = t0.Length;
                _btnClean.Text = "重新清洗";
            }

            if (_src == null || _src.Trim().Length == 0) { SetStat("没有可清洗的文字", UI.Warn); return false; }

            if (_settings.EffectiveStart.Length == 0 && _settings.EffectiveEnd.Length == 0)
            {
                SetStat("还没有设置要删除的内容，请点右上角「设置」", UI.Warn);
                StopAutoClose();
                return false;
            }

            CleanResult res = Cleaner.Clean(_src, _settings.EffectiveStart, _settings.EffectiveEnd);

            _internalPreview = true;
            _preview.Text = res.Text;
            _internalPreview = false;

            bool copied = false;
            if (res.Text.Trim().Length > 0) copied = ClipboardSafe.SetText(res.Text);

            string info = string.Format("原始 {0} 字 → 清洗后 {1} 字（删除 {2} 处）",
                _src.Length, res.Text.Length, res.Blocks);

            if (res.Blocks > 0)
            {
                SetStat("✔ 已删除指定段落并复制到剪贴板　" + info, UI.Ok);
                if (allowAutoClose && copied) StartAutoClose();
            }
            else
            {
                string why;
                if (res.EndMissing)
                    why = "找到起始标记，但没找到结束标记 “" + res.EndMarker + "”，为避免误删已保留原文";
                else if (!res.StartFound)
                    why = "没找到起始标记 “" + res.StartMarker + "”";
                else
                    why = "没有可删除的内容";
                SetStat("⚠ " + why + "，请检查「设置」", UI.Warn);
                StopAutoClose();
            }

            _lastSrcChars = res.Text.Length;
            return res.Blocks > 0;
        }

        private void StartAutoClose()
        {
            _autoLeft = 1;
            _autoClose.Start();
        }

        private void StopAutoClose()
        {
            _autoClose.Stop();
        }

        private void AutoCloseTick(object sender, EventArgs e)
        {
            _autoLeft--;
            if (_autoLeft > 0)
            {
                SetStat("✔ 已复制到剪贴板，窗口即将自动关闭…", UI.Ok);
                return;
            }
            _autoClose.Stop();
            Close();
        }

        private void OpenSettings()
        {
            StopAutoClose();
            using (SettingsForm dlg = new SettingsForm(_settings))
            {
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    _settings.Preset = dlg.ResultPreset;
                    _settings.Start = dlg.ResultStart;
                    _settings.End = dlg.ResultEnd;
                    string err = _settings.Save();
                    UpdateRule();
                    if (err != null)
                        MessageBox.Show(this, "设置已生效，但保存失败（下次打开会恢复默认）：\r\n\r\n" + err,
                            "保存失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    if (_src != null && _src.Trim().Length > 0) RunClean(false);
                }
            }
        }
    }

    // ---------------- 设置窗口 ----------------
    internal sealed class SettingsForm : Form
    {
        private readonly AppSettings _working;
        private CheckBox[] _radios;
        private TextBox _start, _end;
        private Panel _customPanel;

        public int ResultPreset = 0;
        public string ResultStart = "";
        public string ResultEnd = "";

        public SettingsForm(AppSettings current)
        {
            _working = new AppSettings();
            _working.Start = current.Start;
            _working.End = current.End;
            _working.Preset = current.Preset;

            Text = "设置";
            ClientSize = new Size(520, 452);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = UI.Bg;
            Font = UI.F(9.5f, FontStyle.Regular);
            ShowInTaskbar = false;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            BuildUi();
        }

        private void BuildUi()
        {
            Label h = UI.Text("要删除哪一段内容？", 13f, FontStyle.Bold, UI.TextMain);
            h.SetBounds(24, 16, 400, 30);

            Label sub = UI.Text("程序只删除「起始文字」到「结束文字」之间的内容，其余文字与换行原样保留。", 9f, FontStyle.Regular, UI.TextSub);
            sub.SetBounds(24, 46, 470, 32);

            _radios = new CheckBox[3];
            string[] labels = new string[]
            {
                "自定义（在下面填写起始 / 结束文字）",
                "从 “" + AppSettings.Presets[0][0] + "” 到 “" + AppSettings.Presets[0][1] + "”",
                "从 “" + AppSettings.Presets[1][0] + "” 到 “" + AppSettings.Presets[1][1] + "”"
            };
            int y = 84;
            for (int i = 0; i < 3; i++)
            {
                _radios[i] = UI.Radio(labels[i]);
                _radios[i].SetBounds(24, y, 470, 24);
                _radios[i].Checked = (_working.Preset == i);
                int idx = i;
                _radios[i].CheckedChanged += (s, e) => { if (_radios[idx].Checked) SelectPreset(idx); };
                Controls.Add(_radios[i]);
                y += 30;
            }
            if (_working.Preset < 0 || _working.Preset > 2) _radios[0].Checked = true;

            _customPanel = new Panel();
            _customPanel.SetBounds(24, y + 6, 472, 118);
            _customPanel.BackColor = UI.Card;
            _customPanel.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (GraphicsPath p = UI.Round(new Rectangle(0, 0, _customPanel.Width - 1, _customPanel.Height - 1), 8))
                {
                    using (SolidBrush br = new SolidBrush(UI.Card)) e.Graphics.FillPath(br, p);
                    using (Pen pen = new Pen(UI.Border)) e.Graphics.DrawPath(pen, p);
                }
            };
            Controls.Add(_customPanel);

            Label l1 = UI.Text("起始文字", 9.5f, FontStyle.Regular, UI.TextSub);
            l1.SetBounds(16, 14, 70, 24);
            _start = UI.Input(_working.Start);
            _start.SetBounds(92, 12, 360, 26);
            Label l2 = UI.Text("结束文字", 9.5f, FontStyle.Regular, UI.TextSub);
            l2.SetBounds(16, 60, 70, 24);
            _end = UI.Input(_working.End);
            _end.SetBounds(92, 58, 360, 26);
            Label l3 = UI.Text("留空表示不限制该端（例如结束文字留空 = 从起始文字删到结尾）", 8.5f, FontStyle.Regular, UI.TextSub);
            l3.SetBounds(92, 88, 370, 22);
            _customPanel.Controls.AddRange(new Control[] { l1, _start, l2, _end, l3 });

            Button btnReset = UI.Flat("恢复默认", 96, 36, false);
            btnReset.SetBounds(24, 404, 96, 36);
            btnReset.Click += (s, e) =>
            {
                _start.Text = AppSettings.Presets[0][0];
                _end.Text = AppSettings.Presets[0][1];
                _radios[0].Checked = true;
                SelectPreset(0);
            };

            Button btnCancel = UI.Flat("取消", 88, 36, false);
            btnCancel.SetBounds(ClientSize.Width - 24 - 88, 404, 88, 36);
            btnCancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };

            Button btnOk = UI.Flat("保存", 104, 36, true);
            btnOk.SetBounds(ClientSize.Width - 24 - 88 - 12 - 104, 404, 104, 36);
            btnOk.Click += OnSave;

            Controls.AddRange(new Control[] { h, sub, btnReset, btnCancel, btnOk });
            AcceptButton = btnOk;
            CancelButton = btnCancel;
            SelectPreset(_working.Preset);
        }

        private void SelectPreset(int idx)
        {
            _customPanel.Enabled = (idx == 0);
            _start.Enabled = _end.Enabled = (idx == 0);
            _customPanel.Invalidate();
        }

        private void OnSave(object sender, EventArgs e)
        {
            if (_radios[0].Checked)
            {
                string a = Cleaner.NormalizeMarker(_start.Text), b = Cleaner.NormalizeMarker(_end.Text);
                if (a.Length == 0 && b.Length == 0)
                {
                    MessageBox.Show(this, "请至少填写一个删除标记（起始文字或结束文字）。", "提示",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                ResultPreset = 0;
                ResultStart = a;
                ResultEnd = b;
            }
            else if (_radios[1].Checked)
            {
                ResultPreset = 1; ResultStart = AppSettings.Presets[0][0]; ResultEnd = AppSettings.Presets[0][1];
            }
            else
            {
                ResultPreset = 2; ResultStart = AppSettings.Presets[1][0]; ResultEnd = AppSettings.Presets[1][1];
            }
            DialogResult = DialogResult.OK;
            Close();
        }
    }

    // ---------------- 自检 ----------------
    internal static class SelfTest
    {
        public static void Run()
        {
            StringBuilder sb = new StringBuilder();
            int pass = 0, fail = 0;

            Action<string, string, string, string> t = (name, input, expected, startEnd) =>
            {
                string[] parts = startEnd.Split('|');
                CleanResult r = Cleaner.Clean(input, parts[0], parts[1]);
                bool ok = r.Text == expected;
                if (ok) pass++; else fail++;
                sb.AppendLine((ok ? "[PASS] " : "[FAIL] ") + name);
                if (!ok)
                {
                    sb.AppendLine("  expected: " + Show(expected));
                    sb.AppendLine("  actual  : " + Show(r.Text));
                    sb.AppendLine("  blocks=" + r.Blocks + " startFound=" + r.StartFound
                        + " endFound=" + r.EndFound + " endMissing=" + r.EndMissing);
                }
            };

            t("基本删除：只删指定段落",
                "You are a helpful AI assistant\r\n\r\n已删除内容\r\n\r\nDo you understand?\r\n正文开始。",
                "正文开始。",
                "You are a helpful AI assistant|Do you understand?");

            t("保留前面文字与原有空行",
                "问题：什么是HADS？\r\nYou are a helpful AI assistant\r\n废话\r\nDo you understand?\r\n答案：HADS是……",
                "问题：什么是HADS？\r\n\r\n答案：HADS是……",
                "You are a helpful AI assistant|Do you understand?");

            t("全角引号差异",
                "前言\r\n“You are a helpful AI assistant”\r\n内容\r\n“Do you understand?”\r\n结尾",
                "前言\r\n\r\n结尾",
                "You are a helpful AI assistant|Do you understand?");

            t("换行与多空格差异",
                "A\r\nYou  are a\r\nhelpful AI assistant\r\nX\r\nDo you\r\nunderstand?\r\nB",
                "A\r\n\r\nB",
                "You are a helpful AI assistant|Do you understand?");

            t("无标记时不改动",
                "完全无关的文字\r\n第二行",
                "完全无关的文字\r\n第二行",
                "You are a helpful AI assistant|Do you understand?");

            t("结束标记留空则删到结尾",
                "保留部分\r\nTo uphold Coursera\r\n后面全删",
                "保留部分",
                "To uphold Coursera|");

            t("结束标记找不到时保留原文",
                "保留部分\r\nTo uphold Coursera\r\n后面还有内容",
                "保留部分\r\nTo uphold Coursera\r\n后面还有内容",
                "To uphold Coursera|不存在的结束标记");

            t("多段全部删除（标记对各自配对）",
                "头\r\nS1\r\nE1\r\n中\r\nS2\r\nE2\r\n尾",
                "头\r\n\r\n中\r\nS2\r\nE2\r\n尾",
                "S1|E1");

            t("删除第一对到第二对之间的全部内容",
                "头\r\nS1\r\nE1\r\n中\r\nS2\r\nE2\r\n尾",
                "头\r\n\r\n尾",
                "S1|E2");

            t("英文括号里的删除区",
                "Before. To uphold Coursera's policy (see related topics.) After.",
                "Before.  After.",
                "To uphold Coursera|related topics.");

            t("标记首尾空白自动清理",
                "前言\r\n  You are a helpful AI assistant  \r\n内容\r\n  Do you understand?  \r\n结尾",
                "前言\r\n\r\n  \r\n结尾",
                "  You are a helpful AI assistant  |  Do you understand?  ");

            t("找不到起始标记且结束标记留空",
                "任意内容",
                "任意内容",
                "不存在的标记|");

            t("其余内容（含空行、行尾空格、制表符）保持原样，只删标记所在行",
                "第一段\r\n\r\n\r\n第二段  \r\nS1\r\nE1\r\n第三段\t\r\n\r\n第四段",
                "第一段\r\n\r\n\r\n第二段  \r\n\r\n第三段\t\r\n\r\n第四段",
                "S1|E1");

            sb.AppendLine();
            sb.AppendLine("PASS=" + pass + " FAIL=" + fail);

            try
            {
                string outPath = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "selftest.txt");
                File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(true));
            }
            catch { }
        }

        private static string Show(string s)
        {
            return "\"" + (s ?? "").Replace("\r", "\\r").Replace("\n", "\\n") + "\"";
        }
    }
}
