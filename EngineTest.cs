// EngineTest.cs
// 控制台测试程序：真实验证备份核心逻辑（合并同名文件夹、同名文件自动重命名、跳过相同文件等）
// 编译：test.bat   运行：EngineTest.exe
using System;
using System.IO;

namespace BackupHelper
{
    public static class EngineTest
    {
        private static int _passed;
        private static int _failed;

        public static int Main(string[] args)
        {
            try { Console.OutputEncoding = System.Text.Encoding.UTF8; }
            catch { /* 某些重定向环境不允许设置编码 */ }

            string root = Path.Combine(Path.GetTempPath(), "BackupHelperTest_" + Guid.NewGuid().ToString("N"));
            string src = Path.Combine(root, "src");
            string bak = Path.Combine(root, "bak");
            Console.WriteLine("测试目录：" + root);
            Console.WriteLine();

            try
            {
                TestMergeAndRename(src, bak);
                TestNewFolder(src, bak);
                TestNestedSelection(src, bak);
                TestOverwrite(src, bak);
                TestOverlappingSelection(src, bak);
                TestSameFolderError(src);
                TestBackupInsideSource(src);
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

        // ---------------------------------------------------------------- 测试用例

        private static void TestMergeAndRename(string src, string bak)
        {
            Console.WriteLine("场景 1：备份目录已有同名文件夹 -> 合并；同名文件 -> 自动重命名；内容相同 -> 跳过");
            WriteText(Path.Combine(src, "Data", "a.txt"), "A-new");
            WriteText(Path.Combine(src, "Data", "sub", "b.txt"), "B");
            WriteText(Path.Combine(bak, "Data", "a.txt"), "A-old");
            WriteText(Path.Combine(bak, "Data", "extra.txt"), "keep");
            WriteText(Path.Combine(bak, "Data", "sub", "b.txt"), "B");

            BackupResult r = RunBackup(src, bak, new string[] { Path.Combine(src, "Data") }, false, true);

            Check(ReadText(Path.Combine(bak, "Data", "a.txt")) == "A-old", "原有同名文件 a.txt 未被改动");
            Check(ReadText(Path.Combine(bak, "Data", "a (1).txt")) == "A-new", "新文件被自动重命名为 a (1).txt");
            Check(ReadText(Path.Combine(bak, "Data", "extra.txt")) == "keep", "原有其他文件 extra.txt 保留");
            Check(ReadText(Path.Combine(bak, "Data", "sub", "b.txt")) == "B", "子文件夹已合并");
            Check(!File.Exists(Path.Combine(bak, "Data", "sub", "b (1).txt")), "内容相同的同名文件未产生副本");
            Check(r.FilesRenamed == 1, "自动重命名计数 = 1（实际 " + r.FilesRenamed + "）");
            Check(r.FilesSkipped == 1, "跳过相同文件计数 = 1（实际 " + r.FilesSkipped + "）");
            Check(r.Errors == 0, "没有错误（实际 " + r.Errors + "）");
            Console.WriteLine();
        }

        private static void TestNewFolder(string src, string bak)
        {
            Console.WriteLine("场景 2：备份目录没有同名文件夹 -> 新建文件夹后整份备份");
            WriteText(Path.Combine(src, "NewFolder", "n.txt"), "N");
            WriteText(Path.Combine(src, "NewFolder", "deep", "d.txt"), "D");
            BackupResult r = RunBackup(src, bak, new string[] { Path.Combine(src, "NewFolder") }, false, true);

            Check(ReadText(Path.Combine(bak, "NewFolder", "n.txt")) == "N", "新建 NewFolder 并复制 n.txt");
            Check(ReadText(Path.Combine(bak, "NewFolder", "deep", "d.txt")) == "D", "深层子文件夹一并复制");
            Check(r.FoldersCreated >= 2, "新建文件夹计数 >= 2（实际 " + r.FoldersCreated + "）");
            Console.WriteLine();
        }

        private static void TestNestedSelection(string src, string bak)
        {
            Console.WriteLine("场景 3：单独勾选深层子文件夹 -> 按相对路径备份到 备份目录\\Data\\sub");
            RunBackup(src, bak, new string[] { Path.Combine(src, "Data", "sub") }, false, false);

            Check(File.Exists(Path.Combine(bak, "Data", "sub", "b.txt")), "备份到 备份目录\\Data\\sub 下");
            Check(File.Exists(Path.Combine(bak, "Data", "sub", "b (1).txt")), "同名文件在深层目录同样自动重命名");
            Check(!File.Exists(Path.Combine(bak, "sub", "b.txt")), "没有错误地放到备份根目录");
            Console.WriteLine();
        }

        private static void TestOverwrite(string src, string bak)
        {
            Console.WriteLine("场景 4：覆盖模式（同名文件直接替换）");
            BackupResult r = RunBackup(src, bak, new string[] { Path.Combine(src, "Data") }, true, false);

            Check(ReadText(Path.Combine(bak, "Data", "a.txt")) == "A-new", "a.txt 已被覆盖为最新内容");
            Check(ReadText(Path.Combine(bak, "Data", "a (1).txt")) == "A-new", "之前重命名的 a (1).txt 仍然保留");
            Check(r.Errors == 0, "覆盖模式没有错误（实际 " + r.Errors + "）");
            Console.WriteLine();
        }

        private static void TestOverlappingSelection(string src, string bak)
        {
            Console.WriteLine("场景 5：父子文件夹同时勾选 -> 自动去重");
            BackupResult r = RunBackup(src, bak,
                new string[] { Path.Combine(src, "Data"), Path.Combine(src, "Data", "sub") }, false, true);

            Check(r.TotalFiles == 2, "只统计父文件夹里的 2 个文件（实际 " + r.TotalFiles + "）");
            Console.WriteLine();
        }

        private static void TestSameFolderError(string src)
        {
            Console.WriteLine("场景 6：原文件夹与备份文件夹相同 -> 应当报错");
            bool threw = false;
            try { RunBackup(src, src, new string[] { Path.Combine(src, "Data") }, false, true); }
            catch (ArgumentException) { threw = true; }
            Check(threw, "抛出了 ArgumentException 并给出提示");
            Console.WriteLine();
        }

        private static void TestBackupInsideSource(string src)
        {
            Console.WriteLine("场景 7：备份目录位于被备份文件夹内部 -> 跳过备份目录自身，避免无限递归");
            string bak = Path.Combine(src, "Data", "bakdir");
            WriteText(Path.Combine(src, "Data", "c.txt"), "C");

            BackupResult r = RunBackup(src, bak, new string[] { Path.Combine(src, "Data") }, false, true);

            Check(File.Exists(Path.Combine(bak, "Data", "c.txt")), "其他文件仍然正常备份");
            Check(!Directory.Exists(Path.Combine(bak, "Data", "bakdir")), "备份目录没有被递归复制进自己");
            Check(r.TotalFiles == 3, "统计文件数时已排除备份目录（实际 " + r.TotalFiles + "）");
            Check(r.Errors == 0, "没有错误（实际 " + r.Errors + "）");
            Console.WriteLine();
        }

        // ---------------------------------------------------------------- 辅助方法

        private static BackupResult RunBackup(string srcRoot, string bakRoot, string[] folders, bool overwrite, bool skipIdentical)
        {
            BackupOptions opt = new BackupOptions
            {
                OverwriteExisting = overwrite,
                SkipIdenticalFiles = skipIdentical
            };
            BackupEngine engine = new BackupEngine(opt, delegate (string m) { Console.WriteLine("      " + m); }, null);
            return engine.Run(srcRoot, bakRoot, folders);
        }

        private static void Check(bool condition, string what)
        {
            if (condition) { _passed++; Console.WriteLine("  [通过] " + what); }
            else { _failed++; Console.WriteLine("  [失败] " + what); }
        }

        private static string ReadText(string path)
        {
            try { return File.Exists(path) ? File.ReadAllText(path) : null; }
            catch { return null; }
        }

        private static void WriteText(string path, string text)
        {
            string dir = Path.GetDirectoryName(path);
            if (dir != null && dir.Length > 0 && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, text);
        }
    }
}
