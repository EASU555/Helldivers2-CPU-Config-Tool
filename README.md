# Helldivers 2 CPU Config Tool

《地狱潜兵 2》本地配置调整工具。v1.6.9 只读取和修改游戏目录中的 `data/settings.ini`，不注入进程、不修改游戏内存、不结束游戏或反作弊进程，也不联网。

## 主要功能

- 调整 `num_reserved_threads`、`memory_size` 和 `num_refills_in_voice`。
- 针对 Ultra 9 275HX 提供 8、10、12、6 个游戏可用线程方案。
- 新备份记录 SHA-256 与长度，使用同目录临时文件校验后原子替换，失败自动回滚。
- 确认后如果配置被游戏、Steam 或其他程序改动，本次操作会安全停止。
- 单实例运行，避免两个工具窗口同时写入同一配置。
- 正确保留原配置文件只读属性，可选在成功后锁定配置。
- Steam 更新后自动静默检测配置是否失效，也可手动检测。
- 检测 `helldivers2.exe`；游戏运行时阻止修改，不会强制结束进程。
- 支持应用后通过 `steam://rungameid/553850` 启动游戏。
- 记住上次目录、方案、自定义参数和锁定选项。
- 自动备份最多保留最近 10 份。
- 恢复前永久保留当前配置，并对恢复文件执行完整文件校验。
- 支持打开配置目录、记事本打开和复制脱敏诊断信息。
- 高 DPI 界面为横幅、路径输入和两行命令网格保留最低可用高度；命令按钮只进行一次文本绘制，避免 Windows Forms 的中文文字叠画或丢失。

## 使用与构建

- 使用说明：[USAGE.md](USAGE.md)
- 主源码：[work/Helldivers2CpuFixer.cs](work/Helldivers2CpuFixer.cs)
- 集成测试：[work/Helldivers2CpuFixer.Tests.cs](work/Helldivers2CpuFixer.Tests.cs)
- DPI 清单：[work/app.manifest](work/app.manifest)
- 构建说明：[work/构建说明_v1.6.9.txt](work/构建说明_v1.6.9.txt)

## 重要提示

- 这是非官方社区 workaround，不保证提高所有电脑的平均帧率。
- `num_reserved_threads` 是游戏配置项，不等同于 Windows 进程亲和性；它不能严格保证任务只运行在 P 核。
- 音频参数主要用于排查爆音、断音或短冻结，不是通用帧率优化。
- “恢复常见默认值”使用当前社区常见数值，并不承诺永远等于未来游戏版本的官方默认值。
- Steam 或游戏更新仍可能覆盖配置；更新后打开工具查看状态即可。
