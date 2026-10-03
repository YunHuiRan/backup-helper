// BackupEngine.cs
// 备份核心逻辑（不依赖界面，方便单独编译测试）
//
// 备份规则：
//   1) 选中文件夹在备份目录下的目标位置 = 备份目录 + 该文件夹相对「原文件夹」的相对路径；
//      例：原文件夹 D:\Src 的子文件夹 D:\Src\照片 -> 备份到 E:\Bak\照片
//   2) 目标文件夹不存在时自动新建（相当于整份拷贝）；
//   3) 目标文件夹已存在同名文件夹时，按文件逐个合并进去（已有文件不会被删除）；
//   4) 合并时如果遇到同名文件：默认「自动重命名」新来的文件（xxx (1).ext、xxx (2).ext ...），
//      保留原有文件不动；也可以选择「覆盖」模式直接替换；
//   5) 可选的「跳过内容完全相同的同名文件」可以避免重复备份产生大量副本。

using System;
using System.Collections.Generic;
using System.IO;

namespace BackupHelper
{
    /// <summary>备份选项</summary>
    public class BackupOptions
    {
        /// <summary>true = 同名文件直接覆盖；false = 自动重命名并保留两份（默认）</summary>
        public bool OverwriteExisting = false;

        /// <summary>同名且内容完全相同时跳过，不产生副本（默认 true）</summary>
        public bool SkipIdenticalFiles = true;

        /// <summary>跳过符号链接/联接点，避免无限递归（默认 true）</summary>
        public bool SkipReparsePoints = true;
    }

    /// <summary>备份结果统计</summary>
    public class BackupResult
    {
        /// <summary>新建的文件夹数量</summary>
        public int FoldersCreated;
        /// <summary>成功复制的文件数量（含改名后的文件）</summary>
        public int FilesCopied;
        /// <summary>因为同名而被自动重命名的文件数量</summary>
        public int FilesRenamed;
        /// <summary>因为内容相同而跳过的文件数量</summary>
        public int FilesSkipped;
        /// <summary>出错次数</summary>
        public int Errors;
        /// <summary>需要处理的文件总数</summary>
        public long TotalFiles;
        /// <summary>是否被用户取消</summary>
        public bool Cancelled;
    }

    /// <summary>备份执行器</summary>
    public class BackupEngine
    {
        private readonly BackupOptions _opt;
        private readonly Action<string> _log;
        private readonly Action<long, long> _progress;   // (已处理文件数, 总文件数)
        private readonly BackupResult _result = new BackupResult();
        private volatile bool _cancel;

        private string _sourceRoot = string.Empty;
        private string _backupRoot = string.Empty;

        public BackupEngine(BackupOptions options, Action<string> log, Action<long, long> progress)
        {
            _opt = options ?? new BackupOptions();
            _log = log;
            _progress = progress;
        }

        /// <summary>备份结果（Run 结束后有效，也可在运行中读取）</summary>
        public BackupResult Result { get { return _result; } }

        /// <summary>请求取消备份（会在处理完当前文件后停止）</summary>
        public void Cancel() { _cancel = true; }

        /// <summary>
        /// 执行备份。
        /// </summary>
        /// <param name="sourceRoot">原文件夹路径</param>
        /// <param name="backupRoot">备份文件夹路径</param>
        /// <param name="folders">要备份的文件夹绝对路径列表（通常位于原文件夹之下）</param>
        public BackupResult Run(string sourceRoot, string backupRoot, IList<string> folders)
        {
            _sourceRoot = Normalize(sourceRoot);
            _backupRoot = Normalize(backupRoot);

            if (_sourceRoot.Length == 0 || !Directory.Exists(_sourceRoot))
                throw new DirectoryNotFoundException("原文件夹不存在：" + sourceRoot);
            if (_backupRoot.Length == 0)
                throw new ArgumentException("备份文件夹路径不能为空。");
            if (string.Equals(_sourceRoot, _backupRoot, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("原文件夹和备份文件夹不能是同一个文件夹。");

            if (!Directory.Exists(_backupRoot))
            {
                Directory.CreateDirectory(_backupRoot);
                _result.FoldersCreated++;
                Log("创建备份根目录：" + _backupRoot);
            }

            List<string> roots = PrepareFolders(folders);
            _result.TotalFiles = CountFiles(roots);
            Log("需要备份的文件夹：" + roots.Count + " 个，文件总数：" + _result.TotalFiles + " 个。");

            long done = 0;
            foreach (string dir in roots)
            {
                if (_cancel) break;
                // 选中的文件夹映射到：备份目录 + 相对原文件夹的路径
                CopyDirectory(dir, Path.Combine(_backupRoot, RelativeName(dir)), ref done);
            }

            if (_cancel)
            {
                _result.Cancelled = true;
                Log("备份已被取消。");
            }
            Report(done, _result.TotalFiles);
            return _result;
        }

        // ---------------------------------------------------------------- 路径处理

        private static string Normalize(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            try
            {
                string full = Path.GetFullPath(path.Trim());
                return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>取文件夹相对原文件夹的路径（不在原文件夹之下时只用文件夹名）</summary>
        private string RelativeName(string folder)
        {
            string full = Normalize(folder);
            if (full.Length == 0) return string.Empty;
            if (string.Equals(full, _sourceRoot, StringComparison.OrdinalIgnoreCase)) return string.Empty;
            string prefix = _sourceRoot + Path.DirectorySeparatorChar;
            if (full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return full.Substring(prefix.Length);
            return Path.GetFileName(full);
        }

        /// <summary>去掉重复的、以及被父级文件夹覆盖的子文件夹</summary>
        private List<string> PrepareFolders(IList<string> folders)
        {
            List<string> valid = new List<string>();
            if (folders != null)
            {
                foreach (string f in folders)
                {
                    string full = Normalize(f);
                    if (full.Length == 0 || !Directory.Exists(full))
                    {
                        Log("忽略不存在的文件夹：" + f);
                        continue;
                    }
                    if (IsInBackupRoot(full))
                    {
                        Log("忽略备份目录自身或其内部的文件夹：" + full);
                        continue;
                    }
                    valid.Add(full);
                }
            }
            valid.Sort(StringComparer.OrdinalIgnoreCase);

            List<string> roots = new List<string>();
            foreach (string full in valid)
            {
                bool covered = false;
                for (int i = 0; i < roots.Count; i++)
                {
                    if (IsInside(roots[i], full)) { covered = true; break; }
                }
                if (!covered) roots.Add(full);
            }
            return roots;
        }

        private static bool IsInside(string parent, string child)
        {
            if (string.Equals(parent, child, StringComparison.OrdinalIgnoreCase)) return true;
            return child.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>该路径是否就是备份目录或位于备份目录内部（备份目录不能备份进自己）</summary>
        private bool IsInBackupRoot(string path)
        {
            if (_backupRoot.Length == 0) return false;
            return IsInside(_backupRoot, path);
        }

        // ---------------------------------------------------------------- 统计文件数量

        private long CountFiles(List<string> roots)
        {
            long total = 0;
            for (int i = 0; i < roots.Count; i++) total += CountFilesIn(roots[i]);
            return total;
        }

        private long CountFilesIn(string dir)
        {
            long n = GetFiles(dir).Length;
            foreach (string d in GetDirectories(dir))
            {
                if (_cancel) return n;
                if (IsInBackupRoot(d)) continue;
                n += CountFilesIn(d);
            }
            return n;
        }

        // ---------------------------------------------------------------- 目录/文件枚举

        private string[] GetFiles(string dir)
        {
            try
            {
                return Directory.GetFiles(dir);
            }
            catch (Exception ex)
            {
                _result.Errors++;
                Log("无法读取文件夹（已跳过）：" + dir + "  ->  " + ex.Message);
                return new string[0];
            }
        }

        private string[] GetDirectories(string dir)
        {
            string[] dirs;
            try
            {
                dirs = Directory.GetDirectories(dir);
            }
            catch (Exception ex)
            {
                _result.Errors++;
                Log("无法读取子文件夹（已跳过）：" + dir + "  ->  " + ex.Message);
                return new string[0];
            }

            if (!_opt.SkipReparsePoints) return dirs;

            List<string> list = new List<string>(dirs.Length);
            for (int i = 0; i < dirs.Length; i++)
            {
                if (IsReparsePoint(dirs[i]))
                {
                    Log("跳过符号链接/联接点：" + dirs[i]);
                    continue;
                }
                list.Add(dirs[i]);
            }
            return list.ToArray();
        }

        private static bool IsReparsePoint(string path)
        {
            try
            {
                return (File.GetAttributes(path) & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint;
            }
            catch
            {
                return true;   // 读不到属性就当作不可用，直接跳过
            }
        }

        // ---------------------------------------------------------------- 拷贝（合并）

        private void CopyDirectory(string sourceDir, string targetDir, ref long done)
        {
            if (_cancel) return;

            if (!Directory.Exists(targetDir))
            {
                try
                {
                    Directory.CreateDirectory(targetDir);
                    _result.FoldersCreated++;
                    Log("新建文件夹：" + targetDir);
                }
                catch (Exception ex)
                {
                    _result.Errors++;
                    Log("无法创建文件夹：" + targetDir + "  ->  " + ex.Message);
                    return;
                }
            }

            string[] files = GetFiles(sourceDir);
            for (int i = 0; i < files.Length; i++)
            {
                if (_cancel) return;
                CopyOneFile(files[i], Path.Combine(targetDir, Path.GetFileName(files[i])));
                done++;
                Report(done, _result.TotalFiles);
            }

            foreach (string sub in GetDirectories(sourceDir))
            {
                if (_cancel) return;
                if (IsInBackupRoot(sub))
                {
                    Log("跳过备份目录本身：" + sub);
                    continue;
                }
                CopyDirectory(sub, Path.Combine(targetDir, Path.GetFileName(sub)), ref done);
            }
        }

        /// <summary>复制单个文件：目标同名时按选项覆盖或自动重命名</summary>
        private void CopyOneFile(string sourceFile, string targetFile)
        {
            try
            {
                string dir = Path.GetDirectoryName(targetFile);
                if (dir != null && dir.Length > 0 && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                    _result.FoldersCreated++;
                    Log("新建文件夹：" + dir);
                }

                bool exists = File.Exists(targetFile);

                if (exists && _opt.SkipIdenticalFiles && SameContent(sourceFile, targetFile))
                {
                    _result.FilesSkipped++;
                    Log("跳过（内容完全相同的同名文件）：" + targetFile);
                    return;
                }

                string final = targetFile;
                if (exists && !_opt.OverwriteExisting)
                {
                    final = MakeUniqueName(targetFile);
                    File.Copy(sourceFile, final, false);
                    _result.FilesCopied++;
                    _result.FilesRenamed++;
                    Log("同名，已自动重命名：" + Path.GetFileName(targetFile) + "   ->   " + Path.GetFileName(final));
                }
                else
                {
                    if (exists) Log("覆盖同名文件：" + targetFile);
                    File.Copy(sourceFile, final, exists);
                    _result.FilesCopied++;
                }

                try { File.SetLastWriteTime(final, File.GetLastWriteTime(sourceFile)); }
                catch { /* 时间戳设置失败不影响备份结果 */ }
            }
            catch (Exception ex)
            {
                _result.Errors++;
                Log("复制失败：" + sourceFile + "   ->   " + targetFile + "  ：" + ex.Message);
            }
        }

        /// <summary>生成不冲突的文件名：xxx (1).ext，xxx (2).ext ...</summary>
        private static string MakeUniqueName(string path)
        {
            string dir = Path.GetDirectoryName(path);
            string name = Path.GetFileNameWithoutExtension(path);
            string ext = Path.GetExtension(path);

            for (int i = 1; i < 1000000; i++)
            {
                string candidate = Path.Combine(dir, name + " (" + i.ToString() + ")" + ext);
                if (!File.Exists(candidate)) return candidate;
            }
            return Path.Combine(dir, name + " (" + Guid.NewGuid().ToString("N") + ")" + ext);
        }

        /// <summary>逐字节比较两个文件内容是否完全相同</summary>
        private static bool SameContent(string a, string b)
        {
            try
            {
                FileInfo fa = new FileInfo(a);
                FileInfo fb = new FileInfo(b);
                if (!fa.Exists || !fb.Exists) return false;
                if (fa.Length != fb.Length) return false;

                using (FileStream sa = File.OpenRead(a))
                using (FileStream sb = File.OpenRead(b))
                {
                    byte[] ba = new byte[65536];
                    byte[] bb = new byte[65536];
                    while (true)
                    {
                        int na = ReadFull(sa, ba);
                        int nb = ReadFull(sb, bb);
                        if (na != nb) return false;
                        if (na == 0) return true;
                        for (int i = 0; i < na; i++)
                        {
                            if (ba[i] != bb[i]) return false;
                        }
                    }
                }
            }
            catch
            {
                return false;
            }
        }

        private static int ReadFull(Stream stream, byte[] buffer)
        {
            int total = 0;
            while (total < buffer.Length)
            {
                int read = stream.Read(buffer, total, buffer.Length - total);
                if (read <= 0) break;
                total += read;
            }
            return total;
        }

        // ---------------------------------------------------------------- 回调

        private void Report(long done, long total)
        {
            if (_progress != null) _progress(done, total);
        }

        private void Log(string message)
        {
            if (_log != null) _log(message);
        }
    }
}
