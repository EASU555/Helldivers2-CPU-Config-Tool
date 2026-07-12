using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Helldivers 2 CPU Config Tool")]
[assembly: AssemblyDescription("A small local settings.ini editor for Helldivers 2")]
[assembly: AssemblyCompany("Local User Tool")]
[assembly: AssemblyProduct("Helldivers 2 CPU Config Tool")]
[assembly: AssemblyCopyright("Local User")]
[assembly: AssemblyVersion("1.5.0.0")]
[assembly: AssemblyFileVersion("1.5.0.0")]

namespace Helldivers2CpuFixer
{
    static class Program
    {
        private const string AppId = "553850";

        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }

        sealed class MainForm : Form
        {
            private readonly Color pageColor = Color.FromArgb(246, 248, 251);
            private readonly Color cardColor = Color.White;
            private readonly Color textColor = Color.FromArgb(30, 38, 52);
            private readonly Color mutedColor = Color.FromArgb(92, 104, 122);
            private readonly Color accentColor = Color.FromArgb(35, 102, 218);
            private readonly Color okColor = Color.FromArgb(31, 137, 89);
            private readonly Color warnColor = Color.FromArgb(196, 77, 62);

            private readonly TextBox gameDirBox = new TextBox();
            private readonly ComboBox planBox = new ComboBox();
            private readonly NumericUpDown reservedThreadsBox = new NumericUpDown();
            private readonly NumericUpDown memorySizeBox = new NumericUpDown();
            private readonly NumericUpDown refillsBox = new NumericUpDown();
            private readonly CheckBox cpuCheck = new CheckBox();
            private readonly CheckBox audioCheck = new CheckBox();
            private readonly Label currentLabel = new Label();
            private readonly Label adviceLabel = new Label();
            private readonly Label statusLabel = new Label();
            private readonly Label statusDot = new Label();
            private readonly TextBox logBox = new TextBox();
            private readonly ToolTip tips = new ToolTip();

            private bool applyingPlan;

            public MainForm()
            {
                Text = "地狱潜兵 CPU 修改器";
                ClientSize = new Size(820, 660);
                MinimumSize = new Size(820, 660);
                MaximumSize = new Size(820, 660);
                StartPosition = FormStartPosition.CenterScreen;
                Font = new Font("Microsoft YaHei UI", 9F);
                BackColor = pageColor;
                MaximizeBox = false;

                BuildUi();
                Load += delegate
                {
                    var found = FindGameDir();
                    if (!string.IsNullOrEmpty(found))
                    {
                        gameDirBox.Text = found;
                    }
                    UpdateSmartAdvice();
                    RefreshCurrentValues();
                    CheckFixStatus(false);
                };
            }

            private void BuildUi()
            {
                var banner = new BannerPanel(GetHeroImagePath())
                {
                    Location = new Point(26, 18),
                    Size = new Size(768, 128)
                };

                var title = new Label
                {
                    Text = "地狱潜兵 2 配置修改",
                    Font = new Font(Font.FontFamily, 19F, FontStyle.Bold),
                    Location = new Point(22, 24),
                    Size = new Size(430, 36),
                    ForeColor = Color.White,
                    BackColor = Color.Transparent
                };

                var subtitle = new Label
                {
                    Text = "低风险版：只读取和修改游戏目录 data\\settings.ini，确认后才会写入。",
                    Location = new Point(25, 64),
                    Size = new Size(640, 22),
                    ForeColor = Color.FromArgb(226, 232, 240),
                    BackColor = Color.Transparent
                };

                statusDot.Location = new Point(620, 31);
                statusDot.Size = new Size(10, 10);
                statusDot.BackColor = okColor;

                statusLabel.Text = "准备就绪";
                statusLabel.Location = new Point(636, 24);
                statusLabel.Size = new Size(120, 24);
                statusLabel.TextAlign = ContentAlignment.MiddleLeft;
                statusLabel.ForeColor = okColor;
                statusLabel.BackColor = Color.Transparent;

                banner.Controls.Add(title);
                banner.Controls.Add(subtitle);
                banner.Controls.Add(statusDot);
                banner.Controls.Add(statusLabel);

                var pathBox = MakeCard(26, 166, 768, 100);
                pathBox.Controls.Add(MakeTitle("游戏目录", 16, 12));

                gameDirBox.Location = new Point(18, 46);
                gameDirBox.Size = new Size(558, 24);
                tips.SetToolTip(gameDirBox, "这里应是 Helldivers 2 安装目录，例如 SteamLibrary\\steamapps\\common\\Helldivers 2");

                var browseButton = MakeButton("浏览", 590, 43, 58, 30, false);
                browseButton.Click += BrowseButton_Click;
                var detectButton = MakeButton("自动检测", 656, 43, 84, 30, false);
                detectButton.Click += delegate { AutoDetectGameDir(true); };

                pathBox.Controls.Add(gameDirBox);
                pathBox.Controls.Add(browseButton);
                pathBox.Controls.Add(detectButton);

                var planCard = MakeCard(26, 284, 376, 276);
                planCard.Controls.Add(MakeTitle("修改方案", 16, 12));

                planBox.DropDownStyle = ComboBoxStyle.DropDownList;
                planBox.Location = new Point(18, 44);
                planBox.Size = new Size(330, 25);
                planBox.Items.Add("主方案：U9-275HX 稳定 8P");
                planBox.Items.Add("备用：高帧率 10 线程");
                planBox.Items.Add("备用：激进高帧 12 线程");
                planBox.Items.Add("备用：低温稳定 6 线程");
                planBox.Items.Add("备用：只改 CPU 8P");
                planBox.Items.Add("恢复官方默认");
                planBox.Items.Add("自定义");
                planBox.SelectedIndex = 0;
                planBox.SelectedIndexChanged += PlanBox_SelectedIndexChanged;
                tips.SetToolTip(planBox, "主方案优先稳定。10/12 线程方案用于尝试更高峰值帧率；6 线程方案用于温度墙或冻结严重时降负载。");

                cpuCheck.Text = "修改 CPU 线程";
                cpuCheck.Checked = true;
                cpuCheck.Location = new Point(18, 84);
                cpuCheck.Size = new Size(140, 22);
                cpuCheck.ForeColor = textColor;
                cpuCheck.CheckedChanged += MarkCustom;
                tips.SetToolTip(cpuCheck, "修改 num_reserved_threads，用于限制游戏可用线程数量。");

                audioCheck.Text = "修改音频缓冲";
                audioCheck.Checked = true;
                audioCheck.Location = new Point(182, 84);
                audioCheck.Size = new Size(140, 22);
                audioCheck.ForeColor = textColor;
                audioCheck.CheckedChanged += MarkCustom;
                tips.SetToolTip(audioCheck, "修改 memory_size 和 num_refills_in_voice，用于缓解音频爆音或短冻结。");

                planCard.Controls.Add(MakeLabel("保留线程数", 18, 120));
                reservedThreadsBox.Location = new Point(128, 117);
                reservedThreadsBox.Size = new Size(84, 24);
                reservedThreadsBox.Minimum = 0;
                reservedThreadsBox.Maximum = 128;
                reservedThreadsBox.Value = 16;
                reservedThreadsBox.ValueChanged += MarkCustom;

                var recommendButton = MakeButton("智能填入", 226, 115, 88, 28, false);
                recommendButton.Click += delegate
                {
                    reservedThreadsBox.Value = RecommendReservedThreads();
                    MarkCustom(null, EventArgs.Empty);
                    Log("已按当前 CPU 线程数填入建议值。");
                };

                planCard.Controls.Add(MakeLabel("音频缓存 MB", 18, 156));
                memorySizeBox.Location = new Point(128, 153);
                memorySizeBox.Size = new Size(96, 24);
                memorySizeBox.Minimum = 1;
                memorySizeBox.Maximum = 512;
                memorySizeBox.Value = 75;
                memorySizeBox.ValueChanged += MarkCustom;

                planCard.Controls.Add(MakeLabel("缓冲补充次数", 18, 192));
                refillsBox.Location = new Point(128, 189);
                refillsBox.Size = new Size(96, 24);
                refillsBox.Minimum = 1;
                refillsBox.Maximum = 16;
                refillsBox.Value = 4;
                refillsBox.ValueChanged += MarkCustom;
                tips.SetToolTip(refillsBox, "对应 num_refills_in_voice。数值越高，音频缓冲余量越大，但理论上延迟也可能略增。");

                var note = new Label
                {
                    Text = "主方案仍是 16 / 75MB / 4。备用方案用于按帧率、温度和稳定性微调。",
                    Location = new Point(18, 226),
                    Size = new Size(330, 36),
                    ForeColor = mutedColor
                };

                planCard.Controls.Add(planBox);
                planCard.Controls.Add(cpuCheck);
                planCard.Controls.Add(audioCheck);
                planCard.Controls.Add(reservedThreadsBox);
                planCard.Controls.Add(recommendButton);
                planCard.Controls.Add(memorySizeBox);
                planCard.Controls.Add(refillsBox);
                planCard.Controls.Add(note);

                var statusCard = MakeCard(418, 284, 376, 276);
                statusCard.Controls.Add(MakeTitle("检查与操作", 16, 12));

                currentLabel.Text = "当前配置：未读取";
                currentLabel.Location = new Point(18, 44);
                currentLabel.Size = new Size(334, 76);
                currentLabel.ForeColor = textColor;

                adviceLabel.Text = "智能建议：读取中";
                adviceLabel.Location = new Point(18, 124);
                adviceLabel.Size = new Size(334, 54);
                adviceLabel.ForeColor = mutedColor;

                var checkButton = MakeButton("检测失效", 18, 184, 96, 34, false);
                checkButton.Click += delegate { CheckFixStatus(true); };

                var applyButton = MakeButton("应用修改", 132, 184, 116, 34, true);
                applyButton.Click += ApplyButton_Click;

                var refreshButton = MakeButton("刷新", 18, 224, 72, 34, false);
                refreshButton.Click += delegate
                {
                    UpdateSmartAdvice();
                    RefreshCurrentValues();
                    CheckFixStatus(false);
                };

                var restoreButton = MakeButton("恢复备份", 106, 224, 96, 34, false);
                restoreButton.Click += RestoreButton_Click;

                statusCard.Controls.Add(currentLabel);
                statusCard.Controls.Add(adviceLabel);
                statusCard.Controls.Add(checkButton);
                statusCard.Controls.Add(refreshButton);
                statusCard.Controls.Add(applyButton);
                statusCard.Controls.Add(restoreButton);

                var logCard = MakeCard(26, 578, 768, 52);
                logBox.Location = new Point(14, 12);
                logBox.Size = new Size(740, 28);
                logBox.Multiline = true;
                logBox.BorderStyle = BorderStyle.None;
                logBox.ReadOnly = true;
                logBox.BackColor = cardColor;
                logBox.ForeColor = Color.FromArgb(68, 78, 92);
                logCard.Controls.Add(logBox);

                Controls.Add(banner);
                Controls.Add(pathBox);
                Controls.Add(planCard);
                Controls.Add(statusCard);
                Controls.Add(logCard);
            }

            private string GetHeroImagePath()
            {
                var exeDir = AppDomain.CurrentDomain.BaseDirectory;
                var localAsset = Path.Combine(exeDir, "assets", "library_hero.jpg");
                if (File.Exists(localAsset)) return localAsset;

                var desktopAsset = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                    "地狱潜兵修改CPU",
                    "assets",
                    "library_hero.jpg");
                if (File.Exists(desktopAsset)) return desktopAsset;

                return "";
            }

            private Panel MakeCard(int x, int y, int w, int h)
            {
                return new Panel
                {
                    Location = new Point(x, y),
                    Size = new Size(w, h),
                    BackColor = cardColor,
                    BorderStyle = BorderStyle.FixedSingle
                };
            }

            private Label MakeTitle(string text, int x, int y)
            {
                return new Label
                {
                    Text = text,
                    Location = new Point(x, y),
                    Size = new Size(230, 24),
                    Font = new Font(Font.FontFamily, 10.5F, FontStyle.Bold),
                    ForeColor = textColor
                };
            }

            private Label MakeLabel(string text, int x, int y)
            {
                return new Label
                {
                    Text = text,
                    Location = new Point(x, y),
                    Size = new Size(104, 22),
                    ForeColor = mutedColor
                };
            }

            private Button MakeButton(string text, int x, int y, int w, int h, bool primary)
            {
                var button = new Button
                {
                    Text = text,
                    Location = new Point(x, y),
                    Size = new Size(w, h),
                    FlatStyle = FlatStyle.Flat,
                    Cursor = Cursors.Hand
                };

                if (primary)
                {
                    button.BackColor = accentColor;
                    button.ForeColor = Color.White;
                    button.FlatAppearance.BorderSize = 0;
                    button.Font = new Font(Font.FontFamily, 9.5F, FontStyle.Bold);
                }
                else
                {
                    button.BackColor = Color.FromArgb(250, 252, 255);
                    button.ForeColor = textColor;
                    button.FlatAppearance.BorderColor = Color.FromArgb(204, 213, 226);
                }

                return button;
            }

            private void AutoDetectGameDir(bool showResult)
            {
                var found = FindGameDir();
                if (string.IsNullOrEmpty(found))
                {
                    if (showResult)
                    {
                        Warn("没有自动找到游戏目录，请手动选择。");
                    }
                    return;
                }

                gameDirBox.Text = found;
                RefreshCurrentValues();
                CheckFixStatus(false);
                if (showResult)
                {
                    Log("已检测到游戏目录：" + found);
                }
            }

            private void BrowseButton_Click(object sender, EventArgs e)
            {
                using (var dialog = new FolderBrowserDialog())
                {
                    dialog.Description = "选择 Helldivers 2 游戏安装目录";
                    dialog.SelectedPath = Directory.Exists(gameDirBox.Text)
                        ? gameDirBox.Text
                        : Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

                    if (dialog.ShowDialog(this) == DialogResult.OK)
                    {
                        gameDirBox.Text = dialog.SelectedPath;
                        RefreshCurrentValues();
                        CheckFixStatus(false);
                    }
                }
            }

            private void PlanBox_SelectedIndexChanged(object sender, EventArgs e)
            {
                if (applyingPlan) return;
                applyingPlan = true;

                if (planBox.SelectedIndex == 0)
                {
                    cpuCheck.Checked = true;
                    audioCheck.Checked = true;
                    reservedThreadsBox.Value = 16;
                    memorySizeBox.Value = 75;
                    refillsBox.Value = 4;
                }
                else if (planBox.SelectedIndex == 1)
                {
                    cpuCheck.Checked = true;
                    audioCheck.Checked = true;
                    reservedThreadsBox.Value = 14;
                    memorySizeBox.Value = 75;
                    refillsBox.Value = 4;
                }
                else if (planBox.SelectedIndex == 2)
                {
                    cpuCheck.Checked = true;
                    audioCheck.Checked = true;
                    reservedThreadsBox.Value = 12;
                    memorySizeBox.Value = 75;
                    refillsBox.Value = 4;
                }
                else if (planBox.SelectedIndex == 3)
                {
                    cpuCheck.Checked = true;
                    audioCheck.Checked = true;
                    reservedThreadsBox.Value = 18;
                    memorySizeBox.Value = 75;
                    refillsBox.Value = 4;
                }
                else if (planBox.SelectedIndex == 4)
                {
                    cpuCheck.Checked = true;
                    audioCheck.Checked = false;
                    reservedThreadsBox.Value = 16;
                    memorySizeBox.Value = 75;
                    refillsBox.Value = 4;
                }
                else if (planBox.SelectedIndex == 5)
                {
                    cpuCheck.Checked = true;
                    audioCheck.Checked = true;
                    reservedThreadsBox.Value = 2;
                    memorySizeBox.Value = 25;
                    refillsBox.Value = 2;
                }

                applyingPlan = false;
            }

            private void MarkCustom(object sender, EventArgs e)
            {
                if (applyingPlan || planBox.SelectedIndex == 6) return;
                applyingPlan = true;
                planBox.SelectedIndex = 6;
                applyingPlan = false;
            }

            private void ApplyButton_Click(object sender, EventArgs e)
            {
                var ini = GetSettingsFile();
                if (ini == null)
                {
                    Warn("没有找到 data\\settings.ini，请检查游戏目录。");
                    return;
                }

                if (!cpuCheck.Checked && !audioCheck.Checked)
                {
                    Warn("至少勾选一个要修改的项目。");
                    return;
                }

                try
                {
                    var oldText = ReadText(ini);
                    var missing = FindMissingRequiredKeys(oldText);
                    if (missing.Length > 0)
                    {
                        Warn("配置文件缺少必要字段，已停止修改：" + missing);
                        return;
                    }

                    var newText = oldText;
                    var preview = new StringBuilder();
                    var targetThreads = (int)reservedThreadsBox.Value;
                    var targetMemory = (int)memorySizeBox.Value * 1024 * 1024;
                    var targetRefills = (int)refillsBox.Value;

                    if (cpuCheck.Checked)
                    {
                        var oldValue = ReadInt(oldText, @"(?im)^\s*num[\s_]+reserved[\s_]+threads\s*=\s*(\d+)");
                        if (oldValue.HasValue && oldValue.Value != targetThreads)
                        {
                            preview.AppendLine("num_reserved_threads: " + oldValue.Value + " -> " + targetThreads);
                        }
                        newText = ReplaceInt(newText, @"(?im)^(\s*num[\s_]+reserved[\s_]+threads\s*=\s*)\d+([^\r\n]*)", targetThreads);
                    }

                    if (audioCheck.Checked)
                    {
                        var oldMemory = ReadInt(oldText, @"(?im)^\s*memory_size\s*=\s*(\d+)");
                        if (oldMemory.HasValue && oldMemory.Value != targetMemory)
                        {
                            preview.AppendLine("memory_size: " + ToMb(oldMemory.Value) + " -> " + ToMb(targetMemory));
                        }
                        newText = ReplaceInt(newText, @"(?im)^(\s*memory_size\s*=\s*)\d+([^\r\n]*)", targetMemory);

                        var oldRefills = ReadInt(oldText, @"(?im)^\s*num_refills_in_voice\s*=\s*(\d+)");
                        if (oldRefills.HasValue && oldRefills.Value != targetRefills)
                        {
                            preview.AppendLine("num_refills_in_voice: " + oldRefills.Value + " -> " + targetRefills);
                        }
                        newText = ReplaceInt(newText, @"(?im)^(\s*num_refills_in_voice\s*=\s*)\d+([^\r\n]*)", targetRefills);
                    }

                    if (newText == oldText)
                    {
                        MessageBox.Show(this, "当前配置已经是目标值，不需要修改。", "无需修改", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }

                    var message = "确认应用以下修改吗？\r\n\r\n" + preview + "\r\n文件：\r\n" + ini + "\r\n\r\n程序会先自动备份。";
                    if (MessageBox.Show(this, message, "确认修改", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                    {
                        Log("已取消，没有修改文件。");
                        return;
                    }

                    var backup = ini + ".bak_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    File.Copy(ini, backup, true);

                    var oldAttributes = File.GetAttributes(ini);
                    if ((oldAttributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                    {
                        File.SetAttributes(ini, oldAttributes & ~FileAttributes.ReadOnly);
                    }

                    WriteText(ini, newText);
                    VerifyWrittenValues(ini, cpuCheck.Checked, audioCheck.Checked, targetThreads, targetMemory, targetRefills);

                    Log("修改完成，已备份原文件：" + Path.GetFileName(backup));
                    SetStatus("修改完成", true);
                    RefreshCurrentValues();
                }
                catch (Exception ex)
                {
                    SetStatus("修改失败", false);
                    MessageBox.Show(this, ex.Message, "修改失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }

            private void CheckFixStatus(bool showMessage)
            {
                var ini = GetSettingsFile();
                if (ini == null)
                {
                    Warn("没有找到 data\\settings.ini，请检查游戏目录。");
                    return;
                }

                try
                {
                    var text = ReadText(ini);
                    var missing = FindMissingRequiredKeys(text);
                    if (missing.Length > 0)
                    {
                        Warn("配置文件缺少必要字段，无法检测：" + missing);
                        return;
                    }

                    var currentThreads = ReadInt(text, @"(?im)^\s*num[\s_]+reserved[\s_]+threads\s*=\s*(\d+)");
                    var currentMemory = ReadInt(text, @"(?im)^\s*memory_size\s*=\s*(\d+)");
                    var currentRefills = ReadInt(text, @"(?im)^\s*num_refills_in_voice\s*=\s*(\d+)");

                    var targetThreads = (int)reservedThreadsBox.Value;
                    var targetMemory = (int)memorySizeBox.Value * 1024 * 1024;
                    var targetRefills = (int)refillsBox.Value;
                    var differences = new StringBuilder();

                    if (cpuCheck.Checked && currentThreads.HasValue && currentThreads.Value != targetThreads)
                    {
                        differences.AppendLine("num_reserved_threads: 当前 " + currentThreads.Value + "，目标 " + targetThreads);
                    }

                    if (audioCheck.Checked && currentMemory.HasValue && currentMemory.Value != targetMemory)
                    {
                        differences.AppendLine("memory_size: 当前 " + ToMb(currentMemory.Value) + "，目标 " + ToMb(targetMemory));
                    }

                    if (audioCheck.Checked && currentRefills.HasValue && currentRefills.Value != targetRefills)
                    {
                        differences.AppendLine("num_refills_in_voice: 当前 " + currentRefills.Value + "，目标 " + targetRefills);
                    }

                    if (differences.Length == 0)
                    {
                        SetStatus("修复有效", true);
                        Log("检测完成：当前配置仍符合所选方案。");
                        if (showMessage)
                        {
                            MessageBox.Show(this, "检测完成：当前配置仍符合所选方案，修复没有失效。", "修复有效", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }
                    }
                    else
                    {
                        SetStatus("修复失效", false);
                        Log("检测完成：配置与所选方案不一致，建议重新应用修改。");
                        if (showMessage)
                        {
                            MessageBox.Show(this,
                                "检测完成：修复可能已被 Steam 更新覆盖。\r\n\r\n" + differences + "\r\n可以点击“应用修改”重新写入。",
                                "修复已失效",
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Warning);
                        }
                    }
                }
                catch (Exception ex)
                {
                    SetStatus("检测失败", false);
                    MessageBox.Show(this, ex.Message, "检测失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }

            private void RestoreButton_Click(object sender, EventArgs e)
            {
                var ini = GetSettingsFile();
                if (ini == null)
                {
                    Warn("没有找到 data\\settings.ini。");
                    return;
                }

                var dir = Path.GetDirectoryName(ini);
                var backups = Directory.GetFiles(dir, "settings.ini.bak_*");
                if (backups.Length == 0)
                {
                    Warn("没有找到备份文件。");
                    return;
                }

                Array.Sort(backups);
                var latest = backups[backups.Length - 1];
                if (MessageBox.Show(this, "恢复最近备份吗？\r\n\r\n" + latest, "确认恢复", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                {
                    return;
                }

                File.Copy(latest, ini, true);
                Log("已恢复最近备份。");
                SetStatus("已恢复", true);
                RefreshCurrentValues();
            }

            private void RefreshCurrentValues()
            {
                var ini = GetSettingsFile();
                if (ini == null)
                {
                    currentLabel.Text = "当前配置：未找到 data\\settings.ini";
                    SetStatus("未找到配置", false);
                    return;
                }

                try
                {
                    var text = ReadText(ini);
                    var threads = ReadInt(text, @"(?im)^\s*num[\s_]+reserved[\s_]+threads\s*=\s*(\d+)");
                    var memory = ReadInt(text, @"(?im)^\s*memory_size\s*=\s*(\d+)");
                    var refills = ReadInt(text, @"(?im)^\s*num_refills_in_voice\s*=\s*(\d+)");

                    currentLabel.Text =
                        "文件：" + ini + "\r\n" +
                        "CPU 保留线程：" + ShowValue(threads) + "\r\n" +
                        "音频缓存：" + (memory.HasValue ? ToMb(memory.Value) : "未找到") + "\r\n" +
                        "音频 refill：" + ShowValue(refills);

                    SetStatus("已读取", true);
                }
                catch (Exception ex)
                {
                    currentLabel.Text = "读取失败：" + ex.Message;
                    SetStatus("读取失败", false);
                }
            }

            private void UpdateSmartAdvice()
            {
                var cpu = GetCpuName();
                var threads = Environment.ProcessorCount;
                var recommended = RecommendReservedThreads();
                adviceLabel.Text = "智能建议：检测到 " + threads + " 个逻辑线程，建议保留线程数 " + recommended + "。\r\n" +
                                   (cpu.Length > 0 ? TrimText(cpu, 42) : "CPU 名称读取失败，但不影响修改。");
            }

            private int RecommendReservedThreads()
            {
                var threads = Environment.ProcessorCount;
                var cpu = GetCpuName();
                if (cpu.IndexOf("275HX", StringComparison.OrdinalIgnoreCase) >= 0) return 16;
                if (threads >= 24) return 16;
                if (threads >= 16) return 8;
                return 4;
            }

            private string GetSettingsFile()
            {
                var dir = gameDirBox.Text.Trim();
                if (dir.Length == 0 || !Directory.Exists(dir)) return null;

                var direct = Path.Combine(dir, "data", "settings.ini");
                if (File.Exists(direct)) return direct;

                var alternate = Path.Combine(dir, "data", "settings", "settings.ini");
                if (File.Exists(alternate)) return alternate;

                return null;
            }

            private string FindMissingRequiredKeys(string text)
            {
                var missing = new StringBuilder();
                if (cpuCheck.Checked && !Regex.IsMatch(text, @"(?im)^\s*num[\s_]+reserved[\s_]+threads\s*="))
                {
                    missing.Append("num_reserved_threads ");
                }
                if (audioCheck.Checked && !Regex.IsMatch(text, @"(?im)^\s*memory_size\s*="))
                {
                    missing.Append("memory_size ");
                }
                if (audioCheck.Checked && !Regex.IsMatch(text, @"(?im)^\s*num_refills_in_voice\s*="))
                {
                    missing.Append("num_refills_in_voice ");
                }
                return missing.ToString().Trim();
            }

            private void VerifyWrittenValues(string ini, bool verifyCpu, bool verifyAudio, int targetThreads, int targetMemory, int targetRefills)
            {
                var text = ReadText(ini);
                if (verifyCpu && ReadInt(text, @"(?im)^\s*num[\s_]+reserved[\s_]+threads\s*=\s*(\d+)") != targetThreads)
                {
                    throw new InvalidOperationException("写入后校验失败：num_reserved_threads 未变成目标值。");
                }
                if (verifyAudio && ReadInt(text, @"(?im)^\s*memory_size\s*=\s*(\d+)") != targetMemory)
                {
                    throw new InvalidOperationException("写入后校验失败：memory_size 未变成目标值。");
                }
                if (verifyAudio && ReadInt(text, @"(?im)^\s*num_refills_in_voice\s*=\s*(\d+)") != targetRefills)
                {
                    throw new InvalidOperationException("写入后校验失败：num_refills_in_voice 未变成目标值。");
                }
            }

            private void Warn(string message)
            {
                SetStatus("需要处理", false);
                MessageBox.Show(this, message, "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            private void SetStatus(string text, bool ok)
            {
                statusLabel.Text = text;
                statusLabel.ForeColor = ok ? okColor : warnColor;
                statusDot.BackColor = ok ? okColor : warnColor;
            }

            private void Log(string message)
            {
                logBox.Text = "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + message;
            }
        }

        sealed class BannerPanel : Panel
        {
            private Image heroImage;

            public BannerPanel(string imagePath)
            {
                DoubleBuffered = true;
                BackColor = Color.FromArgb(22, 26, 32);
                if (!string.IsNullOrEmpty(imagePath) && File.Exists(imagePath))
                {
                    try
                    {
                        using (var stream = File.OpenRead(imagePath))
                        {
                            using (var loaded = Image.FromStream(stream))
                            {
                                heroImage = new Bitmap(loaded);
                            }
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
                    var x = (dest.Width - width) / 2f;
                    var y = (dest.Height - height) / 2f;
                    e.Graphics.DrawImage(heroImage, x, y, width, height);
                }

                using (var brush = new LinearGradientBrush(
                    ClientRectangle,
                    Color.FromArgb(215, 11, 14, 18),
                    Color.FromArgb(115, 11, 14, 18),
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

        private static string FindGameDir()
        {
            var steam = GetSteamPath();
            if (string.IsNullOrEmpty(steam)) return null;

            var libraryFile = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            var libraries = new System.Collections.Generic.List<string>();
            libraries.Add(steam);

            if (File.Exists(libraryFile))
            {
                var text = ReadText(libraryFile);
                foreach (Match match in Regex.Matches(text, "\"path\"\\s+\"([^\"]+)\""))
                {
                    var path = match.Groups[1].Value.Replace(@"\\", @"\");
                    if (Directory.Exists(path)) libraries.Add(path);
                }
            }

            foreach (var library in libraries)
            {
                var manifest = Path.Combine(library, "steamapps", "appmanifest_" + AppId + ".acf");
                if (!File.Exists(manifest)) continue;

                var manifestText = ReadText(manifest);
                var installDir = "Helldivers 2";
                var match = Regex.Match(manifestText, "\"installdir\"\\s+\"([^\"]+)\"");
                if (match.Success) installDir = match.Groups[1].Value;

                var candidate = Path.Combine(library, "steamapps", "common", installDir);
                if (Directory.Exists(candidate)) return candidate;
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
            using (var key = root.OpenSubKey(subKey))
            {
                if (key == null) return null;

                var steamPath = key.GetValue("SteamPath") as string;
                if (!string.IsNullOrEmpty(steamPath) && Directory.Exists(steamPath)) return steamPath;

                var installPath = key.GetValue("InstallPath") as string;
                if (!string.IsNullOrEmpty(installPath) && Directory.Exists(installPath)) return installPath;
            }
            return null;
        }

        private static string GetCpuName()
        {
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
                {
                    if (key != null)
                    {
                        var value = key.GetValue("ProcessorNameString") as string;
                        if (!string.IsNullOrEmpty(value)) return value.Trim();
                    }
                }
            }
            catch
            {
            }
            return "";
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

        private static string ShowValue(int? value)
        {
            return value.HasValue ? value.Value.ToString() : "未找到";
        }

        private static string ToMb(int bytes)
        {
            decimal mb = Math.Round((decimal)bytes / 1024m / 1024m, 1);
            return mb.ToString("0.#") + " MB";
        }

        private static string TrimText(string value, int maxLength)
        {
            if (value.Length <= maxLength) return value;
            return value.Substring(0, maxLength - 1) + "...";
        }

        private static string ReadText(string path)
        {
            return File.ReadAllText(path, Encoding.UTF8);
        }

        private static void WriteText(string path, string text)
        {
            File.WriteAllText(path, text, new UTF8Encoding(false));
        }
    }
}
