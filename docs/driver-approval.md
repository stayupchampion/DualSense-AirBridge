# 系统驱动安装前确认单

状态：**这是开发环境的安装前检查记录；安装及重启后的验证已完成。** 结果见 [installation-result.md](installation-result.md)。
核查时间：2026-10-29（Asia/Shanghai）。下列内容保留安装前说明；不代表其他电脑已经安装或通过验收。

## 拟安装组件

| 组件 | 固定版本 | 官方来源 | 当前校验 |
| --- | --- | --- | --- |
| usbip-win2 x64 | 0.9.8.1 | https://github.com/vadimgrn/usbip-win2/releases/tag/v.0.9.8.1 | SHA-256 与发布资产摘要一致；安装包 Authenticode 为 Valid |
| HidHide x64 | 1.5.230.0 | https://github.com/nefarius/HidHide/releases/tag/v1.5.230.0 | 安装包 Authenticode 为 Valid；上游未公布 SHA-256，已记录本地摘要 |

安装包位置：

- `.local\downloads\USBip-0.9.8.1-x64.exe`
- `.local\downloads\HidHide_1.5.230_x64.exe`

准确摘要、签名主体、时间戳签名和校验时间见 `.local\driver-plan.json`。签名校验仅证明当前下载包通过 Windows Authenticode 检查，不等于已经完成安装后的驱动兼容性或内存完整性验证。

## 系统影响

- USB/IP 添加虚拟 USB 控制器及相关内核驱动；安装会重启 USB 3 集线器，USB 外设可能暂时断开，可能要求重启。
- HidHide 添加系统级 HID 过滤驱动和配置工具。驱动可随系统加载，隐藏规则会持久化；本工具只在一次桥接会话中暂时修改目标 DualSense 的规则。
- 预计程序目录为 `C:\Program Files\USBip`、`C:\Program Files\Nefarius Software Solutions\HidHide`。驱动安装还会由 Windows 写入 `C:\Windows\System32\DriverStore\FileRepository`、C:\Windows\System32\drivers、C:\Windows\INF、C:\Windows\Installer、C:\Windows\System32\Tasks、C:\ProgramData\Package Cache 等驱动/安装器目录及相应驱动/服务注册表；这些不在本项目目录内，必须等用户确认后才能操作。
- **不安装原版 vDS 服务，不创建开机项、计划任务或自动桥接服务。** 本项目的 vdsd 作为随会话启动的前台子进程运行。USB/IP 安装器自身可能登记重启/关机时清理已导入设备的任务，这是第三方组件行为，需要在安装验收时记录；它不是本工具的开机桥接。
- 当前机器检测到 Microsoft Visual C++ x64 运行库 `v14.51.36247.00`。本项目后端静态链接 MSVC runtime 和 Opus，无需单独部署 Opus DLL；第三方驱动安装器自己的运行库需求按实际检查处理，不默认覆盖现有运行库。
- 采用官方签名发行包；不要求关闭 Secure Boot、内存完整性或开启测试签名。若正常安全配置下安装失败，停止并报告原因，不自动降低安全设置。

## 已知验证边界

- USB/IP 0.9.8.1 的上游发布说明提到大量变更及回归风险，且列有设备序列号设置相关已知问题。因此“最新版且签名有效”不等于适配已经验收。
- 安装前的软件测试不能证明蓝牙触觉、耳机共存、其他手柄原生连接、驱动与本机硬件兼容。
- 安装确认后仍需单独检查设备管理器状态、驱动加载状态、手柄 USB/蓝牙原生基线，再进行本工具启用与退出测试。

## 回退

停止桥接并确认恢复后，可通过 Windows 已安装的应用分别卸载这两个依赖；按提示重启。若其他软件共用 HidHide 或 USB/IP，应保留共享组件，仅移除本工具添加的配置。删除本工具文件夹不会卸载系统驱动。

## 确认范围

每次执行安装前应确认上述两个固定版本安装包及系统写入、USB 暂时断连、可能重启的影响。未经确认，程序仅提供检查和诊断，不运行安装器。
