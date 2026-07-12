# Helldivers 2 CPU Config Tool

《地狱潜兵 2》本地配置调整工具。它只读取和修改游戏安装目录中的 `data/settings.ini`，用于调整可用 CPU 线程和音频缓冲相关参数，帮助特定硬件配置尝试缓解高负载场景中的卡顿、温度撞墙或音频问题。

## 项目用途

- 修改 `num_reserved_threads`，控制游戏保留的 CPU 线程数量。
- 可选修改 `memory_size` 与 `num_refills_in_voice`，调整 Wwise 音频缓冲参数。
- 自动尝试查找 Steam 游戏目录，也支持手动选择安装目录。
- 写入前显示确认，并为原始 `settings.ini` 创建备份。
- 提供方案失效检测和恢复官方默认值的能力。

## 使用说明

请阅读 [USAGE.md](USAGE.md)。

## 源码

主要源码位于 [work/Helldivers2CpuFixer.cs](work/Helldivers2CpuFixer.cs)，为本地 Windows Forms 工具。仓库不包含已编译 EXE、压缩包、测试生成物或本机配置文件。

## 重要提示

- 这是非官方社区工具，不修改游戏主程序、不注入游戏进程、不联网，也不常驻后台。
- 不同 CPU、驱动和游戏版本的效果不同，请一次只测试一个方案并自行验证稳定性。
- Steam 或游戏更新可能会覆盖 `settings.ini` 中的设置。
