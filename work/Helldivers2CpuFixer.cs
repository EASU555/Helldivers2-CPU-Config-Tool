using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Helldivers 2 CPU Config Tool")]
[assembly: AssemblyDescription("A local settings.ini editor for Helldivers 2")]
[assembly: AssemblyCompany("Local User Tool")]
[assembly: AssemblyProduct("Helldivers 2 CPU Config Tool")]
[assembly: AssemblyCopyright("Local User")]
[assembly: AssemblyVersion("1.6.9.0")]
[assembly: AssemblyFileVersion("1.6.9.0")]

namespace Helldivers2CpuFixer
{
    internal static class AppConstants
    {
        internal const string AppId = "553850";
        internal const string SteamLaunchUri = "steam://rungameid/553850";
        internal const string Version = "1.6.9";
        internal const string ThreadsPattern = @"(?im)^\s*num[\s_]+reserved[\s_]+threads\s*=\s*(\d+)";
        internal const string ThreadsReplacePattern = @"(?im)^(\s*num[\s_]+reserved[\s_]+threads\s*=\s*)\d+([^\r\n]*)";
        internal const string MemoryPattern = @"(?im)^\s*memory_size\s*=\s*(\d+)";
        internal const string MemoryReplacePattern = @"(?im)^(\s*memory_size\s*=\s*)\d+([^\r\n]*)";
        internal const string RefillsPattern = @"(?im)^\s*num_refills_in_voice\s*=\s*(\d+)";
        internal const string RefillsReplacePattern = @"(?im)^(\s*num_refills_in_voice\s*=\s*)\d+([^\r\n]*)";
    }

    internal static class GameLauncher
    {
        internal static ProcessStartInfo CreateStartInfo()
        {
            return new ProcessStartInfo(AppConstants.SteamLaunchUri) { UseShellExecute = true };
        }

        internal static void Launch(Action<ProcessStartInfo> launcher)
        {
            if (launcher == null) throw new ArgumentNullException("launcher");
            launcher(CreateStartInfo());
        }
    }

    internal sealed class ConfigSnapshot
    {
        internal int? ReservedThreads;
        internal int? MemorySize;
        internal int? Refills;

        internal bool HasAnyValue
        {
            get { return ReservedThreads.HasValue || MemorySize.HasValue || Refills.HasValue; }
        }

        internal bool HasAllValues
        {
            get { return ReservedThreads.HasValue && MemorySize.HasValue && Refills.HasValue; }
        }

        internal string ToDisplayText()
        {
            return "num_reserved_threads=" + ValueOrMissing(ReservedThreads) +
                   ", memory_size=" + ValueOrMissing(MemorySize) +
                   ", num_refills_in_voice=" + ValueOrMissing(Refills);
        }

        private static string ValueOrMissing(int? value)
        {
            return value.HasValue ? value.Value.ToString() : "未找到";
        }
    }

    internal sealed class ConfigTarget
    {
        internal bool ModifyCpu;
        internal bool ModifyAudio;
        internal int ReservedThreads;
        internal int MemorySize;
        internal int Refills;
    }

    internal sealed class ApplyResult
    {
        internal bool Changed;
        internal string BackupPath;
        internal string MaintenanceWarning;
    }

    internal sealed class RestoreResult
    {
        internal string PreRestoreBackupPath;
        internal bool LegacyBackupWithoutIntegrityMetadata;
        internal string MaintenanceWarning;
    }

    internal static class ConfigFileOperations
    {
        private sealed class FileFingerprint
        {
            internal long Length;
            internal string Sha256;
        }

        internal static string ReadText(string path)
        {
            return File.ReadAllText(path, Encoding.UTF8);
        }

        internal static ConfigSnapshot ReadSnapshot(string path)
        {
            return ParseSnapshot(ReadText(path));
        }

        internal static ConfigSnapshot ParseSnapshot(string text)
        {
            return new ConfigSnapshot
            {
                ReservedThreads = ReadInt(text, AppConstants.ThreadsPattern),
                MemorySize = ReadInt(text, AppConstants.MemoryPattern),
                Refills = ReadInt(text, AppConstants.RefillsPattern)
            };
        }

        internal static string BuildModifiedText(string oldText, ConfigTarget target)
        {
            var text = oldText;
            if (target.ModifyCpu)
            {
                text = ReplaceInt(text, AppConstants.ThreadsReplacePattern, target.ReservedThreads);
            }
            if (target.ModifyAudio)
            {
                text = ReplaceInt(text, AppConstants.MemoryReplacePattern, target.MemorySize);
                text = ReplaceInt(text, AppConstants.RefillsReplacePattern, target.Refills);
            }
            return text;
        }

        internal static string FindMissingRequiredKeys(string text, ConfigTarget target)
        {
            var missing = new List<string>();
            if (target.ModifyCpu && !Regex.IsMatch(text, AppConstants.ThreadsPattern))
            {
                missing.Add("num_reserved_threads");
            }
            if (target.ModifyAudio && !Regex.IsMatch(text, AppConstants.MemoryPattern))
            {
                missing.Add("memory_size");
            }
            if (target.ModifyAudio && !Regex.IsMatch(text, AppConstants.RefillsPattern))
            {
                missing.Add("num_refills_in_voice");
            }
            return string.Join("、", missing.ToArray());
        }

        internal static void VerifyTarget(string path, ConfigTarget target)
        {
            var current = ReadSnapshot(path);
            if (target.ModifyCpu && current.ReservedThreads != target.ReservedThreads)
            {
                throw new InvalidOperationException("num_reserved_threads 未变成目标值。");
            }
            if (target.ModifyAudio && current.MemorySize != target.MemorySize)
            {
                throw new InvalidOperationException("memory_size 未变成目标值。");
            }
            if (target.ModifyAudio && current.Refills != target.Refills)
            {
                throw new InvalidOperationException("num_refills_in_voice 未变成目标值。");
            }
        }

        internal static ApplyResult ApplySafely(
            string path,
            string expectedOriginalText,
            string newText,
            ConfigTarget target,
            bool lockAfterSuccess,
            Action<string> testCheckpoint)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                throw new FileNotFoundException("配置文件不存在。", path);
            }
            if (expectedOriginalText == null) throw new ArgumentNullException("expectedOriginalText");
            if (newText == null) throw new ArgumentNullException("newText");

            VerifyFileMatchesText(path, expectedOriginalText, "配置文件已被其他程序修改，请刷新后重新确认。");
            if (newText == expectedOriginalText)
            {
                VerifyTarget(path, target);
                if (lockAfterSuccess && !IsReadOnly(path))
                {
                    try
                    {
                        SetReadOnly(path, true);
                    }
                    catch (Exception ex)
                    {
                        throw BuildStepException(
                            "将配置文件设为只读",
                            ex,
                            "配置内容没有变化，无需回滚",
                            null,
                            null);
                    }
                }
                return new ApplyResult { Changed = false };
            }

            var originalAttributes = File.GetAttributes(path);
            var backupPath = MakeBackupPath(path, null);
            var tempPath = MakeTempPath(path, "write");
            var step = "准备";
            var rollbackResult = "原文件尚未替换，无需回滚";
            var replaced = false;
            var backupCreated = false;
            FileFingerprint replacementFingerprint = null;
            Exception operationError = null;
            Exception attributeError = null;
            Exception cleanupError = null;

            try
            {
                step = "创建修改前备份";
                CreateVerifiedBackup(path, backupPath);
                backupCreated = true;
                VerifyFileMatchesText(
                    backupPath,
                    expectedOriginalText,
                    "配置文件在确认后发生变化，已停止修改。");
                Checkpoint(testCheckpoint, "after_backup");

                step = "写入同目录临时文件";
                WriteTextDurable(tempPath, newText);

                step = "校验临时文件参数";
                VerifyTarget(tempPath, target);
                replacementFingerprint = GetFingerprint(tempPath);
                Checkpoint(testCheckpoint, "after_temp_verify");

                step = "确认配置文件未被其他程序修改";
                VerifyFilesIdentical(
                    backupPath,
                    path,
                    "配置文件在确认后发生变化，已停止修改。");
                Checkpoint(testCheckpoint, "before_replace");

                step = "临时解除原文件只读属性";
                ClearReadOnly(path);

                step = "安全替换原配置文件";
                File.Replace(tempPath, path, null, true);
                replaced = true;
                Checkpoint(testCheckpoint, "after_replace");

                step = "再次校验原配置文件";
                VerifyFingerprint(
                    path,
                    replacementFingerprint,
                    "替换后的配置文件与已校验临时文件不完全一致。");
                VerifyTarget(path, target);
                Checkpoint(testCheckpoint, "after_final_verify");
            }
            catch (Exception ex)
            {
                operationError = ex;
                if (replaced && File.Exists(backupPath))
                {
                    try
                    {
                        RestoreBackupCore(backupPath, path, originalAttributes);
                        rollbackResult = "已自动恢复修改前备份";
                    }
                    catch (Exception rollbackError)
                    {
                        rollbackResult = "自动回滚失败：" + rollbackError.Message;
                    }
                }
                else if (backupCreated)
                {
                    try
                    {
                        DeleteBackupBundle(backupPath);
                        rollbackResult = "原文件尚未替换，已删除冗余备份";
                    }
                    catch (Exception cleanupBackupError)
                    {
                        cleanupError = cleanupBackupError;
                        rollbackResult = "原文件尚未替换，但冗余备份清理失败";
                    }
                }
            }
            finally
            {
                try
                {
                    DeleteTemporaryFile(tempPath);
                }
                catch (Exception ex)
                {
                    if (cleanupError == null) cleanupError = ex;
                }

                try
                {
                    if (File.Exists(path))
                    {
                        File.SetAttributes(path, originalAttributes);
                    }
                }
                catch (Exception ex)
                {
                    attributeError = ex;
                }
            }

            if (operationError != null)
            {
                try
                {
                    PruneBackups(path, 10);
                }
                catch (Exception pruneError)
                {
                    if (cleanupError == null) cleanupError = pruneError;
                }
                throw BuildStepException(step, operationError, rollbackResult, cleanupError, attributeError);
            }

            if (attributeError != null || cleanupError != null)
            {
                var postStep = attributeError != null ? "恢复配置文件原始属性" : "清理临时文件";
                var postError = attributeError != null ? attributeError : cleanupError;
                var postRollback = TryRollbackAfterPostWriteFailure(backupPath, path, originalAttributes);
                throw BuildStepException(postStep, postError, postRollback, null, null);
            }

            if (lockAfterSuccess)
            {
                try
                {
                    File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
                }
                catch (Exception ex)
                {
                    var lockRollback = TryRollbackAfterPostWriteFailure(backupPath, path, originalAttributes);
                    throw BuildStepException("将配置文件设为只读", ex, lockRollback, null, null);
                }
            }

            string maintenanceWarning = null;
            try
            {
                PruneBackups(path, 10);
            }
            catch (Exception ex)
            {
                maintenanceWarning = "修改成功，但清理旧备份失败：" + ex.Message;
            }

            return new ApplyResult
            {
                Changed = true,
                BackupPath = backupPath,
                MaintenanceWarning = maintenanceWarning
            };
        }

        internal static RestoreResult RestoreBackupSafely(string backupPath, string targetPath, Action<string> testCheckpoint)
        {
            if (string.IsNullOrEmpty(backupPath) || !File.Exists(backupPath))
            {
                throw new FileNotFoundException("备份文件不存在。", backupPath);
            }
            if (string.IsNullOrEmpty(targetPath) || !File.Exists(targetPath))
            {
                throw new FileNotFoundException("目标配置文件不存在。", targetPath);
            }

            var originalAttributes = File.GetAttributes(targetPath);
            var backupSnapshot = ReadSnapshot(backupPath);
            if (!backupSnapshot.HasAllValues)
            {
                throw new InvalidDataException("备份文件缺少一个或多个必要参数，可能已损坏或截断，已停止恢复。");
            }
            var legacyBackup = !ValidateBackupIntegrityMetadata(backupPath);
            var backupFingerprint = GetFingerprint(backupPath);

            var guardPath = MakeBackupPath(targetPath, "pre_restore");
            var replacementPath = MakeTempPath(targetPath, "restore");
            var step = "准备恢复";
            var replaced = false;
            var guardCreated = false;
            var rollbackSucceeded = false;
            Exception operationError = null;
            Exception attributeError = null;
            Exception cleanupError = null;
            var rollbackResult = "目标文件尚未替换，无需回滚";

            try
            {
                step = "创建恢复前永久备份";
                CreateVerifiedBackup(targetPath, guardPath);
                guardCreated = true;

                step = "复制备份到同目录临时文件";
                File.Copy(backupPath, replacementPath, false);
                NormalizeTemporaryFileAttributes(replacementPath);

                step = "校验恢复临时文件";
                VerifyFingerprint(
                    replacementPath,
                    backupFingerprint,
                    "恢复临时文件与所选备份不完全一致。");
                VerifySameCriticalValues(backupSnapshot, ReadSnapshot(replacementPath));
                Checkpoint(testCheckpoint, "before_restore_replace");

                step = "确认目标配置未被其他程序修改";
                VerifyFilesIdentical(
                    guardPath,
                    targetPath,
                    "目标配置在恢复确认后发生变化，已停止恢复。");

                step = "临时解除目标文件只读属性";
                ClearReadOnly(targetPath);

                step = "安全替换目标配置文件";
                File.Replace(replacementPath, targetPath, null, true);
                replaced = true;
                Checkpoint(testCheckpoint, "after_restore_replace");

                step = "校验恢复结果";
                VerifyFingerprint(
                    targetPath,
                    backupFingerprint,
                    "恢复后的配置文件与所选备份不完全一致。");
                VerifySameCriticalValues(backupSnapshot, ReadSnapshot(targetPath));
            }
            catch (Exception ex)
            {
                operationError = ex;
                if (replaced && File.Exists(guardPath))
                {
                    try
                    {
                        RestoreBackupCore(guardPath, targetPath, originalAttributes);
                        rollbackSucceeded = true;
                        rollbackResult = "已恢复到执行恢复前的配置";
                    }
                    catch (Exception rollbackError)
                    {
                        rollbackResult = "恢复保护副本失败：" + rollbackError.Message;
                    }
                }
                if (guardCreated && (!replaced || rollbackSucceeded))
                {
                    try
                    {
                        DeleteBackupBundle(guardPath);
                        guardCreated = false;
                    }
                    catch (Exception cleanupGuardError)
                    {
                        cleanupError = cleanupGuardError;
                    }
                }
            }
            finally
            {
                try { DeleteTemporaryFile(replacementPath); }
                catch (Exception ex) { if (cleanupError == null) cleanupError = ex; }

                try
                {
                    if (File.Exists(targetPath))
                    {
                        File.SetAttributes(targetPath, originalAttributes);
                    }
                }
                catch (Exception ex)
                {
                    attributeError = ex;
                }
            }

            if (operationError != null)
            {
                try
                {
                    PruneBackups(targetPath, 10);
                }
                catch (Exception pruneError)
                {
                    if (cleanupError == null) cleanupError = pruneError;
                }
                throw BuildStepException(step, operationError, rollbackResult, cleanupError, attributeError);
            }
            if (attributeError != null || cleanupError != null)
            {
                var postStep = attributeError != null ? "恢复目标文件原始属性" : "清理恢复临时文件";
                var postError = attributeError != null ? attributeError : cleanupError;
                throw BuildStepException(postStep, postError, "恢复内容已完成，请检查文件状态", null, null);
            }

            string maintenanceWarning = null;
            try
            {
                PruneBackups(targetPath, 10);
            }
            catch (Exception ex)
            {
                maintenanceWarning = "恢复成功，但清理旧备份失败：" + ex.Message;
            }
            return new RestoreResult
            {
                PreRestoreBackupPath = guardPath,
                LegacyBackupWithoutIntegrityMetadata = legacyBackup,
                MaintenanceWarning = maintenanceWarning
            };
        }

        internal static void PruneBackups(string settingsPath, int keepCount)
        {
            var backups = GetBackups(settingsPath);
            var firstRemoval = Math.Max(0, keepCount);
            for (var i = firstRemoval; i < backups.Length; i++)
            {
                DeleteBackupBundle(backups[i]);
            }
        }

        internal static string[] GetBackups(string settingsPath)
        {
            var directory = Path.GetDirectoryName(settingsPath);
            var fileName = Path.GetFileName(settingsPath);
            var candidates = Directory.GetFiles(directory, fileName + ".bak_*");
            var backupList = new List<string>();
            foreach (var candidate in candidates)
            {
                if (!candidate.EndsWith(".integrity", StringComparison.OrdinalIgnoreCase))
                {
                    backupList.Add(candidate);
                }
            }
            var backups = backupList.ToArray();
            Array.Sort(backups, StringComparer.OrdinalIgnoreCase);
            Array.Reverse(backups);
            return backups;
        }

        internal static void SetReadOnly(string path, bool readOnly)
        {
            var attributes = File.GetAttributes(path);
            var updated = readOnly
                ? attributes | FileAttributes.ReadOnly
                : attributes & ~FileAttributes.ReadOnly;
            File.SetAttributes(path, updated);
        }

        internal static bool IsReadOnly(string path)
        {
            return (File.GetAttributes(path) & FileAttributes.ReadOnly) == FileAttributes.ReadOnly;
        }

        private static void RestoreBackupCore(string backupPath, string targetPath, FileAttributes desiredAttributes)
        {
            var expected = ReadSnapshot(backupPath);
            var backupFingerprint = GetFingerprint(backupPath);

            var tempPath = MakeTempPath(targetPath, "rollback");
            try
            {
                File.Copy(backupPath, tempPath, false);
                NormalizeTemporaryFileAttributes(tempPath);
                VerifyFingerprint(tempPath, backupFingerprint, "回滚临时文件与备份不完全一致。");
                if (expected.HasAnyValue) VerifySameCriticalValues(expected, ReadSnapshot(tempPath));
                if (File.Exists(targetPath))
                {
                    ClearReadOnly(targetPath);
                    File.Replace(tempPath, targetPath, null, true);
                }
                else
                {
                    File.Move(tempPath, targetPath);
                }
                VerifyFingerprint(targetPath, backupFingerprint, "回滚后的配置文件与备份不完全一致。");
                if (expected.HasAnyValue) VerifySameCriticalValues(expected, ReadSnapshot(targetPath));
            }
            finally
            {
                try
                {
                    DeleteTemporaryFile(tempPath);
                }
                finally
                {
                    if (File.Exists(targetPath))
                    {
                        File.SetAttributes(targetPath, desiredAttributes);
                    }
                }
            }
        }

        private static string TryRollbackAfterPostWriteFailure(string backupPath, string path, FileAttributes originalAttributes)
        {
            if (!File.Exists(backupPath))
            {
                return "未找到修改前备份，无法自动回滚";
            }
            try
            {
                RestoreBackupCore(backupPath, path, originalAttributes);
                return "已自动恢复修改前备份";
            }
            catch (Exception rollbackError)
            {
                return "自动回滚失败：" + rollbackError.Message;
            }
        }

        private static Exception BuildStepException(
            string step,
            Exception error,
            string rollbackResult,
            Exception cleanupError,
            Exception attributeError)
        {
            var message = new StringBuilder();
            message.AppendLine("失败步骤：" + step);
            message.AppendLine("异常信息：" + error.Message);
            message.AppendLine("文件保护：" + rollbackResult);
            if (cleanupError != null)
            {
                message.AppendLine("临时文件清理：" + cleanupError.Message);
            }
            if (attributeError != null)
            {
                message.AppendLine("属性恢复：" + attributeError.Message);
            }
            return new InvalidOperationException(message.ToString().TrimEnd(), error);
        }

        private static void WriteTextDurable(string path, string text)
        {
            var bytes = new UTF8Encoding(false).GetBytes(text);
            using (var stream = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
        }

        private static string MakeBackupPath(string settingsPath, string purpose)
        {
            var token = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + "_" +
                        Guid.NewGuid().ToString("N").Substring(0, 8);
            if (!string.IsNullOrEmpty(purpose)) token += "_" + purpose;
            return settingsPath + ".bak_" + token;
        }

        private static string GetIntegrityMetadataPath(string backupPath)
        {
            return backupPath + ".integrity";
        }

        private static void CreateVerifiedBackup(string sourcePath, string backupPath)
        {
            try
            {
                var sourceFingerprint = GetFingerprint(sourcePath);
                File.Copy(sourcePath, backupPath, false);
                NormalizeTemporaryFileAttributes(backupPath);
                VerifyFingerprint(backupPath, sourceFingerprint, "备份文件与原配置不完全一致。");
                WriteIntegrityMetadata(backupPath, sourceFingerprint);
            }
            catch
            {
                try { DeleteBackupBundle(backupPath); }
                catch { }
                throw;
            }
        }

        private static void WriteIntegrityMetadata(string backupPath, FileFingerprint fingerprint)
        {
            var metadataPath = GetIntegrityMetadataPath(backupPath);
            var text = "Version=1\r\n" +
                       "Length=" + fingerprint.Length + "\r\n" +
                       "SHA256=" + fingerprint.Sha256 + "\r\n";
            WriteTextDurable(metadataPath, text);
            NormalizeTemporaryFileAttributes(metadataPath);
        }

        private static bool ValidateBackupIntegrityMetadata(string backupPath)
        {
            var metadataPath = GetIntegrityMetadataPath(backupPath);
            if (!File.Exists(metadataPath)) return false;

            long expectedLength = -1;
            string expectedHash = null;
            foreach (var rawLine in File.ReadAllLines(metadataPath, Encoding.UTF8))
            {
                var line = (rawLine ?? "").Trim();
                var separator = line.IndexOf('=');
                if (separator <= 0) continue;
                var key = line.Substring(0, separator).Trim();
                var value = line.Substring(separator + 1).Trim();
                if (key.Equals("Length", StringComparison.OrdinalIgnoreCase))
                {
                    long parsed;
                    if (long.TryParse(value, out parsed)) expectedLength = parsed;
                }
                else if (key.Equals("SHA256", StringComparison.OrdinalIgnoreCase))
                {
                    expectedHash = value;
                }
            }
            if (expectedLength < 0 || string.IsNullOrEmpty(expectedHash))
            {
                throw new InvalidDataException("备份完整性信息损坏，已停止恢复。");
            }
            VerifyFingerprint(
                backupPath,
                new FileFingerprint { Length = expectedLength, Sha256 = expectedHash },
                "备份完整性校验失败，文件可能已损坏或被修改。");
            return true;
        }

        private static FileFingerprint GetFingerprint(string path)
        {
            using (var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.SequentialScan))
            using (var sha256 = SHA256.Create())
            {
                var length = stream.Length;
                var hash = sha256.ComputeHash(stream);
                return new FileFingerprint
                {
                    Length = length,
                    Sha256 = BitConverter.ToString(hash).Replace("-", "")
                };
            }
        }

        private static void VerifyFingerprint(string path, FileFingerprint expected, string errorMessage)
        {
            var actual = GetFingerprint(path);
            if (actual.Length != expected.Length ||
                !string.Equals(actual.Sha256, expected.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(errorMessage);
            }
        }

        private static void VerifyFilesIdentical(string expectedPath, string actualPath, string errorMessage)
        {
            VerifyFingerprint(actualPath, GetFingerprint(expectedPath), errorMessage);
        }

        private static void VerifyFileMatchesText(string path, string expectedText, string errorMessage)
        {
            if (!string.Equals(ReadText(path), expectedText, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(errorMessage);
            }
        }

        private static void DeleteBackupBundle(string backupPath)
        {
            if (string.IsNullOrEmpty(backupPath)) return;
            if (File.Exists(backupPath))
            {
                ClearReadOnly(backupPath);
                File.Delete(backupPath);
            }
            var metadataPath = GetIntegrityMetadataPath(backupPath);
            if (File.Exists(metadataPath))
            {
                ClearReadOnly(metadataPath);
                File.Delete(metadataPath);
            }
        }

        private static void VerifySameCriticalValues(ConfigSnapshot expected, ConfigSnapshot actual)
        {
            if (expected.ReservedThreads.HasValue && expected.ReservedThreads != actual.ReservedThreads)
            {
                throw new InvalidDataException("num_reserved_threads 校验不一致。");
            }
            if (expected.MemorySize.HasValue && expected.MemorySize != actual.MemorySize)
            {
                throw new InvalidDataException("memory_size 校验不一致。");
            }
            if (expected.Refills.HasValue && expected.Refills != actual.Refills)
            {
                throw new InvalidDataException("num_refills_in_voice 校验不一致。");
            }
        }

        private static void Checkpoint(Action<string> checkpoint, string name)
        {
            if (checkpoint != null)
            {
                checkpoint(name);
            }
        }

        private static string MakeTempPath(string targetPath, string purpose)
        {
            return Path.Combine(
                Path.GetDirectoryName(targetPath),
                "." + Path.GetFileName(targetPath) + "." + purpose + "_" + Guid.NewGuid().ToString("N") + ".tmp");
        }

        private static void NormalizeTemporaryFileAttributes(string path)
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }

        private static void ClearReadOnly(string path)
        {
            if (!File.Exists(path)) return;
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
            {
                File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            }
        }

        private static void DeleteTemporaryFile(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            ClearReadOnly(path);
            File.Delete(path);
        }

        private static int? ReadInt(string text, string pattern)
        {
            var match = Regex.Match(text, pattern);
            if (!match.Success) return null;
            int value;
            return int.TryParse(match.Groups[1].Value, out value) ? (int?)value : null;
        }

        private static string ReplaceInt(string text, string pattern, int value)
        {
            return Regex.Replace(text, pattern, "${1}" + value + "${2}");
        }
    }

    internal sealed class UserSettings
    {
        internal string GameDirectory = "";
        internal int PlanIndex;
        internal int ReservedThreads = 16;
        internal int MemoryMb = 75;
        internal int Refills = 4;
        internal bool ModifyCpu = true;
        internal bool ModifyAudio = true;
        internal bool LockAfterApply;
    }

    internal static class UserSettingsStore
    {
        internal static string SettingsPath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Helldivers2CpuFixer",
                    "user-settings.ini");
            }
        }

        internal static UserSettings Load()
        {
            return LoadFromPath(SettingsPath);
        }

        internal static UserSettings LoadFromPath(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return new UserSettings();
                return Parse(File.ReadAllLines(path, Encoding.UTF8));
            }
            catch
            {
                return new UserSettings();
            }
        }

        internal static UserSettings Parse(string[] lines)
        {
            var result = new UserSettings();
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rawLine in lines ?? new string[0])
            {
                var line = (rawLine ?? "").Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                var separator = line.IndexOf('=');
                if (separator <= 0) continue;
                values[line.Substring(0, separator).Trim()] = line.Substring(separator + 1);
            }

            string value;
            if (values.TryGetValue("GameDirectory", out value)) result.GameDirectory = value.Trim();
            result.PlanIndex = ReadBoundedInt(values, "PlanIndex", 0, 6, result.PlanIndex);
            result.ReservedThreads = ReadBoundedInt(values, "ReservedThreads", 0, 4096, result.ReservedThreads);
            result.MemoryMb = ReadBoundedInt(values, "MemoryMb", 1, 512, result.MemoryMb);
            result.Refills = ReadBoundedInt(values, "Refills", 1, 16, result.Refills);
            result.ModifyCpu = ReadBool(values, "ModifyCpu", result.ModifyCpu);
            result.ModifyAudio = ReadBool(values, "ModifyAudio", result.ModifyAudio);
            result.LockAfterApply = ReadBool(values, "LockAfterApply", result.LockAfterApply);
            return result;
        }

        internal static void Save(UserSettings settings)
        {
            SaveToPath(settings, SettingsPath);
        }

        internal static void SaveToPath(UserSettings settings, string path)
        {
            if (settings == null) throw new ArgumentNullException("settings");
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("设置文件路径不能为空。", "path");
            var directory = Path.GetDirectoryName(path);
            Directory.CreateDirectory(directory);
            var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var text = new StringBuilder();
            text.AppendLine("Version=1");
            text.AppendLine("GameDirectory=" + (settings.GameDirectory ?? ""));
            text.AppendLine("PlanIndex=" + settings.PlanIndex);
            text.AppendLine("ReservedThreads=" + settings.ReservedThreads);
            text.AppendLine("MemoryMb=" + settings.MemoryMb);
            text.AppendLine("Refills=" + settings.Refills);
            text.AppendLine("ModifyCpu=" + settings.ModifyCpu);
            text.AppendLine("ModifyAudio=" + settings.ModifyAudio);
            text.AppendLine("LockAfterApply=" + settings.LockAfterApply);
            try
            {
                File.WriteAllText(tempPath, text.ToString(), new UTF8Encoding(false));
                if (File.Exists(path))
                {
                    File.Replace(tempPath, path, null, true);
                }
                else
                {
                    File.Move(tempPath, path);
                }
            }
            finally
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
        }

        private static int ReadBoundedInt(
            Dictionary<string, string> values,
            string key,
            int minimum,
            int maximum,
            int fallback)
        {
            string raw;
            int parsed;
            if (values.TryGetValue(key, out raw) && int.TryParse(raw, out parsed) && parsed >= minimum && parsed <= maximum)
            {
                return parsed;
            }
            return fallback;
        }

        private static bool ReadBool(Dictionary<string, string> values, string key, bool fallback)
        {
            string raw;
            bool parsed;
            if (values.TryGetValue(key, out raw) && bool.TryParse(raw, out parsed)) return parsed;
            return fallback;
        }
    }

    internal enum StatusLevel
    {
        Success,
        Warning,
        Error
    }

    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            bool createdNew;
            using (var instanceMutex = new Mutex(
                true,
                @"Local\Helldivers2CpuFixer_553850_v1",
                out createdNew))
            {
                if (!createdNew)
                {
                    MessageBox.Show(
                        "程序已经在运行，请切换到已有窗口。",
                        "地狱潜兵 CPU 修改器",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                    return;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += OnThreadException;
                Application.Run(new MainForm());
                GC.KeepAlive(instanceMutex);
            }
        }

        private static void OnThreadException(object sender, ThreadExceptionEventArgs e)
        {
            MessageBox.Show(
                "程序遇到未处理错误，但已阻止界面直接退出。\r\n\r\n" + e.Exception.Message,
                "地狱潜兵 CPU 修改器",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    internal sealed class MainForm : Form
    {
        internal const int LayoutPlanLabelWidth = 185;
        internal const int LayoutBannerAreaHeight = 138;
        internal const int LayoutPathAreaHeight = 116;
        internal const int LayoutMainAreaHeight = 410;
        internal const int LayoutPlanSummaryHeight = 60;
        internal const int LayoutPlanWarningHeight = 42;
        internal const int LayoutCommandAreaHeight = 128;
        internal const int LayoutCommandButtonsHeight = 80;
        internal const int LayoutCommandColumns = 3;
        internal const int LayoutCommandRows = 2;

        private readonly Color pageColor = Color.FromArgb(243, 246, 250);
        private readonly Color cardColor = Color.White;
        private readonly Color textColor = Color.FromArgb(30, 38, 52);
        private readonly Color mutedColor = Color.FromArgb(92, 104, 122);
        private readonly Color accentColor = Color.FromArgb(35, 102, 218);
        private readonly Color okColor = Color.FromArgb(31, 137, 89);
        private readonly Color cautionColor = Color.FromArgb(188, 124, 25);
        private readonly Color errorColor = Color.FromArgb(196, 61, 54);

        private readonly TextBox gameDirBox = new TextBox();
        private readonly ComboBox planBox = new ComboBox();
        private readonly NumericUpDown reservedThreadsBox = new NumericUpDown();
        private readonly NumericUpDown memorySizeBox = new NumericUpDown();
        private readonly NumericUpDown refillsBox = new NumericUpDown();
        private readonly CheckBox cpuCheck = new CheckBox();
        private readonly CheckBox audioCheck = new CheckBox();
        private readonly CheckBox lockAfterApplyCheck = new CheckBox();
        private readonly Label currentLabel = new Label();
        private readonly Label currentPathLabel = new Label();
        private readonly Label adviceLabel = new Label();
        private readonly Label threadSummaryLabel = new Label();
        private readonly Label safetyWarningLabel = new Label();
        private readonly Label fileStateLabel = new Label();
        private readonly Label statusLabel = new Label();
        private readonly Label statusDot = new Label();
        private readonly TextBox logBox = new TextBox();
        private readonly ToolTip tips = new ToolTip();
        private readonly List<string> logLines = new List<string>();

        private bool applyingPlan;
        private string lastDetectionResult = "尚未检测";
        private string lastErrorMessage = "无";

        internal MainForm()
        {
            Text = "地狱潜兵 CPU 修改器 v" + AppConstants.Version;
            ClientSize = new Size(1240, 960);
            MinimumSize = new Size(1040, 900);
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96F, 96F);
            Font = CreateUiFont(9F, FontStyle.Regular);
            BackColor = pageColor;
            MaximizeBox = true;
            AutoScroll = true;

            BuildUi();
            Load += MainForm_Load;
            FormClosing += MainForm_FormClosing;
        }

        private void BuildUi()
        {
            SuspendLayout();
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = pageColor,
                Padding = new Padding(22, 16, 22, 16),
                ColumnCount = 1,
                RowCount = 5
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, LayoutBannerAreaHeight));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, LayoutPathAreaHeight));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, LayoutMainAreaHeight));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, LayoutCommandAreaHeight));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            root.Controls.Add(BuildBanner(), 0, 0);
            root.Controls.Add(BuildPathCard(), 0, 1);
            root.Controls.Add(BuildMainArea(), 0, 2);
            root.Controls.Add(BuildCommandCard(), 0, 3);
            root.Controls.Add(BuildLogCard(), 0, 4);
            Controls.Add(root);
            ResumeLayout(true);
        }

        private Control BuildBanner()
        {
            var banner = new BannerPanel(GetHeroImagePath())
            {
                Dock = DockStyle.Fill,
                Margin = new Padding(4, 2, 4, 10),
                Padding = new Padding(22, 18, 22, 14)
            };
            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Transparent,
                ColumnCount = 2,
                RowCount = 2
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 74F));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 26F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 56F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 44F));

            var title = new Label
            {
                Text = "地狱潜兵 2 配置修改",
                Dock = DockStyle.Fill,
                Font = CreateUiFont(19F, FontStyle.Bold),
                ForeColor = Color.White,
                BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleLeft
            };
            var subtitle = new Label
            {
                Text = "仅修改 data\\settings.ini · 安全写入 · 自动校验 · 失败回滚",
                Dock = DockStyle.Fill,
                ForeColor = Color.FromArgb(226, 232, 240),
                BackColor = Color.Transparent,
                TextAlign = ContentAlignment.TopLeft,
                AutoEllipsis = true
            };
            var statusPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Transparent,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Padding = new Padding(4, 15, 0, 0)
            };
            statusDot.Size = new Size(10, 10);
            statusDot.Margin = new Padding(0, 5, 8, 0);
            statusDot.BackColor = cautionColor;
            statusLabel.Text = "准备就绪";
            statusLabel.AutoSize = true;
            statusLabel.MaximumSize = new Size(190, 40);
            statusLabel.ForeColor = cautionColor;
            statusLabel.BackColor = Color.Transparent;
            statusPanel.Controls.Add(statusDot);
            statusPanel.Controls.Add(statusLabel);

            layout.Controls.Add(title, 0, 0);
            layout.Controls.Add(statusPanel, 1, 0);
            layout.Controls.Add(subtitle, 0, 1);
            layout.SetColumnSpan(subtitle, 2);
            banner.Controls.Add(layout);
            return banner;
        }

        private Control BuildPathCard()
        {
            var card = MakeCard();
            card.Margin = new Padding(4, 4, 4, 10);
            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(14, 9, 14, 10),
                ColumnCount = 4,
                RowCount = 2
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100F));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 132F));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 1F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28F));
            // Keep the edit field and its margins intact at high DPI.
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40F));

            layout.Controls.Add(MakeTitle("游戏目录"), 0, 0);
            layout.SetColumnSpan(layout.GetControlFromPosition(0, 0), 3);
            gameDirBox.Dock = DockStyle.Fill;
            gameDirBox.Margin = new Padding(2, 4, 8, 2);
            gameDirBox.TextChanged += delegate
            {
                tips.SetToolTip(gameDirBox, gameDirBox.Text);
            };
            var browseButton = MakeButton("浏览", false);
            browseButton.Dock = DockStyle.Fill;
            browseButton.Margin = new Padding(4, 2, 4, 2);
            browseButton.Click += delegate { SafeUi(delegate { BrowseGameDirectory(); }, "选择游戏目录"); };
            var detectButton = MakeButton("自动检测", false);
            detectButton.Dock = DockStyle.Fill;
            detectButton.Margin = new Padding(4, 2, 0, 2);
            detectButton.Click += delegate { SafeUi(delegate { AutoDetectGameDir(true); }, "自动检测游戏目录"); };
            layout.Controls.Add(gameDirBox, 0, 1);
            layout.Controls.Add(browseButton, 1, 1);
            layout.Controls.Add(detectButton, 2, 1);
            card.Controls.Add(layout);
            return card;
        }

        private Control BuildMainArea()
        {
            var split = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                Margin = new Padding(4, 2, 4, 10)
            };
            split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 54F));
            split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 46F));
            split.Controls.Add(BuildPlanCard(), 0, 0);
            split.Controls.Add(BuildStatusCard(), 1, 0);
            return split;
        }

        private Control BuildPlanCard()
        {
            var card = MakeCard();
            card.Margin = new Padding(0, 0, 7, 0);
            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(16, 10, 16, 10),
                ColumnCount = 3,
                RowCount = 9
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, LayoutPlanLabelWidth));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, LayoutPlanSummaryHeight));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, LayoutPlanWarningHeight));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            var title = MakeTitle("修改方案");
            layout.Controls.Add(title, 0, 0);
            layout.SetColumnSpan(title, 3);

            planBox.DropDownStyle = ComboBoxStyle.DropDownList;
            planBox.Dock = DockStyle.Fill;
            planBox.Margin = new Padding(0, 3, 0, 5);
            planBox.Items.AddRange(new object[]
            {
                "主方案：U9-275HX 游戏可用 8 线程",
                "备用：高帧率 10 线程",
                "备用：激进高帧 12 线程",
                "备用：低温稳定 6 线程",
                "备用：只改 CPU，可用 8 线程",
                "恢复常见默认值",
                "自定义"
            });
            planBox.SelectedIndexChanged += PlanBox_SelectedIndexChanged;
            tips.SetToolTip(planBox, "主方案优先稳定；备用方案用于按帧率、温度与稳定性逐项测试。");
            layout.Controls.Add(planBox, 0, 1);
            layout.SetColumnSpan(planBox, 3);

            cpuCheck.Text = "修改 CPU 线程";
            cpuCheck.Dock = DockStyle.Fill;
            cpuCheck.ForeColor = textColor;
            cpuCheck.CheckedChanged += MarkCustom;
            tips.SetToolTip(cpuCheck, "修改 num_reserved_threads。不会设置进程亲和性，也不会操作游戏内存。");
            audioCheck.Text = "修改音频缓冲";
            audioCheck.Dock = DockStyle.Fill;
            audioCheck.ForeColor = textColor;
            audioCheck.CheckedChanged += MarkCustom;
            tips.SetToolTip(audioCheck, "修改 memory_size 和 num_refills_in_voice。该项并非所有卡顿都有效。");
            layout.Controls.Add(cpuCheck, 0, 2);
            layout.SetColumnSpan(cpuCheck, 2);
            layout.Controls.Add(audioCheck, 2, 2);

            layout.Controls.Add(MakeFieldLabel("当前保留线程数"), 0, 3);
            reservedThreadsBox.Dock = DockStyle.Fill;
            reservedThreadsBox.Minimum = 0;
            reservedThreadsBox.Maximum = Math.Max(0, Environment.ProcessorCount - 4);
            reservedThreadsBox.ValueChanged += MarkCustom;
            layout.Controls.Add(reservedThreadsBox, 1, 3);
            var recommendButton = MakeButton("智能填入", false);
            recommendButton.Dock = DockStyle.Fill;
            recommendButton.Margin = new Padding(7, 1, 0, 1);
            recommendButton.Click += delegate
            {
                SafeUi(delegate
                {
                    SetReservedValue(RecommendReservedThreads());
                    MarkCustom(null, EventArgs.Empty);
                    Log("已按当前 CPU 逻辑线程数填入建议值。");
                }, "智能填入");
            };
            layout.Controls.Add(recommendButton, 2, 3);

            layout.Controls.Add(MakeFieldLabel("音频缓存 MB"), 0, 4);
            memorySizeBox.Dock = DockStyle.Fill;
            memorySizeBox.Minimum = 1;
            memorySizeBox.Maximum = 512;
            memorySizeBox.ValueChanged += MarkCustom;
            layout.Controls.Add(memorySizeBox, 1, 4);
            layout.SetColumnSpan(memorySizeBox, 2);

            layout.Controls.Add(MakeFieldLabel("缓冲补充次数"), 0, 5);
            refillsBox.Dock = DockStyle.Fill;
            refillsBox.Minimum = 1;
            refillsBox.Maximum = 16;
            refillsBox.ValueChanged += MarkCustom;
            tips.SetToolTip(refillsBox, "对应 num_refills_in_voice。数值较高可能增加音频缓冲余量，也可能略增延迟。");
            layout.Controls.Add(refillsBox, 1, 5);
            layout.SetColumnSpan(refillsBox, 2);

            threadSummaryLabel.Dock = DockStyle.Fill;
            threadSummaryLabel.ForeColor = textColor;
            threadSummaryLabel.AutoEllipsis = false;
            layout.Controls.Add(threadSummaryLabel, 0, 6);
            layout.SetColumnSpan(threadSummaryLabel, 3);

            safetyWarningLabel.Dock = DockStyle.Fill;
            safetyWarningLabel.ForeColor = cautionColor;
            safetyWarningLabel.AutoEllipsis = true;
            layout.Controls.Add(safetyWarningLabel, 0, 7);
            layout.SetColumnSpan(safetyWarningLabel, 3);

            var note = new Label
            {
                Text = "主方案保持 16 / 75MB / 4。每次只测试一个方案并观察帧时间、温度和音频稳定性。",
                Dock = DockStyle.Fill,
                ForeColor = mutedColor,
                AutoEllipsis = true
            };
            layout.Controls.Add(note, 0, 8);
            layout.SetColumnSpan(note, 3);
            card.Controls.Add(layout);
            return card;
        }

        private Control BuildStatusCard()
        {
            var card = MakeCard();
            card.Margin = new Padding(7, 0, 0, 0);
            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(16, 10, 16, 10),
                ColumnCount = 1,
                RowCount = 7
            };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 92F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            layout.Controls.Add(MakeTitle("配置状态"), 0, 0);

            currentPathLabel.Text = "配置文件：未读取";
            currentPathLabel.Dock = DockStyle.Fill;
            currentPathLabel.ForeColor = mutedColor;
            currentPathLabel.AutoEllipsis = true;
            layout.Controls.Add(currentPathLabel, 0, 1);

            currentLabel.Text = "当前参数：未读取";
            currentLabel.Dock = DockStyle.Fill;
            currentLabel.ForeColor = textColor;
            currentLabel.AutoEllipsis = true;
            layout.Controls.Add(currentLabel, 0, 2);

            adviceLabel.Text = "智能建议：读取中";
            adviceLabel.Dock = DockStyle.Fill;
            adviceLabel.ForeColor = mutedColor;
            adviceLabel.AutoEllipsis = true;
            layout.Controls.Add(adviceLabel, 0, 3);

            fileStateLabel.Text = "文件状态：未读取";
            fileStateLabel.Dock = DockStyle.Fill;
            fileStateLabel.ForeColor = cautionColor;
            layout.Controls.Add(fileStateLabel, 0, 4);

            var quickActions = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 3,
                RowCount = 1,
                Margin = new Padding(0)
            };
            quickActions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33F));
            quickActions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.34F));
            quickActions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33F));
            quickActions.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            var checkButton = MakeButton("检测失效", false);
            checkButton.Dock = DockStyle.Fill;
            checkButton.Margin = new Padding(0, 1, 4, 1);
            checkButton.Click += delegate { SafeUi(delegate { CheckFixStatus(true); }, "检测配置"); };
            var refreshButton = MakeButton("刷新", false);
            refreshButton.Dock = DockStyle.Fill;
            refreshButton.Margin = new Padding(4, 1, 4, 1);
            refreshButton.Click += delegate { SafeUi(RefreshAll, "刷新状态"); };
            var restoreButton = MakeButton("恢复备份", false);
            restoreButton.Dock = DockStyle.Fill;
            restoreButton.Margin = new Padding(4, 1, 0, 1);
            restoreButton.Click += delegate { SafeUi(RestoreLatestBackup, "恢复备份"); };
            quickActions.Controls.Add(checkButton, 0, 0);
            quickActions.Controls.Add(refreshButton, 1, 0);
            quickActions.Controls.Add(restoreButton, 2, 0);
            layout.Controls.Add(quickActions, 0, 5);

            var explanation = new Label
            {
                Text = "自动检测只更新状态；只有手动点击“检测失效”才会弹出结果。",
                Dock = DockStyle.Fill,
                ForeColor = mutedColor,
                AutoEllipsis = true
            };
            layout.Controls.Add(explanation, 0, 6);
            card.Controls.Add(layout);
            return card;
        }

        private Control BuildCommandCard()
        {
            var card = MakeCard();
            card.Margin = new Padding(4, 2, 4, 4);
            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(14, 8, 14, 7),
                ColumnCount = 1,
                RowCount = 2
            };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, LayoutCommandButtonsHeight));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            var actions = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = LayoutCommandColumns,
                RowCount = LayoutCommandRows,
                Margin = new Padding(0)
            };
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33F));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.34F));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33F));
            actions.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));
            actions.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));
            var applyButton = MakeButton("应用修改", true);
            PrepareCommandButton(applyButton, 0, 0);
            applyButton.Click += delegate { SafeUi(delegate { ApplyChanges(false); }, "应用修改"); };
            var applyLaunchButton = MakeButton("应用修改并启动游戏", true);
            PrepareCommandButton(applyLaunchButton, 1, 0);
            applyLaunchButton.Click += delegate { SafeUi(delegate { ApplyChanges(true); }, "应用并启动游戏"); };
            var unlockButton = MakeButton("一键解除只读", false);
            PrepareCommandButton(unlockButton, 2, 0);
            unlockButton.Click += delegate { SafeUi(UnlockSettingsFile, "解除只读"); };
            var folderButton = MakeButton("打开配置目录", false);
            PrepareCommandButton(folderButton, 0, 1);
            folderButton.Click += delegate { SafeUi(OpenSettingsDirectory, "打开配置目录"); };
            var notepadButton = MakeButton("记事本打开", false);
            PrepareCommandButton(notepadButton, 1, 1);
            notepadButton.Click += delegate { SafeUi(OpenSettingsInNotepad, "打开配置文件"); };
            var diagnosticButton = MakeButton("复制诊断信息", false);
            PrepareCommandButton(diagnosticButton, 2, 1);
            diagnosticButton.Click += delegate { SafeUi(CopyDiagnostics, "复制诊断信息"); };
            actions.Controls.Add(applyButton, 0, 0);
            actions.Controls.Add(applyLaunchButton, 1, 0);
            actions.Controls.Add(unlockButton, 2, 0);
            actions.Controls.Add(folderButton, 0, 1);
            actions.Controls.Add(notepadButton, 1, 1);
            actions.Controls.Add(diagnosticButton, 2, 1);
            layout.Controls.Add(actions, 0, 0);

            lockAfterApplyCheck.Text = "修改完成后将配置文件设为只读";
            lockAfterApplyCheck.Dock = DockStyle.Fill;
            lockAfterApplyCheck.AutoSize = false;
            lockAfterApplyCheck.TextAlign = ContentAlignment.MiddleLeft;
            lockAfterApplyCheck.Margin = new Padding(0);
            lockAfterApplyCheck.ForeColor = textColor;
            lockAfterApplyCheck.CheckedChanged += delegate { SaveSettingsQuietly(); };
            tips.SetToolTip(lockAfterApplyCheck, "可降低游戏更新以外的意外覆盖；Steam 更新前如遇问题，可使用“一键解除只读”。");
            layout.Controls.Add(lockAfterApplyCheck, 0, 1);
            card.Controls.Add(layout);
            return card;
        }

        private Control BuildLogCard()
        {
            var card = MakeCard();
            card.Margin = new Padding(4, 2, 4, 2);
            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(14, 8, 14, 10),
                ColumnCount = 1,
                RowCount = 2
            };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            layout.Controls.Add(MakeTitle("操作日志（最近 100 条）"), 0, 0);
            logBox.Dock = DockStyle.Fill;
            logBox.Multiline = true;
            logBox.ScrollBars = ScrollBars.Vertical;
            logBox.BorderStyle = BorderStyle.None;
            logBox.ReadOnly = true;
            logBox.BackColor = cardColor;
            logBox.ForeColor = Color.FromArgb(68, 78, 92);
            layout.Controls.Add(logBox, 0, 1);
            card.Controls.Add(layout);
            return card;
        }

        private void MainForm_Load(object sender, EventArgs e)
        {
            SafeUi(delegate
            {
                LoadSavedSettings();
                if (string.IsNullOrWhiteSpace(gameDirBox.Text) || !Directory.Exists(gameDirBox.Text))
                {
                    var found = FindGameDir();
                    if (!string.IsNullOrEmpty(found)) gameDirBox.Text = found;
                }
                UpdateThreadLimits();
                RefreshAll();
                Log("v" + AppConstants.Version + " 已启动。未执行任何写入操作。");
            }, "初始化程序");
        }

        private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            SaveSettingsQuietly();
        }

        private void LoadSavedSettings()
        {
            var settings = UserSettingsStore.Load();
            applyingPlan = true;
            gameDirBox.Text = settings.GameDirectory;
            planBox.SelectedIndex = Clamp(settings.PlanIndex, 0, 6);
            if (settings.PlanIndex < 6)
            {
                ApplyPresetValues(settings.PlanIndex);
            }
            else
            {
                SetReservedValue(settings.ReservedThreads);
                SetNumericValue(memorySizeBox, settings.MemoryMb);
                SetNumericValue(refillsBox, settings.Refills);
                cpuCheck.Checked = settings.ModifyCpu;
                audioCheck.Checked = settings.ModifyAudio;
            }
            lockAfterApplyCheck.Checked = settings.LockAfterApply;
            applyingPlan = false;
        }

        private void SaveSettingsQuietly()
        {
            try
            {
                UserSettingsStore.Save(new UserSettings
                {
                    GameDirectory = gameDirBox.Text.Trim(),
                    PlanIndex = planBox.SelectedIndex < 0 ? 0 : planBox.SelectedIndex,
                    ReservedThreads = (int)reservedThreadsBox.Value,
                    MemoryMb = (int)memorySizeBox.Value,
                    Refills = (int)refillsBox.Value,
                    ModifyCpu = cpuCheck.Checked,
                    ModifyAudio = audioCheck.Checked,
                    LockAfterApply = lockAfterApplyCheck.Checked
                });
            }
            catch (Exception ex)
            {
                lastErrorMessage = "保存本地设置失败：" + ex.Message;
                Log(lastErrorMessage);
            }
        }

        private void BrowseGameDirectory()
        {
            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = "选择 Helldivers 2 游戏安装目录";
                dialog.SelectedPath = Directory.Exists(gameDirBox.Text)
                    ? gameDirBox.Text
                    : Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                gameDirBox.Text = dialog.SelectedPath;
                SaveSettingsQuietly();
                RefreshAll();
            }
        }

        private void AutoDetectGameDir(bool showResult)
        {
            var found = FindGameDir();
            if (string.IsNullOrEmpty(found))
            {
                SetStatus("未找到配置文件", StatusLevel.Error);
                lastDetectionResult = "未自动找到游戏目录";
                if (showResult) Warn("没有自动找到游戏目录，请手动选择 Helldivers 2 安装目录。");
                return;
            }
            gameDirBox.Text = found;
            SaveSettingsQuietly();
            RefreshAll();
            if (showResult) Log("已检测到游戏目录：" + RedactPath(found));
        }

        private void PlanBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (applyingPlan || planBox.SelectedIndex < 0) return;
            applyingPlan = true;
            if (planBox.SelectedIndex < 6) ApplyPresetValues(planBox.SelectedIndex);
            applyingPlan = false;
            TargetSettingsChanged();
        }

        private void ApplyPresetValues(int index)
        {
            if (index == 0)
            {
                cpuCheck.Checked = true;
                audioCheck.Checked = true;
                SetReservedValue(16);
                SetNumericValue(memorySizeBox, 75);
                SetNumericValue(refillsBox, 4);
            }
            else if (index == 1)
            {
                cpuCheck.Checked = true;
                audioCheck.Checked = true;
                SetReservedValue(Math.Max(0, Environment.ProcessorCount - 10));
                SetNumericValue(memorySizeBox, 75);
                SetNumericValue(refillsBox, 4);
            }
            else if (index == 2)
            {
                cpuCheck.Checked = true;
                audioCheck.Checked = true;
                SetReservedValue(Math.Max(0, Environment.ProcessorCount - 12));
                SetNumericValue(memorySizeBox, 75);
                SetNumericValue(refillsBox, 4);
            }
            else if (index == 3)
            {
                cpuCheck.Checked = true;
                audioCheck.Checked = true;
                SetReservedValue(Math.Max(0, Environment.ProcessorCount - 6));
                SetNumericValue(memorySizeBox, 75);
                SetNumericValue(refillsBox, 4);
            }
            else if (index == 4)
            {
                cpuCheck.Checked = true;
                audioCheck.Checked = false;
                SetReservedValue(16);
                SetNumericValue(memorySizeBox, 75);
                SetNumericValue(refillsBox, 4);
            }
            else if (index == 5)
            {
                cpuCheck.Checked = true;
                audioCheck.Checked = true;
                SetReservedValue(2);
                SetNumericValue(memorySizeBox, 25);
                SetNumericValue(refillsBox, 2);
            }
        }

        private void MarkCustom(object sender, EventArgs e)
        {
            if (applyingPlan) return;
            if (planBox.SelectedIndex != 6)
            {
                applyingPlan = true;
                planBox.SelectedIndex = 6;
                applyingPlan = false;
            }
            TargetSettingsChanged();
        }

        private void TargetSettingsChanged()
        {
            UpdateThreadSummary();
            CheckFixStatus(false);
            SaveSettingsQuietly();
        }

        private void ApplyChanges(bool launchAfter)
        {
            var ini = GetSettingsFile();
            if (ini == null)
            {
                Warn("没有找到 data\\settings.ini，请检查游戏目录。");
                return;
            }
            if (IsGameRunning())
            {
                Warn("检测到 helldivers2.exe 正在运行。请先正常关闭游戏，再应用配置。程序不会强制结束游戏或反作弊进程。");
                return;
            }

            string validationError;
            var target = GetTarget();
            if (!ValidateTarget(target, out validationError))
            {
                Warn(validationError);
                return;
            }

            try
            {
                var oldText = ConfigFileOperations.ReadText(ini);
                var missing = ConfigFileOperations.FindMissingRequiredKeys(oldText, target);
                if (missing.Length > 0)
                {
                    Warn("配置文件缺少必要字段，已停止修改：" + missing);
                    return;
                }
                var newText = ConfigFileOperations.BuildModifiedText(oldText, target);
                if (newText != oldText)
                {
                    var preview = BuildPreview(oldText, target, ini);
                    if (MessageBox.Show(this, preview, "确认修改", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                    {
                        Log("已取消，没有修改配置文件。");
                        return;
                    }
                }

                if (IsGameRunning())
                {
                    Warn("确认期间检测到游戏已经启动。为避免配置竞争，本次操作已安全取消。");
                    return;
                }

                var result = ConfigFileOperations.ApplySafely(
                    ini,
                    oldText,
                    newText,
                    target,
                    lockAfterApplyCheck.Checked,
                    null);
                if (result.Changed)
                {
                    Log("修改完成并通过完整性校验。备份：" + Path.GetFileName(result.BackupPath));
                }
                else if (lockAfterApplyCheck.Checked)
                {
                    Log("配置内容已是目标值，已确认只读状态；未创建重复备份。");
                }
                else
                {
                    Log("配置已经是目标值，未写入，也未创建重复备份。");
                }
                if (!string.IsNullOrEmpty(result.MaintenanceWarning)) Log(result.MaintenanceWarning);
                lastErrorMessage = "无";
                SetStatus("配置有效", StatusLevel.Success);
                RefreshAll();
                if (launchAfter) LaunchGame();
                else MessageBox.Show(
                    this,
                    result.Changed ? "修改成功，配置已通过完整性校验。" : "当前配置已经是目标值，不需要重复修改。",
                    result.Changed ? "完成" : "无需修改",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                lastErrorMessage = ex.Message;
                SetStatus("修改失败", StatusLevel.Error);
                Log("修改失败：" + FirstLine(ex.Message));
                MessageBox.Show(this, ex.Message, "修改失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                RefreshFileState();
            }
        }

        private string BuildPreview(string oldText, ConfigTarget target, string ini)
        {
            var old = ConfigFileOperations.ParseSnapshot(oldText);
            var preview = new StringBuilder();
            preview.AppendLine("确认应用以下修改吗？");
            preview.AppendLine();
            if (target.ModifyCpu && old.ReservedThreads != target.ReservedThreads)
            {
                preview.AppendLine("num_reserved_threads: " + ShowValue(old.ReservedThreads) + " -> " + target.ReservedThreads);
            }
            if (target.ModifyAudio && old.MemorySize != target.MemorySize)
            {
                preview.AppendLine("memory_size: " + ShowMb(old.MemorySize) + " -> " + ToMb(target.MemorySize));
            }
            if (target.ModifyAudio && old.Refills != target.Refills)
            {
                preview.AppendLine("num_refills_in_voice: " + ShowValue(old.Refills) + " -> " + target.Refills);
            }
            preview.AppendLine();
            preview.AppendLine("文件：" + ini);
            preview.AppendLine();
            preview.Append("程序会记录原文件完整性指纹，确认后如文件发生变化会安全停止；写入失败时自动回滚。");
            if (lockAfterApplyCheck.Checked) preview.Append(" 成功后会设为只读。");
            return preview.ToString();
        }

        private void CheckFixStatus(bool showMessage)
        {
            var ini = GetSettingsFile();
            if (ini == null)
            {
                lastDetectionResult = "未找到配置文件";
                SetStatus("未找到配置文件", StatusLevel.Error);
                if (showMessage) Warn("没有找到 data\\settings.ini，请检查游戏目录。");
                return;
            }

            try
            {
                var target = GetTarget();
                string validationError;
                if (!ValidateTarget(target, out validationError))
                {
                    lastDetectionResult = "目标参数无效：" + validationError;
                    SetStatus("未选择修改项", StatusLevel.Warning);
                    if (showMessage)
                    {
                        MessageBox.Show(
                            this,
                            validationError,
                            "无法检测",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Warning);
                    }
                    return;
                }
                var text = ConfigFileOperations.ReadText(ini);
                var missing = ConfigFileOperations.FindMissingRequiredKeys(text, target);
                if (missing.Length > 0)
                {
                    lastDetectionResult = "配置缺少字段：" + missing;
                    SetStatus("配置无效", StatusLevel.Error);
                    if (showMessage) Warn("配置文件缺少必要字段，无法检测：" + missing);
                    return;
                }

                var current = ConfigFileOperations.ParseSnapshot(text);
                var differences = new List<string>();
                if (target.ModifyCpu && current.ReservedThreads != target.ReservedThreads)
                {
                    differences.Add("num_reserved_threads: 当前 " + ShowValue(current.ReservedThreads) + "，目标 " + target.ReservedThreads);
                }
                if (target.ModifyAudio && current.MemorySize != target.MemorySize)
                {
                    differences.Add("memory_size: 当前 " + ShowMb(current.MemorySize) + "，目标 " + ToMb(target.MemorySize));
                }
                if (target.ModifyAudio && current.Refills != target.Refills)
                {
                    differences.Add("num_refills_in_voice: 当前 " + ShowValue(current.Refills) + "，目标 " + target.Refills);
                }

                if (differences.Count == 0)
                {
                    lastDetectionResult = "当前配置符合所选方案";
                    var aggressive = IsAggressiveTarget(target);
                    SetStatus(aggressive ? "配置有效，参数偏激进" : "配置有效", aggressive ? StatusLevel.Warning : StatusLevel.Success);
                    if (showMessage)
                    {
                        Log("手动检测完成：当前配置符合所选方案。");
                        MessageBox.Show(this, "检测完成：当前配置仍符合所选方案，修复没有失效。", "配置有效", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                }
                else
                {
                    lastDetectionResult = "配置与目标不一致：" + string.Join("；", differences.ToArray());
                    SetStatus("修复失效", StatusLevel.Error);
                    if (showMessage)
                    {
                        Log("手动检测完成：配置与所选方案不一致。");
                        MessageBox.Show(
                            this,
                            "检测完成：配置可能已被 Steam 更新覆盖。\r\n\r\n" + string.Join("\r\n", differences.ToArray()) + "\r\n\r\n可以点击“应用修改”重新写入。",
                            "修复已失效",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Warning);
                    }
                }
                lastErrorMessage = "无";
            }
            catch (Exception ex)
            {
                lastDetectionResult = "检测失败";
                lastErrorMessage = ex.Message;
                SetStatus("检测失败", StatusLevel.Error);
                if (showMessage)
                {
                    Log("检测失败：" + FirstLine(ex.Message));
                    MessageBox.Show(this, ex.Message, "检测失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void RestoreLatestBackup()
        {
            var ini = GetSettingsFile();
            if (ini == null)
            {
                Warn("没有找到 data\\settings.ini。");
                return;
            }
            if (IsGameRunning())
            {
                Warn("检测到游戏正在运行。请先关闭游戏，再恢复配置备份。");
                return;
            }

            try
            {
                var backups = ConfigFileOperations.GetBackups(ini);
                if (backups.Length == 0)
                {
                    Warn("没有找到自动备份文件。");
                    return;
                }
                var latest = backups[0];
                if (!File.Exists(latest))
                {
                    Warn("最近备份已不存在，请刷新后重试。");
                    return;
                }
                if (MessageBox.Show(
                    this,
                    "恢复最近备份吗？\r\n\r\n" + latest +
                    "\r\n\r\n程序会永久保留恢复前配置，并对整个文件进行完整性校验。",
                    "确认恢复",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question) != DialogResult.Yes)
                {
                    return;
                }

                if (IsGameRunning())
                {
                    Warn("确认期间检测到游戏已经启动。为避免配置竞争，本次恢复已安全取消。");
                    return;
                }
                var result = ConfigFileOperations.RestoreBackupSafely(latest, ini, null);
                lastErrorMessage = "无";
                Log("已恢复并校验最近备份：" + Path.GetFileName(latest));
                Log("恢复前配置已永久保留：" + Path.GetFileName(result.PreRestoreBackupPath));
                if (result.LegacyBackupWithoutIntegrityMetadata)
                {
                    Log("提示：所选为旧版备份，无完整性元数据；已校验三个必要参数和恢复后的完整文件。");
                }
                if (!string.IsNullOrEmpty(result.MaintenanceWarning)) Log(result.MaintenanceWarning);
                SetStatus("备份已恢复", StatusLevel.Success);
                RefreshAll();
                MessageBox.Show(this, "最近备份已恢复并校验。", "恢复完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                lastErrorMessage = ex.Message;
                SetStatus("恢复失败", StatusLevel.Error);
                Log("恢复失败：" + FirstLine(ex.Message));
                MessageBox.Show(this, ex.Message, "恢复失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                RefreshFileState();
            }
        }

        private void UnlockSettingsFile()
        {
            var ini = GetSettingsFile();
            if (ini == null)
            {
                Warn("没有找到配置文件，无法解除只读。");
                return;
            }
            ConfigFileOperations.SetReadOnly(ini, false);
            Log("已解除配置文件只读属性。");
            SetStatus("配置可写", StatusLevel.Success);
            RefreshFileState();
        }

        private void OpenSettingsDirectory()
        {
            var ini = GetSettingsFile();
            if (ini == null)
            {
                Warn("没有找到配置文件。");
                return;
            }
            Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + ini + "\"") { UseShellExecute = true });
        }

        private void OpenSettingsInNotepad()
        {
            var ini = GetSettingsFile();
            if (ini == null)
            {
                Warn("没有找到配置文件。");
                return;
            }
            Process.Start(new ProcessStartInfo("notepad.exe", "\"" + ini + "\"") { UseShellExecute = true });
        }

        private void CopyDiagnostics()
        {
            var ini = GetSettingsFile();
            ConfigSnapshot current = null;
            string readOnlyState;
            try
            {
                current = ini == null ? null : ConfigFileOperations.ReadSnapshot(ini);
                readOnlyState = ini == null ? "文件不存在" : (ConfigFileOperations.IsReadOnly(ini) ? "是" : "否");
            }
            catch (UnauthorizedAccessException)
            {
                readOnlyState = "无访问权限";
            }
            catch (Exception ex)
            {
                readOnlyState = "读取失败：" + ex.Message;
            }

            var target = GetTarget();
            var diagnostics = new StringBuilder();
            diagnostics.AppendLine("地狱潜兵 CPU 修改器诊断信息");
            diagnostics.AppendLine("工具版本：" + AppConstants.Version);
            diagnostics.AppendLine("操作系统版本：" + Environment.OSVersion.VersionString);
            diagnostics.AppendLine("CPU 名称：" + (GetCpuName().Length == 0 ? "未读取" : GetCpuName()));
            diagnostics.AppendLine("CPU 逻辑线程数：" + Environment.ProcessorCount);
            diagnostics.AppendLine("游戏目录：" + RedactPath(gameDirBox.Text.Trim()));
            diagnostics.AppendLine("配置文件路径：" + RedactPath(ini ?? "未找到"));
            diagnostics.AppendLine("配置文件是否只读：" + RedactDiagnosticText(readOnlyState));
            diagnostics.AppendLine("当前配置参数：" + (current == null ? "未读取" : current.ToDisplayText()));
            diagnostics.AppendLine("目标配置参数：" + TargetToText(target));
            diagnostics.AppendLine("最后检测结果：" + RedactDiagnosticText(lastDetectionResult));
            diagnostics.AppendLine("最后错误信息：" + RedactDiagnosticText(lastErrorMessage));
            Clipboard.SetText(diagnostics.ToString());
            Log("诊断信息已复制，用户目录已替换为 %USERPROFILE%。");
            MessageBox.Show(this, "诊断信息已复制。未包含用户名、账户或网络数据。", "复制完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void LaunchGame()
        {
            try
            {
                GameLauncher.Launch(delegate(ProcessStartInfo startInfo) { Process.Start(startInfo); });
                Log("已调用 Steam 启动《地狱潜兵 2》。");
            }
            catch (Exception ex)
            {
                lastErrorMessage = "Steam 启动失败：" + ex.Message;
                SetStatus("配置有效，启动失败", StatusLevel.Warning);
                Log(lastErrorMessage);
                MessageBox.Show(
                    this,
                    "配置已经成功应用并校验，但调用 Steam 启动游戏失败。\r\n\r\n" + ex.Message,
                    "启动游戏失败",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }

        internal static bool IsGameRunning()
        {
            Process[] processes = null;
            try
            {
                processes = Process.GetProcessesByName("helldivers2");
                return processes.Length > 0;
            }
            finally
            {
                if (processes != null)
                {
                    foreach (var process in processes) process.Dispose();
                }
            }
        }

        private void RefreshAll()
        {
            UpdateSmartAdvice();
            UpdateThreadSummary();
            RefreshCurrentValues();
            RefreshFileState();
            CheckFixStatus(false);
        }

        private void RefreshCurrentValues()
        {
            var ini = GetSettingsFile();
            if (ini == null)
            {
                currentPathLabel.Text = "配置文件：未找到 data\\settings.ini";
                currentLabel.Text = "当前参数：未读取";
                tips.SetToolTip(currentPathLabel, "未找到配置文件");
                return;
            }
            try
            {
                var snapshot = ConfigFileOperations.ReadSnapshot(ini);
                currentPathLabel.Text = "配置文件：" + ini;
                currentLabel.Text =
                    "CPU 保留线程：" + ShowValue(snapshot.ReservedThreads) +
                    "    游戏可用：" + EstimatedAvailable(snapshot.ReservedThreads) + "\r\n" +
                    "音频缓存：" + ShowMb(snapshot.MemorySize) + "\r\n" +
                    "音频 refill：" + ShowValue(snapshot.Refills);
                tips.SetToolTip(currentPathLabel, ini);
            }
            catch (Exception ex)
            {
                currentPathLabel.Text = "配置文件读取失败";
                currentLabel.Text = FirstLine(ex.Message);
                lastErrorMessage = ex.Message;
            }
        }

        private void RefreshFileState()
        {
            var ini = GetSettingsFile();
            if (ini == null)
            {
                fileStateLabel.Text = "文件状态：文件不存在";
                fileStateLabel.ForeColor = errorColor;
                return;
            }
            try
            {
                if (ConfigFileOperations.IsReadOnly(ini))
                {
                    fileStateLabel.Text = "文件状态：已锁定（只读）";
                    fileStateLabel.ForeColor = cautionColor;
                    return;
                }
                using (File.Open(ini, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
                {
                }
                fileStateLabel.Text = "文件状态：可写";
                fileStateLabel.ForeColor = okColor;
            }
            catch (UnauthorizedAccessException)
            {
                fileStateLabel.Text = "文件状态：无访问权限";
                fileStateLabel.ForeColor = errorColor;
            }
            catch (IOException)
            {
                fileStateLabel.Text = "文件状态：暂时被占用";
                fileStateLabel.ForeColor = cautionColor;
            }
            catch (Exception ex)
            {
                fileStateLabel.Text = "文件状态：读取失败";
                fileStateLabel.ForeColor = errorColor;
                lastErrorMessage = ex.Message;
            }
        }

        private void UpdateSmartAdvice()
        {
            var cpu = GetCpuName();
            var threads = Environment.ProcessorCount;
            var recommended = RecommendReservedThreads();
            adviceLabel.Text = "智能建议：" + threads + " 个逻辑线程，建议保留 " + recommended + "。\r\n" +
                               (cpu.Length > 0 ? TrimText(cpu, 54) : "CPU 名称读取失败，但不影响配置修改。");
        }

        private void UpdateThreadLimits()
        {
            var maximum = Math.Max(0, Environment.ProcessorCount - 4);
            reservedThreadsBox.Maximum = maximum;
            if (reservedThreadsBox.Value > reservedThreadsBox.Maximum)
            {
                reservedThreadsBox.Value = reservedThreadsBox.Maximum;
            }
        }

        private void UpdateThreadSummary()
        {
            var logical = Environment.ProcessorCount;
            var reserved = (int)reservedThreadsBox.Value;
            var available = logical - reserved;
            threadSummaryLabel.Text = "CPU 逻辑线程：" + logical + "  |  当前保留：" + reserved + "\r\n" +
                                      "游戏预计可用线程：" + available;
            if (!cpuCheck.Checked && !audioCheck.Checked)
            {
                safetyWarningLabel.Text = "请选择至少一个要修改或检测的项目。";
                safetyWarningLabel.ForeColor = cautionColor;
            }
            else if (cpuCheck.Checked && available < 4)
            {
                safetyWarningLabel.Text = "禁止应用：必须至少给游戏保留 4 个可用线程。";
                safetyWarningLabel.ForeColor = errorColor;
            }
            else if (cpuCheck.Checked && IsAggressiveTarget(GetTarget()))
            {
                safetyWarningLabel.Text = "注意：该参数偏激进，可能提高峰值帧率，也可能增加温度或帧时间波动。";
                safetyWarningLabel.ForeColor = cautionColor;
            }
            else
            {
                safetyWarningLabel.Text = "参数范围有效。";
                safetyWarningLabel.ForeColor = okColor;
            }
        }

        internal static bool ValidateTarget(ConfigTarget target, out string error)
        {
            if (!target.ModifyCpu && !target.ModifyAudio)
            {
                error = "至少勾选一个要修改的项目。";
                return false;
            }
            if (target.ModifyCpu)
            {
                var logical = Environment.ProcessorCount;
                if (target.ReservedThreads >= logical)
                {
                    error = "保留线程数必须小于 CPU 逻辑线程总数（" + logical + "）。";
                    return false;
                }
                var available = logical - target.ReservedThreads;
                if (available < 4)
                {
                    error = "该设置只给游戏留下 " + available + " 个线程。为避免无法运行或严重卡顿，至少需要 4 个可用线程。";
                    return false;
                }
            }
            error = null;
            return true;
        }

        private bool IsAggressiveTarget(ConfigTarget target)
        {
            if (!target.ModifyCpu) return false;
            var logical = Environment.ProcessorCount;
            var available = logical - target.ReservedThreads;
            var cpu = GetCpuName();
            if (cpu.IndexOf("275HX", StringComparison.OrdinalIgnoreCase) >= 0 && available > 8) return true;
            return logical >= 12 && available > Math.Max(8, logical * 3 / 4);
        }

        private ConfigTarget GetTarget()
        {
            return new ConfigTarget
            {
                ModifyCpu = cpuCheck.Checked,
                ModifyAudio = audioCheck.Checked,
                ReservedThreads = (int)reservedThreadsBox.Value,
                MemorySize = checked((int)memorySizeBox.Value * 1024 * 1024),
                Refills = (int)refillsBox.Value
            };
        }

        private int RecommendReservedThreads()
        {
            var threads = Environment.ProcessorCount;
            var cpu = GetCpuName();
            int recommendation;
            if (cpu.IndexOf("275HX", StringComparison.OrdinalIgnoreCase) >= 0) recommendation = 16;
            else if (threads >= 24) recommendation = 16;
            else if (threads >= 16) recommendation = 8;
            else recommendation = Math.Max(0, threads - 4);
            return Math.Min(recommendation, Math.Max(0, threads - 4));
        }

        private string GetSettingsFile()
        {
            try
            {
                var dir = gameDirBox.Text.Trim();
                if (dir.Length == 0 || !Directory.Exists(dir)) return null;
                var direct = Path.Combine(dir, "data", "settings.ini");
                if (File.Exists(direct)) return direct;
                var alternate = Path.Combine(dir, "data", "settings", "settings.ini");
                return File.Exists(alternate) ? alternate : null;
            }
            catch (Exception ex)
            {
                lastErrorMessage = ex.Message;
                return null;
            }
        }

        private void SetStatus(string text, StatusLevel level)
        {
            var color = level == StatusLevel.Success ? okColor : (level == StatusLevel.Warning ? cautionColor : errorColor);
            statusLabel.Text = text;
            statusLabel.ForeColor = color;
            statusDot.BackColor = color;
        }

        private void Warn(string message)
        {
            SetStatus("需要处理", StatusLevel.Warning);
            Log(message);
            MessageBox.Show(this, message, "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private void Log(string message)
        {
            logLines.Add("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + message);
            while (logLines.Count > 100) logLines.RemoveAt(0);
            logBox.Lines = logLines.ToArray();
            logBox.SelectionStart = logBox.TextLength;
            logBox.ScrollToCaret();
        }

        private void SafeUi(Action action, string operation)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                lastErrorMessage = ex.Message;
                SetStatus(operation + "失败", StatusLevel.Error);
                Log(operation + "失败：" + FirstLine(ex.Message));
                MessageBox.Show(this, ex.Message, operation + "失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void SetReservedValue(int value)
        {
            SetNumericValue(reservedThreadsBox, value);
        }

        private static void SetNumericValue(NumericUpDown control, int value)
        {
            var bounded = Math.Max((int)control.Minimum, Math.Min((int)control.Maximum, value));
            control.Value = bounded;
        }

        private Panel MakeCard()
        {
            return new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = cardColor,
                BorderStyle = BorderStyle.FixedSingle
            };
        }

        private Label MakeTitle(string text)
        {
            return new Label
            {
                Text = text,
                Dock = DockStyle.Fill,
                Font = CreateUiFont(10.5F, FontStyle.Bold),
                ForeColor = textColor,
                TextAlign = ContentAlignment.MiddleLeft
            };
        }

        private Label MakeFieldLabel(string text)
        {
            return new Label
            {
                Text = text,
                Dock = DockStyle.Fill,
                ForeColor = mutedColor,
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = false,
                Padding = new Padding(0, 0, 8, 0)
            };
        }

        private static void PrepareCommandButton(Button button, int column, int row)
        {
            button.Dock = DockStyle.Fill;
            button.Margin = new Padding(
                column == 0 ? 0 : 4,
                row == 0 ? 1 : 4,
                column == LayoutCommandColumns - 1 ? 0 : 4,
                row == LayoutCommandRows - 1 ? 1 : 4);
        }

        private Button MakeButton(string text, bool primary)
        {
            var button = new ActionButton
            {
                Text = text,
                Height = 35,
                MinimumSize = new Size(0, 35),
                AutoSize = false,
                AutoEllipsis = true,
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand,
                Margin = new Padding(0, 1, 8, 1),
                Padding = new Padding(2, 1, 2, 1),
                TextAlign = ContentAlignment.MiddleCenter,
                Font = CreateButtonFont(9F, primary ? FontStyle.Bold : FontStyle.Regular)
            };
            if (primary)
            {
                button.BackColor = accentColor;
                button.ForeColor = Color.White;
                button.FlatAppearance.BorderSize = 0;
            }
            else
            {
                button.BackColor = Color.FromArgb(250, 252, 255);
                button.ForeColor = textColor;
                button.FlatAppearance.BorderColor = Color.FromArgb(204, 213, 226);
            }
            return button;
        }

        private static Font CreateUiFont(float size, FontStyle style)
        {
            try
            {
                return new Font("Segoe UI Variable Text", size, style);
            }
            catch
            {
                return new Font("Microsoft YaHei UI", size, style);
            }
        }

        private string GetHeroImagePath()
        {
            try
            {
                var exeDir = AppDomain.CurrentDomain.BaseDirectory;
                var localAsset = Path.Combine(exeDir, "assets", "library_hero.jpg");
                if (File.Exists(localAsset)) return localAsset;
                var desktopAsset = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                    "地狱潜兵修改CPU",
                    "assets",
                    "library_hero.jpg");
                return File.Exists(desktopAsset) ? desktopAsset : "";
            }
            catch
            {
                return "";
            }
        }

        private static string FindGameDir()
        {
            try
            {
                var steam = GetSteamPath();
                if (string.IsNullOrEmpty(steam)) return null;
                var libraries = new List<string>();
                libraries.Add(steam);
                var libraryFile = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
                if (File.Exists(libraryFile))
                {
                    var text = ConfigFileOperations.ReadText(libraryFile);
                    foreach (Match match in Regex.Matches(text, "\"path\"\\s+\"([^\"]+)\""))
                    {
                        var path = match.Groups[1].Value.Replace(@"\\", @"\");
                        if (Directory.Exists(path) && !libraries.Contains(path)) libraries.Add(path);
                    }
                }
                foreach (var library in libraries)
                {
                    var manifest = Path.Combine(library, "steamapps", "appmanifest_" + AppConstants.AppId + ".acf");
                    if (!File.Exists(manifest)) continue;
                    var manifestText = ConfigFileOperations.ReadText(manifest);
                    var installDir = "Helldivers 2";
                    var match = Regex.Match(manifestText, "\"installdir\"\\s+\"([^\"]+)\"");
                    if (match.Success) installDir = match.Groups[1].Value;
                    var candidate = Path.Combine(library, "steamapps", "common", installDir);
                    if (Directory.Exists(candidate)) return candidate;
                }
            }
            catch
            {
            }
            return null;
        }

        private static string GetSteamPath()
        {
            var path = ReadSteamRegistry(Registry.CurrentUser, @"Software\Valve\Steam");
            if (!string.IsNullOrEmpty(path)) return path;
            path = ReadSteamRegistry(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam");
            if (!string.IsNullOrEmpty(path)) return path;
            return ReadSteamRegistry(Registry.LocalMachine, @"SOFTWARE\Valve\Steam");
        }

        private static string ReadSteamRegistry(RegistryKey root, string subKey)
        {
            try
            {
                using (var key = root.OpenSubKey(subKey))
                {
                    if (key == null) return null;
                    var steamPath = key.GetValue("SteamPath") as string;
                    if (!string.IsNullOrEmpty(steamPath) && Directory.Exists(steamPath)) return steamPath;
                    var installPath = key.GetValue("InstallPath") as string;
                    if (!string.IsNullOrEmpty(installPath) && Directory.Exists(installPath)) return installPath;
                }
            }
            catch
            {
            }
            return null;
        }

        private static string GetCpuName()
        {
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
                {
                    if (key == null) return "";
                    var value = key.GetValue("ProcessorNameString") as string;
                    return string.IsNullOrEmpty(value) ? "" : value.Trim();
                }
            }
            catch
            {
                return "";
            }
        }

        private static string TargetToText(ConfigTarget target)
        {
            var values = new List<string>();
            if (target.ModifyCpu) values.Add("num_reserved_threads=" + target.ReservedThreads);
            if (target.ModifyAudio)
            {
                values.Add("memory_size=" + target.MemorySize);
                values.Add("num_refills_in_voice=" + target.Refills);
            }
            return values.Count == 0 ? "未启用修改项" : string.Join(", ", values.ToArray());
        }

        private static string RedactPath(string path)
        {
            return RedactDiagnosticText(path);
        }

        internal static string RedactDiagnosticText(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var redacted = string.IsNullOrEmpty(profile)
                ? text
                : Regex.Replace(text, Regex.Escape(profile), "%USERPROFILE%", RegexOptions.IgnoreCase);
            var userName = Environment.UserName;
            if (!string.IsNullOrEmpty(userName))
            {
                redacted = Regex.Replace(
                    redacted,
                    @"(?<=\\|/)" + Regex.Escape(userName) + @"(?=\\|/|$)",
                    "%USERNAME%",
                    RegexOptions.IgnoreCase);
            }
            return redacted;
        }

        private static Font CreateButtonFont(float size, FontStyle style)
        {
            try
            {
                return new Font("Microsoft YaHei UI", size, style);
            }
            catch
            {
                return CreateUiFont(size, style);
            }
        }

        private static string EstimatedAvailable(int? reserved)
        {
            return reserved.HasValue ? Math.Max(0, Environment.ProcessorCount - reserved.Value).ToString() : "未找到";
        }

        private static string ShowValue(int? value)
        {
            return value.HasValue ? value.Value.ToString() : "未找到";
        }

        private static string ShowMb(int? bytes)
        {
            return bytes.HasValue ? ToMb(bytes.Value) : "未找到";
        }

        private static string ToMb(int bytes)
        {
            var mb = Math.Round((decimal)bytes / 1024m / 1024m, 1);
            return mb.ToString("0.#") + " MB";
        }

        private static string TrimText(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength) return value;
            return value.Substring(0, maxLength - 3) + "...";
        }

        private static string FirstLine(string value)
        {
            if (string.IsNullOrEmpty(value)) return "未知错误";
            var index = value.IndexOfAny(new[] { '\r', '\n' });
            return index < 0 ? value : value.Substring(0, index);
        }

        private static int Clamp(int value, int minimum, int maximum)
        {
            return Math.Max(minimum, Math.Min(maximum, value));
        }
    }

    internal sealed class ActionButton : Button
    {
        internal ActionButton()
        {
            SetStyle(
                ControlStyles.UserPaint |
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer,
                true);
            DoubleBuffered = true;
            UseVisualStyleBackColor = false;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var bounds = ClientRectangle;
            var background = Enabled ? BackColor : Color.FromArgb(235, 238, 242);
            using (var brush = new SolidBrush(background))
            {
                e.Graphics.FillRectangle(brush, bounds);
            }

            if (FlatAppearance.BorderSize > 0)
            {
                var borderColor = FlatAppearance.BorderColor.IsEmpty
                    ? Color.FromArgb(204, 213, 226)
                    : FlatAppearance.BorderColor;
                using (var pen = new Pen(borderColor, FlatAppearance.BorderSize))
                {
                    var border = bounds;
                    border.Width--;
                    border.Height--;
                    e.Graphics.DrawRectangle(pen, border);
                }
            }

            var textBounds = Rectangle.FromLTRB(
                Padding.Left,
                Padding.Top,
                Math.Max(Padding.Left, bounds.Right - Padding.Right),
                Math.Max(Padding.Top, bounds.Bottom - Padding.Bottom));
            TextRenderer.DrawText(
                e.Graphics,
                Text,
                Font,
                textBounds,
                Enabled ? ForeColor : Color.FromArgb(130, 138, 148),
                TextFormatFlags.HorizontalCenter |
                TextFormatFlags.VerticalCenter |
                TextFormatFlags.SingleLine |
                TextFormatFlags.EndEllipsis);

            if (Focused)
            {
                ControlPaint.DrawFocusRectangle(e.Graphics, textBounds);
            }
        }
    }

    internal sealed class BannerPanel : Panel
    {
        private Image heroImage;

        internal BannerPanel(string imagePath)
        {
            DoubleBuffered = true;
            BackColor = Color.FromArgb(22, 26, 32);
            if (!string.IsNullOrEmpty(imagePath) && File.Exists(imagePath))
            {
                try
                {
                    using (var stream = File.OpenRead(imagePath))
                    using (var loaded = Image.FromStream(stream))
                    {
                        heroImage = new Bitmap(loaded);
                    }
                }
                catch
                {
                    heroImage = null;
                }
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.HighQuality;
            e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            if (heroImage != null)
            {
                var dest = ClientRectangle;
                var scale = Math.Max((float)dest.Width / heroImage.Width, (float)dest.Height / heroImage.Height);
                var width = heroImage.Width * scale;
                var height = heroImage.Height * scale;
                e.Graphics.DrawImage(heroImage, (dest.Width - width) / 2f, (dest.Height - height) / 2f, width, height);
            }
            using (var brush = new LinearGradientBrush(
                ClientRectangle,
                Color.FromArgb(220, 11, 14, 18),
                Color.FromArgb(112, 11, 14, 18),
                LinearGradientMode.Horizontal))
            {
                e.Graphics.FillRectangle(brush, ClientRectangle);
            }
            using (var pen = new Pen(Color.FromArgb(70, 255, 214, 88)))
            {
                e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && heroImage != null)
            {
                heroImage.Dispose();
                heroImage = null;
            }
            base.Dispose(disposing);
        }
    }
}
