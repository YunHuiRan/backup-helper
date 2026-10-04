// BackupHelper.cs
// Windows 原生图形界面（WinForms）
//   1) 选择「原文件夹路径」和「备份文件夹路径」
//   2) 自动列出原文件夹下所有子文件夹（树形，可逐级展开），可单独勾选任意子文件夹
//   3) 把勾选的子文件夹备份到备份文件夹：目标已存在同名文件夹则合并（逐个文件），
//      不存在则新建文件夹；合并时遇到同名文件自动重命名（xxx (1).ext），不会覆盖原有数据
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace BackupHelper
{
    public class MainForm : Form
    {
        private TextBox _txtSource;
        private TextBox _txtBackup;
        private TextBox _txtLog;
        private Button _btnBrowseSource;
        private Button _btnBrowseBackup;
        private Button _btnRefresh;
        private Button _btnSelectAll;
        private Button _btnUnselectAll;
        private Button _btnExpandAll;
        private Button _btnCollapseAll;
        private Button _btnBackup;
        private Button _btnOpenBackup;
        private CheckBox _chkOverwrite;
        private CheckBox _chkSkipIdentical;
        private ProgressBar _progress;
        private Label _lblStatus;
        private Label _lblHint;
        private Label _lblSourcePath;
        private Label _lblBackupPath;
        private TreeView _tree;

        private bool _suppressCheck;
        private volatile bool _running;
        private BackupEngine _engine;
        private int _logLines;
        private const int LogMaxLines = 5000;

        public MainForm()
        {
            BuildUi();
        }

        private void BuildUi()
        {
            Text = "备份助手 —— 文件夹选择性备份（同名文件夹合并 / 同名文件自动重命名）";
            ClientSize = new Size(900, 720);
            MinimumSize = new Size(780, 660);
            StartPosition = FormStartPosition.CenterScreen;
            Font = SystemFonts.MessageBoxFont;

            // ---------------- 第一行：原文件夹（要备份的文件夹） ----------------
            _lblSourcePath = MakeLabel("原文件夹（要备份的文件夹）：", 12, 17, 190);
            _txtSource = MakeTextBox(206, 12, 560);
            _btnBrowseSource = MakeButton("浏览(&S)...", 776, 11, 112, 26);
            _btnBrowseSource.Click += delegate { BrowseFolder(_txtSource, "选择原文件夹"); };

            // ---------------- 第二行：备份文件夹（备份存到哪里） ----------------
            _lblBackupPath = MakeLabel("备份文件夹（备份存到哪里）：", 12, 57, 190);
            _txtBackup = MakeTextBox(206, 52, 560);
            _btnBrowseBackup = MakeButton("浏览(&B)...", 776, 51, 112, 26);
            _btnBrowseBackup.Click += delegate { BrowseFolder(_txtBackup, "选择备份文件夹"); };

            // ---------------- 第三行：子文件夹操作 ----------------
            _btnRefresh = MakeButton("刷新子文件夹列表", 12, 90, 150, 28);
            _btnRefresh.Click += delegate { LoadFolders(); };

            _btnSelectAll = MakeButton("全选", 170, 90, 80, 28);
            _btnSelectAll.Click += delegate { SetAllChecked(true); };

            _btnUnselectAll = MakeButton("全不选", 258, 90, 80, 28);
            _btnUnselectAll.Click += delegate { SetAllChecked(false); };

            _btnExpandAll = MakeButton("展开全部", 346, 90, 90, 28);
            _btnExpandAll.Click += delegate { _tree.ExpandAll(); };

            _btnCollapseAll = MakeButton("折叠全部", 444, 90, 90, 28);
            _btnCollapseAll.Click += delegate { _tree.CollapseAll(); };

            _lblHint = MakeLabel("勾选需要备份的子文件夹（可逐级展开选择）", 546, 97, 342);

            // ---------------- 子文件夹树 ----------------
            _tree = new TreeView
            {
                Location = new Point(12, 126),
                Size = new Size(876, 340),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                CheckBoxes = true,
                HideSelection = false,
                ShowLines = false,
                ShowRootLines = true,
                FullRowSelect = false,
                PathSeparator = "\\"
            };
            _tree.AfterCheck += Tree_AfterCheck;
            _tree.BeforeExpand += Tree_BeforeExpand;
            Controls.Add(_tree);

            // ---------------- 底部：选项 ----------------
            _chkOverwrite = new CheckBox
            {
                Text = "同名文件直接覆盖（默认：自动重命名并保留两份）",
                Location = new Point(12, 476),
                Size = new Size(440, 22),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left
            };
            Controls.Add(_chkOverwrite);

            _chkSkipIdentical = new CheckBox
            {
                Text = "跳过内容完全相同的文件（避免重复备份产生副本）",
                Location = new Point(460, 476),
                Size = new Size(430, 22),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
                Checked = true
            };
            Controls.Add(_chkSkipIdentical);

            // ---------------- 底部：操作按钮 ----------------
            _btnBackup = MakeButton("开始备份(&R)", 12, 506, 136, 36);
            _btnBackup.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            _btnBackup.Font = new Font(Font, FontStyle.Bold);
            _btnBackup.Click += delegate { Backup_Click(); };

            _btnOpenBackup = MakeButton("打开备份目录", 158, 506, 130, 36);
            _btnOpenBackup.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            _btnOpenBackup.Click += delegate { OpenFolder(_txtBackup.Text.Trim()); };

            _lblStatus = MakeLabel("就绪。请先选择原文件夹与备份文件夹。", 300, 514, 588);
            _lblStatus.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;

            _progress = new ProgressBar
            {
                Location = new Point(12, 552),
                Size = new Size(876, 18),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                Minimum = 0,
                Maximum = 100
            };
            Controls.Add(_progress);

            // ---------------- 底部：日志 ----------------
            _txtLog = new TextBox
            {
                Location = new Point(12, 578),
                Size = new Size(876, 130),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BackColor = Color.FromArgb(250, 250, 250),
                Font = new Font(FontFamily.GenericMonospace, 9F)
            };
            Controls.Add(_txtLog);

            EnableDrop(_txtSource);
            EnableDrop(_txtBackup);

            AppendLog("欢迎使用备份助手。步骤：选择两个文件夹 -> 勾选子文件夹 -> 开始备份。");
        }

        // ---------------------------------------------------------------- 控件小工具

        private Label MakeLabel(string text, int x, int y, int width)
        {
            Label label = new Label
            {
                Text = text,
                Location = new Point(x, y),
                Size = new Size(width, 20),
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true   // 文字过长时显示省略号，不会压到输入框上
            };
            Controls.Add(label);         // 注意：必须加入窗体，否则标签不会显示
            return label;
        }

        private TextBox MakeTextBox(int x, int y, int width)
        {
            TextBox box = new TextBox
            {
                Location = new Point(x, y),
                Size = new Size(width, 24),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            Controls.Add(box);
            return box;
        }

        private Button MakeButton(string text, int x, int y, int width, int height)
        {
            Button button = new Button
            {
                Text = text,
                Location = new Point(x, y),
                Size = new Size(width, height),
                Anchor = AnchorStyles.Top | AnchorStyles.Left
            };
            Controls.Add(button);
            return button;
        }

        private void EnableDrop(TextBox box)
        {
            box.AllowDrop = true;
            box.DragEnter += delegate (object s, DragEventArgs e)
            {
                if (e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop))
                    e.Effect = DragDropEffects.Copy;
                else
                    e.Effect = DragDropEffects.None;
            };
            box.DragDrop += delegate (object s, DragEventArgs e)
            {
                if (e.Data == null || !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
                string[] items = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (items != null && items.Length > 0 && Directory.Exists(items[0]))
                    box.Text = items[0];
            };
        }

        // ---------------------------------------------------------------- 子文件夹树

        private void LoadFolders()
        {
            string src = _txtSource.Text.Trim();
            if (src.Length == 0)
            {
                MessageBox.Show(this, "请先选择原文件夹路径。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (!Directory.Exists(src))
            {
                MessageBox.Show(this, "原文件夹不存在：\r\n" + src, "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            Cursor old = Cursor;
            Cursor = Cursors.WaitCursor;
            _tree.BeginUpdate();
            try
            {
                _tree.Nodes.Clear();
                foreach (string dir in ListDirectories(src)) _tree.Nodes.Add(CreateNode(dir));
            }
            finally
            {
                _tree.EndUpdate();
                Cursor = old;
            }

            _lblHint.Text = "已列出 " + _tree.Nodes.Count + " 个一级子文件夹，可展开查看更深层。";
            AppendLog("已列出原文件夹下的子文件夹：" + _tree.Nodes.Count + " 个（" + src + "）");
        }

        private string[] ListDirectories(string dir)
        {
            try
            {
                string[] all = Directory.GetDirectories(dir);
                List<string> result = new List<string>(all.Length);
                foreach (string d in all)
                {
                    try
                    {
                        if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0) continue; // 符号链接/联接点
                    }
                    catch { continue; }
                    result.Add(d);
                }
                return result.ToArray();
            }
            catch (Exception ex)
            {
                AppendLog("无法读取文件夹（已跳过）：" + dir + " -> " + ex.Message);
                return [];
            }
        }

        private static TreeNode CreateNode(string path)
        {
            TreeNode node = new TreeNode(Path.GetFileName(path))
            {
                Tag = path
            };
            node.Nodes.Add(new TreeNode(string.Empty));   // 占位，保证显示展开箭头
            return node;
        }

        /// <summary>按需加载子节点（懒加载，避免超大目录一次性展开）</summary>
        private void EnsureChildrenLoaded(TreeNode node)
        {
            if (node.Tag == null) return;
            if (node.Nodes.Count != 1 || node.Nodes[0].Tag != null) return;   // 已经加载过
            node.Nodes.Clear();
            foreach (string dir in ListDirectories((string)node.Tag)) node.Nodes.Add(CreateNode(dir));
        }

        private void Tree_BeforeExpand(object sender, TreeViewCancelEventArgs e)
        {
            EnsureChildrenLoaded(e.Node);
        }

        private void Tree_AfterCheck(object sender, TreeViewEventArgs e)
        {
            if (_suppressCheck || e.Action == TreeViewAction.Unknown) return;
            _suppressCheck = true;
            try { SetChecked(e.Node, e.Node.Checked); }
            finally { _suppressCheck = false; }
        }

        /// <summary>勾选/取消勾选时，子文件夹一起同步</summary>
        private void SetChecked(TreeNode node, bool value)
        {
            node.Checked = value;
            if (value) EnsureChildrenLoaded(node);
            foreach (TreeNode child in node.Nodes)
            {
                if (child.Tag == null) continue;    // 占位节点
                SetChecked(child, value);
            }
        }

        private void SetAllChecked(bool value)
        {
            Cursor old = Cursor;
            Cursor = Cursors.WaitCursor;
            _suppressCheck = true;
            try
            {
                foreach (TreeNode node in _tree.Nodes) SetChecked(node, value);
            }
            finally
            {
                _suppressCheck = false;
                Cursor = old;
            }
        }

        /// <summary>收集勾选的文件夹；父文件夹已勾选时不再重复收集它的子文件夹</summary>
        private List<string> CollectSelection()
        {
            List<string> list = new List<string>();
            foreach (TreeNode node in _tree.Nodes) Collect(node, false, list);
            return list;
        }

        private static void Collect(TreeNode node, bool parentChecked, List<string> list)
        {
            if (node.Tag == null) return;
            bool self = node.Checked;
            if (self && !parentChecked) list.Add((string)node.Tag);
            foreach (TreeNode child in node.Nodes) Collect(child, parentChecked || self, list);
        }

        // ---------------------------------------------------------------- 通用操作

        private void BrowseFolder(TextBox target, string description)
        {
            using FolderBrowserDialog dialog = new FolderBrowserDialog();
            dialog.Description = description;
            dialog.ShowNewFolderButton = true;
            string current = target.Text.Trim();
            if (current.Length > 0 && Directory.Exists(current)) dialog.SelectedPath = current;

            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                target.Text = dialog.SelectedPath;
                if (target == _txtSource) LoadFolders();
            }
        }

        private void OpenFolder(string path)
        {
            if (path.Length == 0 || !Directory.Exists(path))
            {
                MessageBox.Show(this, "备份文件夹还不存在。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            try { Process.Start("explorer.exe", "\"" + path + "\""); }
            catch (Exception ex) { MessageBox.Show(this, "无法打开文件夹：" + ex.Message, "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        // ---------------------------------------------------------------- 日志与进度

        private void AppendLog(string message)
        {
            if (_txtLog.IsDisposed) return;
            if (_logLines >= LogMaxLines)      // 日志太长时清空旧内容，避免界面变慢
            {
                _txtLog.Clear();
                _logLines = 0;
                _txtLog.AppendText("（日志过长，已清空较早内容）" + Environment.NewLine);
            }
            _txtLog.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + message + Environment.NewLine);
            _logLines++;
        }

        private void LogFromWorker(string message)
        {
            try { BeginInvoke(new Action<string>(AppendLog), message); }
            catch (InvalidOperationException) { /* 窗口已经关闭 */ }
        }

        private void ProgressFromWorker(long done, long total)
        {
            try { BeginInvoke(new Action<long, long>(UpdateProgress), done, total); }
            catch (InvalidOperationException) { }
        }

        private void UpdateProgress(long done, long total)
        {
            long percent = total > 0 ? done * 100 / total : 0;
            if (percent < 0) percent = 0;
            if (percent > 100) percent = 100;
            if (percent != _progress.Value) _progress.Value = (int)percent;
            _lblStatus.Text = "正在备份：" + done + " / " + total + " 个文件（" + percent + "%）";
        }

        // ---------------------------------------------------------------- 执行备份

        private void Backup_Click()
        {
            if (_running)
            {
                _engine?.Cancel();
                _lblStatus.Text = "正在取消，请稍候...";
                _btnBackup.Enabled = false;
                return;
            }

            string src = _txtSource.Text.Trim();
            string bak = _txtBackup.Text.Trim();

            if (src.Length == 0 || !Directory.Exists(src))
            {
                MessageBox.Show(this, "请先选择有效的原文件夹路径。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (bak.Length == 0)
            {
                MessageBox.Show(this, "请先选择备份文件夹路径。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (!Directory.Exists(bak))
            {
                DialogResult answer = MessageBox.Show(this, "备份文件夹不存在，是否现在创建？\r\n\r\n" + bak,
                    "提示", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (answer != DialogResult.Yes) return;
                try { Directory.CreateDirectory(bak); }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "无法创建备份文件夹：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
            }
            if (string.Equals(Path.GetFullPath(src), Path.GetFullPath(bak), StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(this, "原文件夹和备份文件夹不能是同一个文件夹。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            List<string> selection = CollectSelection();
            if (selection.Count == 0)
            {
                MessageBox.Show(this, "请先在上面的列表中勾选需要备份的子文件夹。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            BackupOptions options = new BackupOptions
            {
                OverwriteExisting = _chkOverwrite.Checked,
                SkipIdenticalFiles = _chkSkipIdentical.Checked
            };
            _engine = new BackupEngine(options, LogFromWorker, ProgressFromWorker);

            AppendLog("----------------------------------------------");
            AppendLog("开始备份：选中 " + selection.Count + " 个文件夹  ->  " + bak);
            AppendLog(options.OverwriteExisting
                ? "同名文件处理：直接覆盖"
                : "同名文件处理：自动重命名并保留两份（同名文件夹自动合并）");

            _running = true;
            SetBusy(true);
            Thread worker = new Thread(delegate () { RunBackup(src, bak, selection); })
            {
                IsBackground = true
            };
            worker.Start();
        }

        private void RunBackup(string src, string bak, List<string> selection)
        {
            BackupResult result = null;
            Exception error = null;
            try { result = _engine.Run(src, bak, selection); }
            catch (Exception ex) { error = ex; }

            try { BeginInvoke(new Action<BackupResult, Exception>(FinishBackup), result, error); }
            catch (InvalidOperationException) { /* 窗口已经关闭 */ }
        }

        private void FinishBackup(BackupResult result, Exception error)
        {
            _running = false;
            SetBusy(false);
            _btnBackup.Enabled = true;
            _progress.Value = _progress.Maximum;

            if (error != null)
            {
                AppendLog("备份失败：" + error.Message);
                _lblStatus.Text = "备份失败。";
                MessageBox.Show(this, "备份失败：\r\n" + error.Message, "备份助手", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            string summary = "复制 " + result.FilesCopied + " 个文件，自动重命名 " + result.FilesRenamed
                + " 个，跳过相同文件 " + result.FilesSkipped + " 个，新建文件夹 " + result.FoldersCreated
                + " 个，错误 " + result.Errors + " 个。";

            AppendLog((result.Cancelled ? "备份已取消。" : "备份完成。") + summary);
            _lblStatus.Text = (result.Cancelled ? "已取消：" : "完成：") + summary;

            MessageBox.Show(this,
                (result.Cancelled ? "备份已取消（已复制的文件保留）。\r\n\r\n" : "备份完成！\r\n\r\n") + summary,
                "备份助手", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void SetBusy(bool busy)
        {
            _btnBackup.Text = busy ? "取消(&C)" : "开始备份(&R)";
            _btnRefresh.Enabled = !busy;
            _btnSelectAll.Enabled = !busy;
            _btnUnselectAll.Enabled = !busy;
            _btnExpandAll.Enabled = !busy;
            _btnCollapseAll.Enabled = !busy;
            _btnBrowseSource.Enabled = !busy;
            _btnBrowseBackup.Enabled = !busy;
            _chkOverwrite.Enabled = !busy;
            _chkSkipIdentical.Enabled = !busy;
            _tree.Enabled = !busy;
            Cursor = busy ? Cursors.AppStarting : Cursors.Default;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_running)
            {
                DialogResult answer = MessageBox.Show(this, "备份正在进行，确定要退出吗？", "确认",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (answer != DialogResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
                _engine?.Cancel();
            }
            base.OnFormClosing(e);
        }
    }

    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}
