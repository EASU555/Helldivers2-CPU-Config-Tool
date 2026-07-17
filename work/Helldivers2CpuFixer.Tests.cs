using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Helldivers2CpuFixer.Tests
{
    internal static class Program
    {
        private static readonly List<string> results = new List<string>();

        private static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--process-only")
            {
                var running = MainForm.IsGameRunning();
                Console.WriteLine(running ? "RESULT PASS game-running" : "RESULT FAIL game-not-running");
                return running ? 0 : 1;
            }
            if (args.Length > 1 && args[0] == "--permission-only")
            {
                return RunPermissionTest(args[1]);
            }
            var root = args.Length > 0
                ? Path.GetFullPath(args[0])
                : Path.Combine(Path.GetTempPath(), "HD2CpuFixer_v16_tests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                RunFileOperationTests(root);
                RunStaticContractTests(root);
                foreach (var result in results) Console.WriteLine("PASS " + result);
                Console.WriteLine("RESULT PASS (" + results.Count + " checks)");
                return 0;
            }
            catch (Exception ex)
            {
                foreach (var result in results) Console.WriteLine("PASS " + result);
                Console.WriteLine("RESULT FAIL");
                Console.WriteLine(ex.ToString());
                return 1;
            }
        }

        private static void RunFileOperationTests(string root)
        {
            var gameDir = Path.Combine(root, "Helldivers 2");
            var dataDir = Path.Combine(gameDir, "data");
            Directory.CreateDirectory(dataDir);
            var ini = Path.Combine(dataDir, "settings.ini");
            var defaultText = MakeText(2, 26214400, 2);
            File.WriteAllText(ini, defaultText, new UTF8Encoding(false));

            var stable = Target(Math.Min(16, Math.Max(0, Environment.ProcessorCount - 4)), 78643200, 4);
            var stableText = ConfigFileOperations.BuildModifiedText(defaultText, stable);
            var apply = ConfigFileOperations.ApplySafely(ini, defaultText, stableText, stable, false, null);
            Assert(File.Exists(apply.BackupPath), "修改前创建精确时间备份");
            Assert(File.Exists(apply.BackupPath + ".integrity"), "新备份创建完整性元数据");
            Assert(File.ReadAllText(ini, Encoding.UTF8) == stableText, "应用后整个配置文件与已校验内容完全一致");
            AssertSnapshot(ini, stable.ReservedThreads, 78643200, 4, "临时文件校验后安全替换并再次校验");
            Assert(!ConfigFileOperations.IsReadOnly(ini), "普通文件修改后保持原始可写属性");

            var backupCount = ConfigFileOperations.GetBackups(ini).Length;
            var unchangedOriginal = ConfigFileOperations.ReadText(ini);
            var unchanged = ConfigFileOperations.BuildModifiedText(unchangedOriginal, stable);
            var unchangedResult = ConfigFileOperations.ApplySafely(
                ini,
                unchangedOriginal,
                unchanged,
                stable,
                false,
                null);
            Assert(!unchangedResult.Changed, "内容相同时底层流程跳过写入");
            Assert(ConfigFileOperations.GetBackups(ini).Length == backupCount, "内容未变化时备份数量不增加");

            ConfigFileOperations.SetReadOnly(ini, true);
            var tenThreads = Target(Math.Max(0, Environment.ProcessorCount - 10), 78643200, 4);
            var tenOriginal = ConfigFileOperations.ReadText(ini);
            var tenText = ConfigFileOperations.BuildModifiedText(tenOriginal, tenThreads);
            ConfigFileOperations.ApplySafely(ini, tenOriginal, tenText, tenThreads, false, null);
            Assert(ConfigFileOperations.IsReadOnly(ini), "只读文件写入后在 finally 恢复原始只读属性");
            AssertSnapshot(ini, tenThreads.ReservedThreads, 78643200, 4, "只读配置可安全修改并校验");

            ConfigFileOperations.SetReadOnly(ini, false);
            File.WriteAllText(ini, defaultText, new UTF8Encoding(false));
            var beforeRollback = File.ReadAllText(ini, Encoding.UTF8);
            var rollbackRaised = false;
            try
            {
                ConfigFileOperations.ApplySafely(
                    ini,
                    beforeRollback,
                    stableText,
                    stable,
                    false,
                    delegate(string checkpoint)
                    {
                        if (checkpoint == "after_replace")
                        {
                            File.AppendAllText(ini, "unrelated_setting = tampered\r\n", new UTF8Encoding(false));
                        }
                    });
            }
            catch (InvalidOperationException ex)
            {
                rollbackRaised = ex.Message.Contains("失败步骤") && ex.Message.Contains("已自动恢复修改前备份");
            }
            Assert(rollbackRaised, "替换后整文件校验失败时报告自动回滚");
            Assert(File.ReadAllText(ini, Encoding.UTF8) == beforeRollback, "替换后文件被改动时自动恢复原配置内容");
            Assert(!ConfigFileOperations.IsReadOnly(ini), "回滚后恢复原始文件属性");
            Assert(Directory.GetFiles(dataDir, ".settings.ini.*.tmp").Length == 0, "失败后清理全部临时文件");

            var beforeLock = ConfigFileOperations.ReadText(ini);
            ConfigFileOperations.ApplySafely(ini, beforeLock, stableText, stable, true, null);
            Assert(ConfigFileOperations.IsReadOnly(ini), "成功校验后按选项锁定配置文件");
            ConfigFileOperations.SetReadOnly(ini, false);
            Assert(!ConfigFileOperations.IsReadOnly(ini), "一键解除只读底层操作有效");

            var restoreSource = ConfigFileOperations.GetBackups(ini)[0];
            var expectedRestore = ConfigFileOperations.ReadSnapshot(restoreSource);
            ConfigFileOperations.SetReadOnly(ini, true);
            var beforeRestore = File.ReadAllText(ini, Encoding.UTF8);
            var restoreResult = ConfigFileOperations.RestoreBackupSafely(restoreSource, ini, null);
            var restored = ConfigFileOperations.ReadSnapshot(ini);
            AssertSame(expectedRestore, restored, "恢复备份后校验关键参数");
            Assert(
                File.ReadAllText(ini, Encoding.UTF8) == File.ReadAllText(restoreSource, Encoding.UTF8),
                "恢复后整个配置文件与备份完全一致");
            Assert(ConfigFileOperations.IsReadOnly(ini), "恢复备份后恢复目标文件原始只读属性");
            Assert(
                File.Exists(restoreResult.PreRestoreBackupPath) &&
                File.ReadAllText(restoreResult.PreRestoreBackupPath, Encoding.UTF8) == beforeRestore,
                "成功恢复永久保留恢复前完整配置");
            Assert(
                File.Exists(restoreResult.PreRestoreBackupPath + ".integrity"),
                "恢复前永久备份带完整性元数据");
            Assert(Directory.GetFiles(dataDir, ".settings.ini.*.tmp").Length == 0, "恢复成功后清理全部临时文件");

            ConfigFileOperations.SetReadOnly(ini, false);
            File.WriteAllText(ini, stableText, new UTF8Encoding(false));
            var restoreFailureOriginal = File.ReadAllText(ini, Encoding.UTF8);
            var restoreFailureRaised = false;
            try
            {
                ConfigFileOperations.RestoreBackupSafely(
                    restoreSource,
                    ini,
                    delegate(string checkpoint)
                    {
                        if (checkpoint == "after_restore_replace") throw new IOException("测试注入：恢复替换后失败");
                    });
            }
            catch (InvalidOperationException ex)
            {
                restoreFailureRaised = ex.Message.Contains("失败步骤") && ex.Message.Contains("已恢复到执行恢复前的配置");
            }
            Assert(restoreFailureRaised, "恢复替换后失败时报告保护副本回滚");
            Assert(File.ReadAllText(ini, Encoding.UTF8) == restoreFailureOriginal, "恢复失败时回到操作前配置，不继续覆盖");
            Assert(Directory.GetFiles(dataDir, ".settings.ini.*.tmp").Length == 0, "恢复失败后清理保护副本和临时文件");

            ConfigFileOperations.SetReadOnly(ini, false);
            var staleOriginal = defaultText + "unrelated_setting = before_confirmation\r\n";
            File.WriteAllText(ini, staleOriginal, new UTF8Encoding(false));
            var staleTargetText = ConfigFileOperations.BuildModifiedText(staleOriginal, stable);
            var backupsBeforeStaleAttempt = ConfigFileOperations.GetBackups(ini).Length;
            File.WriteAllText(
                ini,
                defaultText + "unrelated_setting = changed_during_confirmation\r\n",
                new UTF8Encoding(false));
            var staleRejected = false;
            try
            {
                ConfigFileOperations.ApplySafely(
                    ini,
                    staleOriginal,
                    staleTargetText,
                    stable,
                    false,
                    null);
            }
            catch (InvalidOperationException ex)
            {
                staleRejected = ex.Message.Contains("其他程序修改");
            }
            Assert(staleRejected, "确认期间配置变化时拒绝旧快照写入");
            Assert(
                File.ReadAllText(ini, Encoding.UTF8).Contains("changed_during_confirmation"),
                "拒绝旧快照后保留并发写入的新设置");
            Assert(
                ConfigFileOperations.GetBackups(ini).Length == backupsBeforeStaleAttempt,
                "旧快照被拒绝时不留下冗余备份");

            var duplicateOriginal = defaultText + "num_reserved_threads = 7\r\n";
            File.WriteAllText(ini, duplicateOriginal, new UTF8Encoding(false));
            var duplicateTargetText = ConfigFileOperations.BuildModifiedText(duplicateOriginal, stable);
            var backupsBeforeDuplicateAttempt = ConfigFileOperations.GetBackups(ini).Length;
            var duplicateRejected = false;
            try
            {
                ConfigFileOperations.ApplySafely(
                    ini,
                    duplicateOriginal,
                    duplicateTargetText,
                    stable,
                    false,
                    null);
            }
            catch (InvalidDataException ex)
            {
                duplicateRejected = ex.Message.Contains("重复字段");
            }
            Assert(duplicateRejected, "重复必要字段时拒绝写入");
            Assert(File.ReadAllText(ini, Encoding.UTF8) == duplicateOriginal, "拒绝重复字段后保持配置不变");
            Assert(
                ConfigFileOperations.GetBackups(ini).Length == backupsBeforeDuplicateAttempt,
                "重复字段被拒绝时不创建备份");

            File.WriteAllText(ini, defaultText, new UTF8Encoding(false));
            var invalidAudio = new ConfigTarget
            {
                ModifyAudio = true,
                MemorySize = 0,
                Refills = 0
            };
            var invalidAudioText = ConfigFileOperations.BuildModifiedText(defaultText, invalidAudio);
            var invalidTargetRejected = false;
            try
            {
                ConfigFileOperations.ApplySafely(
                    ini,
                    defaultText,
                    invalidAudioText,
                    invalidAudio,
                    false,
                    null);
            }
            catch (ArgumentOutOfRangeException)
            {
                invalidTargetRejected = true;
            }
            Assert(invalidTargetRejected, "底层写入接口拒绝越界音频参数");
            Assert(File.ReadAllText(ini, Encoding.UTF8) == defaultText, "越界参数被拒绝时不修改配置");

            var oversizedPath = Path.Combine(dataDir, "oversized.ini");
            using (var oversized = new FileStream(oversizedPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                oversized.SetLength(ConfigFileOperations.MaxConfigFileBytes + 1);
            }
            var oversizedRejected = false;
            try
            {
                ConfigFileOperations.ReadText(oversizedPath);
            }
            catch (InvalidDataException ex)
            {
                oversizedRejected = ex.Message.Contains("16 MB");
            }
            Assert(oversizedRejected, "超过安全上限的配置文件被拒绝读取");

            File.WriteAllText(ini, defaultText, new UTF8Encoding(false));
            var backupsBeforeInjectedFailures = ConfigFileOperations.GetBackups(ini).Length;
            for (var failureIndex = 0; failureIndex < 12; failureIndex++)
            {
                try
                {
                    ConfigFileOperations.ApplySafely(
                        ini,
                        defaultText,
                        stableText,
                        stable,
                        false,
                        delegate(string checkpoint)
                        {
                            if (checkpoint == "after_backup") throw new IOException("测试注入：备份后失败");
                        });
                }
                catch (InvalidOperationException)
                {
                }
            }
            Assert(
                ConfigFileOperations.GetBackups(ini).Length == backupsBeforeInjectedFailures,
                "替换前重复失败不会无限堆积备份");

            var partialBackup = ini + ".bak_29990101_000000_000_partial";
            File.WriteAllText(partialBackup, "num_reserved_threads = 2\r\n", new UTF8Encoding(false));
            var beforePartialRestore = File.ReadAllText(ini, Encoding.UTF8);
            var partialRejected = false;
            try
            {
                ConfigFileOperations.RestoreBackupSafely(partialBackup, ini, null);
            }
            catch (InvalidDataException)
            {
                partialRejected = true;
            }
            Assert(partialRejected, "截断备份缺少必要参数时拒绝恢复");
            Assert(
                File.ReadAllText(ini, Encoding.UTF8) == beforePartialRestore,
                "拒绝截断备份后保持当前配置不变");

            var duplicateBackup = ini + ".bak_29990101_000001_000_duplicate";
            File.WriteAllText(
                duplicateBackup,
                defaultText + "memory_size = 78643200\r\n",
                new UTF8Encoding(false));
            var beforeDuplicateRestore = File.ReadAllText(ini, Encoding.UTF8);
            var duplicateBackupRejected = false;
            try
            {
                ConfigFileOperations.RestoreBackupSafely(duplicateBackup, ini, null);
            }
            catch (InvalidDataException ex)
            {
                duplicateBackupRejected = ex.Message.Contains("重复字段");
            }
            Assert(duplicateBackupRejected, "包含重复必要字段的备份被拒绝恢复");
            Assert(File.ReadAllText(ini, Encoding.UTF8) == beforeDuplicateRestore, "拒绝歧义备份后保持当前配置不变");

            var unsafeBackup = ini + ".bak_29990101_000002_000_unsafe";
            File.WriteAllText(unsafeBackup, MakeText(2, 0, 2), new UTF8Encoding(false));
            var unsafeBackupRejected = false;
            try
            {
                ConfigFileOperations.RestoreBackupSafely(unsafeBackup, ini, null);
            }
            catch (InvalidDataException ex)
            {
                unsafeBackupRejected = ex.Message.Contains("安全范围");
            }
            Assert(unsafeBackupRejected, "参数越界的备份被拒绝恢复");
            Assert(File.ReadAllText(ini, Encoding.UTF8) == beforeDuplicateRestore, "拒绝越界备份后保持当前配置不变");

            File.AppendAllText(apply.BackupPath, "# tampered\r\n", new UTF8Encoding(false));
            var tamperedRejected = false;
            try
            {
                ConfigFileOperations.RestoreBackupSafely(apply.BackupPath, ini, null);
            }
            catch (InvalidDataException)
            {
                tamperedRejected = true;
            }
            Assert(tamperedRejected, "带完整性元数据的备份被篡改后拒绝恢复");

            var legacyBackup = ini + ".bak_29980101_000000_000_legacy";
            File.WriteAllText(legacyBackup, defaultText, new UTF8Encoding(false));
            var legacyResult = ConfigFileOperations.RestoreBackupSafely(legacyBackup, ini, null);
            Assert(
                legacyResult.LegacyBackupWithoutIntegrityMetadata,
                "旧版完整备份无元数据时保持兼容并明确标记");
            Assert(
                File.ReadAllText(ini, Encoding.UTF8) == defaultText,
                "旧版完整备份恢复后执行整文件校验");

            var missingBackupRaised = false;
            try
            {
                ConfigFileOperations.RestoreBackupSafely(Path.Combine(dataDir, "missing.bak"), ini, null);
            }
            catch (FileNotFoundException)
            {
                missingBackupRaised = true;
            }
            Assert(missingBackupRaised, "恢复前检查备份是否存在");

            ConfigFileOperations.SetReadOnly(ini, false);
            for (var i = 0; i < 12; i++)
            {
                File.WriteAllText(ini + ".bak_20990101_0000" + i.ToString("00") + "_000", defaultText);
            }
            ConfigFileOperations.PruneBackups(ini, 10);
            Assert(ConfigFileOperations.GetBackups(ini).Length == 10, "自动备份只保留最近 10 份");
            Assert(
                Directory.GetFiles(dataDir, "*.integrity").Length == 0,
                "删除旧备份时同时清理完整性元数据");

            var missingFileRaised = false;
            try
            {
                ConfigFileOperations.ApplySafely(
                    Path.Combine(dataDir, "not-found.ini"),
                    stableText,
                    stableText,
                    stable,
                    false,
                    null);
            }
            catch (FileNotFoundException)
            {
                missingFileRaised = true;
            }
            Assert(missingFileRaised, "配置文件不存在时安全停止");
        }

        private static void RunStaticContractTests(string root)
        {
            Assert(AppConstants.Version == "1.6.10", "工具版本为 v1.6.10");
            Assert(
                MainForm.LayoutPlanLabelWidth >= 185 &&
                MainForm.LayoutBannerAreaHeight >= 138 &&
                MainForm.LayoutPathAreaHeight >= 116 &&
                MainForm.LayoutMainAreaHeight >= 410 &&
                MainForm.LayoutPlanSummaryHeight >= 60 &&
                MainForm.LayoutPlanWarningHeight >= 42 &&
                MainForm.LayoutCommandAreaHeight == 128 &&
                MainForm.LayoutCommandButtonsHeight == 80 &&
                MainForm.LayoutCommandColumns == 3 &&
                MainForm.LayoutCommandRows == 2,
                "高 DPI 布局为横幅、路径输入和中文按钮保留最低可用高度");
            Assert(AppConstants.SteamLaunchUri == "steam://rungameid/553850", "启动按钮使用正确 Steam URI");
            ProcessStartInfo capturedStartInfo = null;
            GameLauncher.Launch(delegate(ProcessStartInfo info) { capturedStartInfo = info; });
            Assert(
                capturedStartInfo != null && capturedStartInfo.FileName == AppConstants.SteamLaunchUri && capturedStartInfo.UseShellExecute,
                "启动游戏动作通过系统协议处理器调用 Steam URI");
            Assert(Environment.ProcessorCount >= 1, "可读取 CPU 逻辑线程数");
            string validationError;
            Assert(
                !MainForm.ValidateTarget(new ConfigTarget(), out validationError) &&
                validationError.Contains("至少勾选"),
                "未选择修改项时目标验证失败");
            var invalidAudioTarget = new ConfigTarget { ModifyAudio = true, MemorySize = 0, Refills = 0 };
            Assert(
                !MainForm.ValidateTarget(invalidAudioTarget, out validationError) &&
                validationError.Contains("音频缓存"),
                "音频参数越界时目标验证失败");
            var libraryRoot = Path.Combine(root, "SteamLibrary");
            var normalInstall = MainForm.CombineSteamInstallPath(libraryRoot, "Helldivers 2");
            Assert(
                normalInstall == Path.GetFullPath(Path.Combine(libraryRoot, "steamapps", "common", "Helldivers 2")),
                "Steam 安装目录在库目录内时可解析");
            Assert(
                MainForm.CombineSteamInstallPath(libraryRoot, @"..\..\outside") == null &&
                MainForm.CombineSteamInstallPath(libraryRoot, Path.GetPathRoot(libraryRoot)) == null,
                "Steam 清单路径不能逃出库目录");
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(profile))
            {
                var redacted = MainForm.RedactDiagnosticText(
                    profile.ToUpperInvariant() + @"\private\settings.ini");
                Assert(
                    redacted.Contains("%USERPROFILE%") && !redacted.Contains(Environment.UserName),
                    "用户目录脱敏不受路径大小写影响");
            }
            var broken = UserSettingsStore.Parse(new[]
            {
                "PlanIndex=999",
                "ReservedThreads=not-a-number",
                "MemoryMb=-4",
                "Refills=999",
                "ModifyCpu=broken"
            });
            Assert(
                broken.PlanIndex == 0 && broken.ReservedThreads == 16 && broken.MemoryMb == 75 && broken.Refills == 4 && broken.ModifyCpu,
                "本地设置损坏时回退到安全默认值");

            var settingsPath = Path.Combine(root, "local-settings", "user-settings.ini");
            var saved = new UserSettings
            {
                GameDirectory = @"D:\SteamLibrary\steamapps\common\Helldivers 2",
                PlanIndex = 6,
                ReservedThreads = 14,
                MemoryMb = 80,
                Refills = 5,
                ModifyCpu = true,
                ModifyAudio = false,
                LockAfterApply = true
            };
            UserSettingsStore.SaveToPath(saved, settingsPath);
            var loaded = UserSettingsStore.LoadFromPath(settingsPath);
            Assert(
                loaded.GameDirectory == saved.GameDirectory && loaded.PlanIndex == 6 && loaded.ReservedThreads == 14 &&
                loaded.MemoryMb == 80 && loaded.Refills == 5 && loaded.ModifyCpu && !loaded.ModifyAudio && loaded.LockAfterApply,
                "上次目录、方案、自定义参数和勾选状态可保存并读回");
            File.WriteAllText(settingsPath, "PlanIndex=broken\r\nMemoryMb=broken", new UTF8Encoding(false));
            var damagedFile = UserSettingsStore.LoadFromPath(settingsPath);
            Assert(damagedFile.PlanIndex == 0 && damagedFile.MemoryMb == 75, "损坏的本地设置文件不会导致启动失败");
        }

        private static int RunPermissionTest(string ini)
        {
            try
            {
                var target = Target(Math.Min(16, Math.Max(0, Environment.ProcessorCount - 4)), 78643200, 4);
                var oldText = File.ReadAllText(ini, Encoding.UTF8);
                ConfigFileOperations.ApplySafely(
                    ini,
                    oldText,
                    ConfigFileOperations.BuildModifiedText(oldText, target),
                    target,
                    false,
                    null);
                Console.WriteLine("RESULT FAIL permission-write-succeeded");
                return 1;
            }
            catch (Exception ex)
            {
                if (ContainsException<UnauthorizedAccessException>(ex))
                {
                    Console.WriteLine("RESULT PASS permission-denied");
                    Console.WriteLine(ex.Message);
                    return 0;
                }
                Console.WriteLine("RESULT FAIL unexpected-exception");
                Console.WriteLine(ex.ToString());
                return 1;
            }
        }

        private static bool ContainsException<T>(Exception error) where T : Exception
        {
            for (var current = error; current != null; current = current.InnerException)
            {
                if (current is T) return true;
            }
            return false;
        }

        private static ConfigTarget Target(int threads, int memory, int refills)
        {
            return new ConfigTarget
            {
                ModifyCpu = true,
                ModifyAudio = true,
                ReservedThreads = threads,
                MemorySize = memory,
                Refills = refills
            };
        }

        private static string MakeText(int threads, int memory, int refills)
        {
            return "# integration test\r\n" +
                   "num_reserved_threads = " + threads + "\r\n" +
                   "memory_size = " + memory + "\r\n" +
                   "num_refills_in_voice = " + refills + "\r\n";
        }

        private static void AssertSnapshot(string path, int threads, int memory, int refills, string name)
        {
            var value = ConfigFileOperations.ReadSnapshot(path);
            Assert(value.ReservedThreads == threads && value.MemorySize == memory && value.Refills == refills, name);
        }

        private static void AssertSame(ConfigSnapshot expected, ConfigSnapshot actual, string name)
        {
            Assert(
                expected.ReservedThreads == actual.ReservedThreads &&
                expected.MemorySize == actual.MemorySize &&
                expected.Refills == actual.Refills,
                name);
        }

        private static void Assert(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("检查失败：" + name);
            results.Add(name);
        }
    }
}
