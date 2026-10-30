# 驱动安装验收记录

时间：2026-10-29 22:08，Asia/Shanghai。此处记录开发环境安装当时的状态。

## 已完成

| 组件 | 安装版本 | 安装器退出码 | 当前状态 |
|---|---|---|---|
| usbip-win2 x64 | 0.9.8.1 | 3010（成功，需要重启） | usbip2_ude / usbip2_filter 已加载；虚拟主控制器设备状态正常 |
| HidHide x64 | 1.5.230（发行标签 1.5.230.0） | 0 | 驱动已加载；隐藏关闭；隐藏设备列表为空 |

两个安装包在管理员进程内重新校验 SHA-256 和 Authenticode 后执行。禁止自动重启，未降低 Windows 安全设置。

## 未启用桥接时的检查

- 原生 USB DualSense 仍可枚举，PnP ProblemCode=0。
- 默认音频输出仍为安装前的“扬声器 (Bose Mini II SE SoundLink)”；这只证明默认输出未变，不代表听音或耳机共存测试通过。
- USB/IP 导入端口为空；没有 vdsd 服务、桥接进程或本工具开机项。
- HidHide 安装器添加的每日更新任务已禁用；用户登录自动更新项已移除，相关原始配置备份在项目日志目录。这是开发机器的单次配置，不是工具或公开检查脚本的自动行为；当前检查脚本只读系统状态。
- 保留 USB/IP 安装器自带的关机/重启清理任务：USBip Detach All On Reboot Or Shutdown。触发系统事件 109 / 1074，运行 detach --all=closeonly；不是开机桥接。
- 三个内核驱动的 StartMode 为 Manual；PnP 可在系统启动时加载已安装的驱动，不能理解为工具未启动时驱动不驻留。

## 当时后续步骤与当前状态

安装器要求的手动重启已完成。后续蓝牙输入、触觉、扳机、正常退出恢复和独立蓝牙音频共存结果见 [0.1.4 验证](diagnostic-jitter-optimization.md)。其他手柄并发和广泛游戏兼容仍未完成验收。

## 原始证据

- .local\logs\driver-install-receipt.json
- .local\logs\driver-postinstall-check.json
- .local\logs\usbip-install.log
- .local\logs\hidhide-install.log
- dist\DualSenseBridge\.local\diagnostics\before-install.json
- dist\DualSenseBridge\.local\diagnostics\after-install-before-reboot.json
