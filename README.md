# KeySwap

原生 .NET Framework WinForms 键盘工具：交换左侧 Alt 与 Ctrl，保留物理左 Alt+Tab 的 Windows 窗口切换，并内置小米 Pad 的 WorkBuddy Windows 桥接功能。一个程序、一套键盘钩子、一个托盘和一个登录自启任务。右侧 Ctrl/Alt 不做互换。

## 使用

正式运行使用 `%USERPROFILE%\Applications\KeySwap\KeySwap.exe`。从源码构建后，在管理员 PowerShell 中安装：

```powershell
./scripts/install.ps1 -SourcePath ./dist/KeySwap.exe
```

安装器会备份旧程序和自启动任务，将程序安装到独立目录，更新登录自启并启动验证。不要把开发工作目录里的 `dist/KeySwap.exe` 长期用作自启动程序：该目录可能带有沙箱的低完整性标记，即使选择“最高权限”也不能正常操作桌面。安装器保留原有功能设置和旧 WorkBuddy 任务，不修改工作目录的权限。

首次默认启用互换。小米 Pad 用户可勾选“小米 Pad 键盘桥接”，启用远程 Alt+Tab 和 Command→Windows 键。互换与桥接可独立开关；关闭互换时，桥接仍可使用。关闭窗口会进入托盘；右键托盘可切换功能、管理登录自启、打开日志目录或退出。

开关保存在 `HKCU\Software\KeySwap`。登录自启使用当前用户的计划任务，带 `--tray` 参数，并恢复上次保存的开关。任务不因切换到电池或运行超过 72 小时而结束。

参数：`--enable`、`--disable`、`--bridge`、`--no-bridge`、`--tray`、`--no-alttab`、`--quit`。排查问题时可用 `--logkeys`，只记录修饰键及 Tab/F12/Scroll Lock/Win 的键码、扫描码和硬件/注入标志，不记录输入文字；日志位于 `%TEMP%\keyswap-keys.log`。运行状态日志位于 `%USERPROFILE%\Applications\KeySwap\host.log`。

使用小米 Pad 的 WorkBuddy + Moonlight + Sunshine 时，Pad 将 Alt+Tab 编码成 Alt+F12，将 Command 编码成 Scroll Lock。KeySwap 在同一个状态机中先解码，再应用互换策略：互换与保留均开启时得到 Alt+Tab；开启互换但关闭保留时得到 Ctrl+Tab；关闭互换时仍得到原生 Alt+Tab。本机 F12/Scroll Lock 和没有 Alt 的远程 F12保持原有行为。

Pad 端 WorkBuddy 与 Moonlight 无需更新。方向键、三指拖动、触控板滚动仍由原来的 Pad 组件处理。内置状态服务兼容 UDP 57420 / `WB_STATUS_V2`，只返回版本及能力，不接受控制命令；端口占用会自动重试，不影响键盘。已有 Tailscale 状态防火墙规则不需要改动。

迁移时先退出旧 `WorkBuddyAltTabHost.exe`，并关闭它的登录任务，只保留 KeySwap。程序检测到旧 Host 时会拒绝重复启用桥接；启用内置桥接后也会占用旧 Host 的单实例标记，防止误启动第二套钩子。仓库的 `scripts/migrate-workbuddy.ps1` 可执行带备份和失败回滚的本机迁移，必须在管理员 PowerShell 中指定候选程序路径；旧程序文件和日志会保留。

若按键帽上的左 Alt 时，日志已经收到 Ctrl，说明输入端已先做过一次交换。可勾选“输入端已互换 Alt/Ctrl（仅本次开机校准）”，让普通互换和 Alt+Tab 保留基于实际物理位置工作。该校准绑定 Windows 本次启动时间，重启后自动失效；这是为了避免删除驱动映射后重启恢复正常，又被永久补偿反向。诊断日志的 `non-injected` 仅表示未标记为 SendInput 注入，不代表输入未经固件、驱动或虚拟键盘处理。

运行时互换和注册表 Scancode Map 不应同时启用。驱动层映射无法单独保留物理 Alt+Tab，移除已经加载的驱动层映射仍需重启。

## 构建与测试

无需额外 SDK，使用 Windows 自带的 .NET Framework 4.x C# 编译器，输出 x64 单文件程序。

```powershell
./build.ps1
./test.ps1
# 桌面集成测试：临时打开两个测试窗口，并发送模拟快捷键
./test.ps1 -Desktop
```

桌面测试检查真实前台窗口变化、重复 Tab/Shift+Tab、Alt+C 转 Ctrl+C、停止后的按键释放，以及 UI 阻塞时的钩子存活。输入来源仍是 SendInput，不能冒充物理键盘验证。

## 2026-10-30 故障修复

原版有几类彼此独立的问题：

- 只记录“物理 Alt”和“正在切窗口”，没有记录每个模拟按键由谁持有。组合期间按键重复或交叉释放会重新按下 Ctrl，或提前释放另一按键仍需要的 Alt。
- 关闭互换/退出时只清空变量，没有补发程序持有按键的 key-up。
- 钩子与 UI 共用线程，菜单查询、自启设置、日志写入会阻塞它。Windows 对低级钩子有超时限制，超时后可能静默移除钩子，而原程序仍显示为启用。[Microsoft 文档](https://learn.microsoft.com/en-us/windows/win32/winmsg/lowlevelkeyboardproc)
- 原版 `SendInput` 返回值被忽略，发送失败时仍吞掉原始按键。
- `--tray` 只隐藏窗口，互换默认关闭；设置未保存，重复启动也没有单实例保护。
- 自启任务默认禁止电池启动、切到电池停止、72 小时后停止。任务存在并不等于配置正确。

修复使用独立键盘线程、按键所有权状态机、成对释放、发送失败处理、保存开关、单实例保护和后台计划任务管理。首轮原版状态机在 12 个回归场景中有 5 个失败；后续补充启动时已按住修饰键、孤立 key-up、部分注入失败的测试，新版共 19 项状态测试及 7 项服务测试通过。历史 session 中的 INPUT 结构尺寸和 LLKHF_EXTENDED 常量错误此前已被 MiMo 修过，不是本次新修内容。

本次桌面验证还发现旧 KeySwap 实例仍在拦截键盘：测试钩子关闭后，原生 Ctrl 输入依然变为 Alt。停止旧实例后，启动时按住 Ctrl 的测试恢复正常，完整桌面集成测试通过。新版的单实例保护已通过再次运行登录任务验证，进程数保持为 1。测试记录在本地 `dist/integration-clean.log`。

原版在本机的基础模拟 Alt+Tab 测试也能切换窗口。因此这些缺陷是已复现的程序错误，但不足以唯一确定历史上每一次真实键盘失效的原因；应结合当前硬件输入和启动状态判断。

随后用户按键帽顺序实测左 Alt、左 Ctrl，分别在钩子入口收到 `A2/1D`、`A4/38`。这直接定位了真实键盘路径上的双重交换：物理 Alt 先变成 Ctrl，程序又把 Ctrl 变回 Alt，于是 Alt+Tab 正常，但普通互换抵消。现在按本次开机校准处理这些设备事件；模拟输入绕过此补偿。补充后的状态测试为 23 项，另有 7 项服务测试和 6 项启动校准测试。

启用本次开机校准后，用户已用本机真实键盘确认左 Alt+C 复制和左 Alt+Tab 窗口切换均正常。底层输入被交换的具体来源尚未确定，因此校准不跨 Windows 重启永久保存。

远程输入还有独立冲突：KeySwap 先将 Sunshine 注入的 Alt 换成 Ctrl，使独立 WorkBuddy Host 注册的 Alt+F12 热键无法触发。1.1.0 将 Windows 桥接并入 KeySwap，统一处理来源按键、修饰键和释放责任，不再依赖两个进程的钩子顺序或异步热键转发。回归覆盖连续切换、快速松键、设置改变、未处理按键的重复、多来源持有、桥接关闭、独立 Win 键，以及部分发送失败。

1.1.0 已通过 97 项自动测试和真实桌面集成测试，包括快速释放的 Pad Alt+F12、关闭互换后的桥接、右 Alt 与 Windows 键保持/释放。用户随后在小米 Pad → Moonlight → Sunshine 的实际会话中确认 Alt+C、Alt+Tab、按住 Alt 连按 Tab、Command+E 四项均正常。迁移后仅 KeySwap 运行并监听 UDP 57420，旧 WorkBuddy 登录任务已停用，原文件与迁移备份保留。

## 2026-11-08 重启后失效的安装修复

本次故障中，自启动任务和进程都在运行，UDP 也回复 `core=ready`，但托盘不可见，设置与日志写入被拒绝，跨窗口按键功能失效。检查发现：开发目录及 EXE 继承了 Low integrity 标记；进程虽然持有提升后的管理员令牌，实际完整性仍为 Low（RID 4096）。这不是自启动任务漏执行，也没有证据证明键盘钩子超时。

Windows 会根据可执行文件的完整性标记限制新进程。仅检查“管理员”“进程存在”或 UDP 就绪，都不能证明它有权操作普通桌面。[Microsoft：Mandatory Integrity Control](https://learn.microsoft.com/en-us/windows/win32/secauthz/mandatory-integrity-control)

修复将同一份 1.1.0 二进制按内容写入独立安装目录中的新文件，让它继承目标目录的正常权限，再更新自启动路径。安装后实测进程为 High（RID 12288），托盘注册成功，日志写入恢复，左 Alt 输入产生预期的 Ctrl 输出。工作目录的沙箱权限保持不变，键盘映射算法没有改动。

`scripts/install.ps1` 固化了这条安装流程：拒绝仓库内或低完整性的目标路径，校验暂存文件哈希与完整性，正常退出已核实的旧实例，并验证新进程的路径、会话、实际权限和托盘；启用 Pad 桥接时还核对 UDP 的 nonce 回复。安装失败时尝试恢复备份，不按进程名强制终止程序。

验证结果：112 项回归检查通过；安装器已在本机完整执行，确认从登录任务启动的正式程序具有 High 完整性、有效托盘和正确的桥接回复。用户确认托盘及实际按键功能恢复正常。旧 Host 迁移脚本也改用独立安装目录，并增加文件及进程完整性检查。
