// UiTest.cs
// 界面逻辑冒烟测试（不显示窗口）：验证「列出原文件夹下所有子文件夹」「单独勾选任意子文件夹」
// 以及勾选结果收集、父子去重的逻辑。
// 编译：uitest.bat
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace BackupHelper
{
    public static class UiTest
    {
        private static int _passed;
        private static int _failed;

        [STAThread]
        public static int Main(string[] args)
        {
            try { Console.OutputEncoding = System.Text.Encoding.UTF8; }
            catch { }

            string root = Path.Combine(Path.GetTempPath(), "BackupHelperUiTest_" + Guid.NewGuid().ToString("N"));
            string src = Path.Combine(root, "src");

            try
            {
                Directory.CreateDirectory(Path.Combine(src, "A", "A1"));
                Directory.CreateDirectory(Path.Combine(src, "B"));
                Directory.CreateDirectory(Path.Combine(src, "C", "C1", "C2"));

                MainForm form = new MainForm();
                form.CreateControl();                       // 创建窗口句柄，但不显示界面

                TextBox txtSource = (TextBox)GetField(form, "_txtSource");
                TextBox txtBackup = (TextBox)GetField(form, "_txtBackup");
                TreeView tree = (TreeView)GetField(form, "_tree");
                CheckBox chkSkip = (CheckBox)GetField(form, "_chkSkipIdentical");

                Check(tree.CheckBoxes, "列表带复选框（可单独选择任意子文件夹）");
                Check(chkSkip.Checked, "默认勾选「跳过内容完全相同的文件」");

                // 两个路径输入框前要有说明文字，而且要真的加到窗体上（曾出现过标签创建了却没 Add 而不显示的问题）
                Label lblSourcePath = (Label)GetField(form, "_lblSourcePath");
                Label lblBackupPath = (Label)GetField(form, "_lblBackupPath");

                Check(form.Controls.Contains(lblSourcePath) && lblSourcePath.Text.Contains("原文件夹"),
                    "原文件夹输入框前显示说明文字：" + lblSourcePath.Text);
                Check(form.Controls.Contains(lblBackupPath) && lblBackupPath.Text.Contains("备份文件夹"),
                    "备份文件夹输入框前显示说明文字：" + lblBackupPath.Text);
                Check(lblSourcePath.Text != lblBackupPath.Text, "两处说明文字不同，能区分是哪种路径");
                Check(lblSourcePath.Right <= txtSource.Left && lblBackupPath.Right <= txtBackup.Left,
                    "说明文字位于输入框左侧、不遮挡输入内容（说明右边界 " + lblSourcePath.Right +
                    " / 输入框左边界 " + txtSource.Left + "）");
                Check(form.Controls.Contains((Label)GetField(form, "_lblStatus")) &&
                      form.Controls.Contains((Label)GetField(form, "_lblHint")),
                    "底部状态文字与勾选提示文字也都已显示在界面上");

                txtBackup.Text = Path.Combine(root, "bak");
                txtSource.Text = src;
                Invoke(form, "LoadFolders");

                Check(tree.Nodes.Count == 3, "列出 3 个一级子文件夹（实际 " + tree.Nodes.Count + "）");
                Check(Find(tree.Nodes, "A") != null && Find(tree.Nodes, "B") != null && Find(tree.Nodes, "C") != null,
                    "一级子文件夹名称正确：A / B / C");

                // 展开 C：触发真实的 BeforeExpand 事件（等价于用户点开列表箭头）
                TreeNode nodeC = Find(tree.Nodes, "C");
                RaiseEvent(tree, "OnBeforeExpand", new TreeViewCancelEventArgs(nodeC, false, TreeViewAction.Expand));
                Check(nodeC.Nodes.Count == 1 && Find(nodeC.Nodes, "C1") != null,
                    "展开 C 后列出深层子文件夹 C1（BeforeExpand 事件已接线）");

                TreeNode nodeC1 = Find(nodeC.Nodes, "C1");
                if (nodeC1 == null) throw new InvalidOperationException("未列出 C1，后续检查无法进行");
                RaiseEvent(tree, "OnBeforeExpand", new TreeViewCancelEventArgs(nodeC1, false, TreeViewAction.Expand));
                Check(Find(nodeC1.Nodes, "C2") != null, "继续展开 C1 后列出 C2");

                // 单独勾选某个子文件夹 -> 只备份它（模拟用户点击复选框，触发 AfterCheck）
                ClickCheck(tree, Find(tree.Nodes, "B"), true);
                List<string> sel = Collect(form);
                Check(sel.Count == 1 && sel[0] == Path.Combine(src, "B"), "单独勾选 B -> 只收集 B（实际 " + sel.Count + " 项）");

                // 勾选父级 -> 子级自动勾选，且收集时不会重复
                ClickCheck(tree, Find(tree.Nodes, "B"), false);
                ClickCheck(tree, nodeC, true);
                Check(Find(nodeC.Nodes, "C1").Checked && Find(nodeC1.Nodes, "C2").Checked,
                    "勾选父文件夹后子文件夹自动勾选（AfterCheck 事件已接线）");
                sel = Collect(form);
                Check(sel.Count == 1 && sel[0] == Path.Combine(src, "C"), "父级勾选时只收集父级（去重，实际 " + sel.Count + " 项）");

                // 父级取消勾选 -> 子级也取消
                ClickCheck(tree, nodeC, false);
                Check(!Find(nodeC.Nodes, "C1").Checked && !Find(nodeC1.Nodes, "C2").Checked, "父文件夹取消勾选后子文件夹同步取消");

                // 只勾选深层子文件夹
                ClickCheck(tree, Find(nodeC1.Nodes, "C2"), true);
                sel = Collect(form);
                Check(sel.Count == 1 && sel[0] == Path.Combine(src, "C", "C1", "C2"),
                    "单独勾选深层子文件夹 C2 -> 按相对路径收集（实际 " + (sel.Count > 0 ? sel[0] : "无") + "）");

                // 多选
                ClickCheck(tree, Find(tree.Nodes, "A"), true);
                ClickCheck(tree, Find(tree.Nodes, "B"), true);
                sel = Collect(form);
                Check(sel.Count == 3, "勾选 A / B / C2 -> 收集 3 个文件夹（实际 " + sel.Count + "）");

                // 全不选
                Invoke(form, "SetAllChecked", false);
                sel = Collect(form);
                Check(sel.Count == 0, "「全不选」后没有勾选项（实际 " + sel.Count + " 项）");

                form.Dispose();
            }
            catch (Exception ex)
            {
                _failed++;
                Console.WriteLine("[异常] " + ex);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }

            Console.WriteLine();
            Console.WriteLine("==== 通过 " + _passed + " 项，失败 " + _failed + " 项 ====");
            return _failed == 0 ? 0 : 1;
        }

        // ---------------------------------------------------------------- 辅助方法

        private static object GetField(object target, string name)
        {
            FieldInfo field = target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
            if (field == null) throw new InvalidOperationException("找不到字段：" + name);
            return field.GetValue(target);
        }

        private static void Invoke(object target, string name, params object[] parameters)
        {
            MethodInfo method = target.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance);
            if (method == null) throw new InvalidOperationException("找不到方法：" + name);
            method.Invoke(target, parameters);
        }

        private static List<string> Collect(object target)
        {
            MethodInfo method = target.GetType().GetMethod("CollectSelection", BindingFlags.NonPublic | BindingFlags.Instance);
            if (method == null) throw new InvalidOperationException("找不到方法：CollectSelection");
            return (List<string>)method.Invoke(target, null);
        }

        private static TreeNode Find(TreeNodeCollection nodes, string text)
        {
            foreach (TreeNode node in nodes)
            {
                if (string.Equals(node.Text, text, StringComparison.OrdinalIgnoreCase)) return node;
            }
            return null;
        }

        /// <summary>触发 TreeView 的受保护事件（等价于真实运行时用户操作所触发的事件，用于验证事件确实已接线）</summary>
        private static void RaiseEvent(TreeView tree, string methodName, object args)
        {
            MethodInfo method = typeof(TreeView).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance);
            if (method == null) throw new InvalidOperationException("找不到方法：" + methodName);
            method.Invoke(tree, new object[] { args });
        }

        /// <summary>模拟用户点击复选框：改变勾选状态并触发 AfterCheck 事件</summary>
        private static void ClickCheck(TreeView tree, TreeNode node, bool value)
        {
            node.Checked = value;
            RaiseEvent(tree, "OnAfterCheck", new TreeViewEventArgs(node, TreeViewAction.ByMouse));
        }

        private static void Check(bool condition, string what)
        {
            if (condition) { _passed++; Console.WriteLine("  [通过] " + what); }
            else { _failed++; Console.WriteLine("  [失败] " + what); }
        }
    }
}
