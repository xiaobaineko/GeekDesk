## GeekDesk — 远程桌面（RDP / RemoteApp）修复版

这是 GeekDesk 的非官方 fork 构建，基于 [BookerLiu/GeekDesk](https://github.com/BookerLiu/GeekDesk) 上游源码与 [Baker9017/GeekDesk](https://github.com/Baker9017/GeekDesk) 的修复提交，由 GitHub Actions 自动编译。原项目为 Apache-2.0 许可证，本仓库保留其许可证与版权声明。

### 修复内容

- 修复远程桌面（RDP / RemoteApp）环境下使用一段时间后，托盘图标点不开主面板、必须重启软件的问题（对应上游 issue #149、#187 等）
- 修复面板被隐藏后需要多次点击托盘图标才能打开的问题
- 当前台程序是远程桌面客户端（mstsc / VNC）时，不再误触全局热键 / 中键呼出，避免与远程会话冲突

### 其他变化（相对上游 2.5.15）

- 目标框架升级到 .NET Framework 4.8，版本 2.6.2.2
- 新增 Listary 风格的 Quick Switch 文件夹跟随功能（默认关闭，可在设置中开启）
- 屏幕右上角右键打开托盘同款菜单
- 已禁用自动更新检查（本 fork 版本号与上游不一致，避免被上游更新提示干扰）

### 使用说明

- 系统要求：Windows 10/11，需 .NET Framework 4.8 运行时（系统一般自带）
- 下载附件 zip 后完整解压，运行文件夹内的 `GeekDesk.exe`；不要单独拷出 exe，其依赖的 dll 位于同目录的 `lib` 子文件夹
- 本程序未做代码签名，首次运行 Windows SmartScreen 可能提示拦截，点「更多信息 → 仍要运行」即可

### 安全性说明

发布前已对 fork 的全部改动提交做过代码审查：无新增第三方依赖包（仅引用 .NET 框架自带的 UIAutomation），无下载执行、无数据外传；QuickSwitch 的跨进程路径注入仅在该功能开启时工作，默认关闭。
