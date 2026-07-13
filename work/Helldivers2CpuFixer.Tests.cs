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

            var stable = Target(16, 78643200, 4);
            var stableText = ConfigFileOperations.BuildModifiedText(defaultText, stable);
            var apply = ConfigFileOperations.ApplySafely(ini, stableText, stable, false, null);
            Assert(File.Exists(apply.BackupPath), "修改前创建精确时间备份");
            AssertSnapshot(ini, 16, 78643200, 4, "临时文件校验后安全替换并再次校验");
            Assert(!ConfigFileOperations.IsReadOnly(ini), "普通文件修改后保持原始可写属性");

            var backupCount = ConfigFileOperations.GetBackups(ini).Length;
            var unchanged = ConfigFileOperations.BuildModifiedText(ConfigFileOperations.ReadText(ini), stable);
            Assert(unchanged == ConfigFileOperations.ReadText(ini), "内容相同时可跳过写入和重复备份");
            Assert(ConfigFileOperations.GetBackups(ini).Length == backupCount, "内容未变化时备份数量不增加");

            ConfigFileOperations.SetReadOnly(ini, true);
            var tenThreads = Target(Math.Max(0, Environment.ProcessorCount - 10), 78643200, 4);
            var tenText = ConfigFileOperations.BuildModifiedText(ConfigFileOperations.ReadText(ini), tenThreads);
            ConfigFileOperations.ApplySafely(ini, tenText, tenThreads, false, null);
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
                    stableText,
                    stable,
                    false,
                    delegate(string checkpoint)
                    {
                        if (checkpoint == "after_replace") throw new IOException("测试注入：替换后失败");
                    });
            }
            catch (InvalidOperationException ex)
            {
                rollbackRaised = ex.Message.Contains("失败步骤") && ex.Message.Contains("已自动恢复修改前备份");
            }
            Assert(rollbackRaised, "写入失败提示具体步骤并报告自动回滚");
            Assert(File.ReadAllText(ini, Encoding.UTF8) == beforeRollback, "替换后失败自动恢复原配置内容");
            Assert(!ConfigFileOperations.IsReadOnly(ini), "回滚后恢复原始文件属性");
            Assert(Directory.GetFiles(dataDir, ".settings.ini.*.tmp").Length == 0, "失败后清理全部临时文件");

            ConfigFileOperations.ApplySafely(ini, stableText, stable, true, null);
            Assert(ConfigFileOperations.IsReadOnly(ini), "成功校验后按选项锁定配置文件");
            ConfigFileOperations.SetReadOnly(ini, false);
            Assert(!ConfigFileOperations.IsReadOnly(ini), "一键解除只读底层操作有效");

            var restoreSource = ConfigFileOperations.GetBackups(ini)[0];
            var expectedRestore = ConfigFileOperations.ReadSnapshot(restoreSource);
            ConfigFileOperations.SetReadOnly(ini, true);
            ConfigFileOperations.RestoreBackupSafely(restoreSource, ini, null);
            var restored = ConfigFileOperations.ReadSnapshot(ini);
            AssertSame(expectedRestore, restored, "恢复备份后校验关键参数");
            Assert(ConfigFileOperations.IsReadOnly(ini), "恢复备份后恢复目标文件原始只读属性");
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

            var missingFileRaised = false;
            try
            {
                ConfigFileOperations.ApplySafely(Path.Combine(dataDir, "not-found.ini"), stableText, stable, false, null);
            }
            catch (FileNotFoundException)
            {
                missingFileRaised = true;
            }
            Assert(missingFileRaised, "配置文件不存在时安全停止");
        }

        private static void RunStaticContractTests(string root)
        {
            Assert(AppConstants.Version == "1.6.0", "工具版本为 v1.6.0");
            Assert(AppConstants.SteamLaunchUri == "steam://rungameid/553850", "启动按钮使用正确 Steam URI");
            ProcessStartInfo capturedStartInfo = null;
            GameLauncher.Launch(delegate(ProcessStartInfo info) { capturedStartInfo = info; });
            Assert(
                capturedStartInfo != null && capturedStartInfo.FileName == AppConstants.SteamLaunchUri && capturedStartInfo.UseShellExecute,
                "启动游戏动作通过系统协议处理器调用 Steam URI");
            Assert(Environment.ProcessorCount >= 1, "可读取 CPU 逻辑线程数");
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
                var target = Target(16, 78643200, 4);
                var oldText = File.ReadAllText(ini, Encoding.UTF8);
                ConfigFileOperations.ApplySafely(
                    ini,
                    ConfigFileOperations.BuildModifiedText(oldText, target),
                    target,
                    false,
                    null);
                Console.WriteLine("RESULT FAIL permission-write-succeeded");
                return 1;
            }
            catch (Exception ex)
            {
                Console.WriteLine("RESULT PASS permission-denied");
                Console.WriteLine(ex.Message);
                return 0;
            }
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
