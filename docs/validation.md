# 软件验证记录

## 0.1.5 正式发布验收（2026-11-02）

正式版沿用已验收 `0.1.5-dev.4` 的功能代码，发布时仅调整版本标识、文档与发布流程。候选版通过 238 项 C# 检查、C++ 运行时／音频／握手／诊断检查、4 项补丁兼容性检查，以及独立音频进程和进程生命周期故障检查。正式包由 GitHub Actions 从标签源码重新构建并重复软件检查；云端检查不替代实机验收。

用户分别完成点击“停止并恢复原生”和直接关闭主窗口两条退出路径，确认精细触觉、自适应扳机正常，退出后恢复原生蓝色常亮，Bose 播放持续正常且音量键实际有效。直接关窗后约 95 秒、19 次只读恢复观察无警告，19 次实体输入均可读取；最终无后端、虚拟手柄、导入端口或待恢复会话，HidHide 配置与启用前一致。记录保存在 `.local/controller-exit-review/acceptance.json`，原始设备与会话数据不随仓库分发。

恢复范围限于工具拥有的变更，保留用户和其他软件的后续调整。自然待机重连、原生 USB 回退仍待本版复测；不承诺任意原有自定义灯效或不可读取的固件内部状态精确复原，也不以本机 Bose 结果推断所有蓝牙音频故障均已解决。详见 [正式版本说明](releases/v0.1.5.md) 和 [手柄状态恢复](controller-state-recovery.md)。

## 0.1.4 普通诊断与锁占用优化（2026-10-30）

121 项 C# 检查、C++ 运行时及诊断并发/阻塞/排空检查、3 项补丁日志兼容性检查、后端空会话协议检查通过。独立审查确认两处问题修正后无剩余阻塞项。新版单独完成触觉、R2、取消归零、12 次脉冲及退出后约 90 秒的 19 次恢复观察，警告为 0；用户确认手柄反馈和 Bose 正常。默认仍为 3 块预缓冲，不新增驱动或开机项，也不宣称新的物理延迟降幅。详见 [诊断优化与验收](diagnostic-jitter-optimization.md) 和 `.local/tests/diagnostic-optimization-acceptance.json`。

## 0.1.3 震动专项优化（2026-10-30）

121 项 C# 检查通过；后端新增异步 HID 请求、取消、有限队列、断开清理、会话代次与 socket 复用边界检查通过。USB 事件驱动音频脉冲实机通过；三阶段桥接脉冲采集均完成。最终内部音频队列等待采样中位数约 26ms，当前物理延迟没有数值结论。详见 [优化记录](haptics-latency-optimization.md)。最终恢复及发布身份以 `.local/tests/haptics-optimization-acceptance.json` 为准。

## 0.1.2 延迟对比模块（2026-10-30）

- Release 构建成功；121 项纯软件检查通过，包括 USB 报告解析、独立输入阈值跟踪、12 次精细震动脉冲及相同 USB/桥接采样时间表。日志：`.local/logs/build-latency-module.log`。
- 独立进程测试互斥通过：空闲探针返回 0，另一进程持有租约时返回 2，释放后返回 0；此检查未打开手柄。证据：`.local/tests/device-test-lease.json`。
- 延迟面板通过程序内位图渲染检查：`.local/latency-panel.png`；不是完整鼠标操作验收。
- 后端保持 0.1.1 已验收二进制；未新增驱动、服务、开机项或蓝牙设置变更。
- **USB/桥接两轮物理延迟结果仍待有效实拍。** 报告间隔和绘制回调耗时不视为物理延迟。测试流程及可测范围见 [latency-comparison.md](latency-comparison.md)。

以下为较早阶段的历史记录。

日期：2026-10-29，Asia/Shanghai。目标机器：Windows 11 x64，10.0.26300。

## 已通过

| 检查 | 结果 | 证据 |
|---|---|---|
| C# Release 编译 | 0 警告、0 错误 | `.local\logs\build-hidhide-contention.log` |
| 回退故障注入 | 52 项检查通过 | 同一构建日志；`tests/DsBridge.Tests/Program.cs` |
| WinForms 1040×800、920×720 渲染 | 检查界面可显示，按钮可布局换行 | `.local/ui-check.png`、`.local/ui-small.png` |
| UI 只读交互 | 检查、游戏步骤、导出诊断；缺依赖时启用按钮禁用 | `.local/ui-check.json` |
| Job Object 生命周期 | 释放管理器持有的 Job 后，仅测试子进程被结束 | `dist/DualSenseBridge/.local/lifecycle-check.json` |
| vDS 后端固定版本身份 | 受控协议版本 1，serviceMode=false | `.local/backend/backend-manifest.json` |
| 后端前置条件拒绝 | 缺参数、缺停止事件、缺 USB/IP 时拒绝启动 | `.local/tests/backend-5a72a71744404593a5e0b32cef83f3fc/results.json` |
| 私有管道与停止事件 | 非空 JSON 回复正常；空会话正常停止 | 同上 |
| 本机只读设备检查 | 检出普通 DualSense USB 接口与默认蓝牙音频输出 | `dist/DualSenseBridge/.local/diagnostics/verified.json` |
| 依赖安装器验证 | USB/IP 官方摘要匹配；两包 Authenticode Valid | `.local/driver-plan.json` |

回退测试包括每项变更前后故障、幂等恢复、已有应用登记保留、其他手柄规则保留、外部并发更改时不覆盖全局配置、解除隐藏失败的报告和重试、磁盘满时仍执行恢复、写前日志失败时禁止第一次修改。

## 实机更新

已完成重启和普通蓝牙连接；虚拟 USB 枚举、输入转发、默认音频及正常停止恢复已通过本机检查。详情见 [hardware-validation.md](hardware-validation.md)。以下条目为最初软件阶段记录，涉及已完成项目以实机记录为准。

## 最初软件阶段的验证边界

- HidHide、usbip-win2 已在用户授权后安装；重启后验证尚未进行。未创建 vDS 服务或本工具开机项。安装详情见 [installation-result.md](installation-result.md)。
- 没有向实体手柄发送桥接输出，未验证真实蓝牙高清触觉、自适应扳机、扬声器/麦克风/耳机通道。
- 没有完成《影之刃：零》游戏内对比；没有进行真实 Xbox/其他 DS 并发回归。
- 默认音频读取已实测；“仅恢复本工具新建虚拟端点抢占的默认输出”写入路径须在驱动安装后验收。
- 桌面自动化 helper / JavaScript runtime 超时。UI 使用程序内 WinForms 渲染与事件调用检查，并人工查看位图；不是鼠标级端到端验收。原生 ComboBox 在 DrawToBitmap 中可能留白，已额外核对实际 SelectedIndex=0、SelectedItem 文字正确。
- 后端的空会话测试只替代 USB/IP 可执行文件存在性检查，未附加设备、未运行驱动安装器、未修改 HidHide；不能据此推断物理桥接通过。
- 断电或全部进程被强杀时，持久化隐藏规则不能保证即时回退；恢复日志和下次手动恢复仍需要。该边界在界面与 README 中明确说明。

## 下一阶段

两个固定版本驱动已安装并完成重启前只读核查。等待用户手动重启后，复核系统及原生连接基线，依照 [acceptance.md](acceptance.md) 完成真实验收。若驱动加载失败，不关闭 Secure Boot、内存完整性或启用测试签名来绕过。
