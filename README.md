# 每天计划

一个为自己做的 Windows 桌面日程工具：按天安排任务，记录开始时间、结束时间和状态，并在任务开始与结束时进行语音提醒。

界面使用 C# / WPF，业务与数据层使用 C++20，数据保存在本机 SQLite3 数据库中。

## 功能

- 按天查看任务，支持前一天 / 后一天 / 今天和日历跳转
- 新建、编辑、删除任务
- 每个任务包含：标题、备注、开始时间、结束时间、状态
- 状态流转：待办 → 进行中 → 已完成，也可以取消或恢复
- 任务开始和结束时：
  - 弹出应用内提示
  - 使用 Windows 语音引擎朗读提醒
- 语音提醒可在标题栏开关，选择会保存
- 一次性 / 跨天任务均可（结束时间必须晚于开始时间）
- 可以把任务设为“每天重复”：从首次日期开始，每天在设定时间段出现，并在每天的开始、结束时间提醒
- 重复任务的完成状态按天独立记录，某天标记完成后不影响其他日期
- 所有数据仅保存在本机，WAL 模式 SQLite 数据库

## 技术架构

```
┌──────────────────────────────┐
│  WPF 前端（C# / .NET 10）     │
│  MainWindow / TaskEditor     │
│  System.Speech（Windows SAPI）│
└──────────────┬───────────────┘
               │ P/Invoke（C ABI，UTF-8）
┌──────────────▼───────────────┐
│  DailyPlanCore.dll（C++20）   │
│  校验、任务 CRUD、提醒轮询     │
│  JSON 编解码（nlohmann/json） │
└──────────────┬───────────────┘
               │ SQLite3 C API
┌──────────────▼───────────────┐
│  SQLite3（WAL）               │
│  %LOCALAPPDATA%\DailyPlan\   │
│      dailyplan.db            │
└──────────────────────────────┘
```

### 为什么这样分层

- 前端只负责交互与展示，业务规则（时间校验、提醒去重）集中在 C++ 核心。
- C++ 通过稳定的 C ABI 暴露接口，C# 侧用 P/Invoke 调用，不依赖 C++/CLI。
- 提醒由前端每秒调用一次 `dp_poll_reminders`，C++ 在同一个事务中“领取并标记”到期提醒，保证同一提醒不会被重复播报。
- 超过 2 分钟的旧提醒会被静默消费，避免软件重启后补播昨天的提醒。

## 环境要求

- Windows 10 / 11（x64）
- CMake 3.21+
- 首次构建需要联网下载工具链（约 350 MB）
  - .NET 10 SDK（项目内便携安装）
  - MSYS2 MinGW-w64（GCC + make + SQLite 开发包，使用国内镜像）

工具链会下载到 `.tools/`，**不会修改系统 PATH 或全局安装**。发布的应用是自包含的，目标电脑无需预装 .NET 运行时。

## 构建与运行

在仓库根目录执行：

```powershell
# 1. 首次运行：下载便携 .NET SDK 与 MSYS2 MinGW 工具链
powershell -ExecutionPolicy Bypass -File .\scripts\setup-toolchain.ps1

# 2. 构建 C++ 核心、编译 C#、运行集成测试、发布应用
powershell -ExecutionPolicy Bypass -File .\scripts\build.ps1

# 3. 启动
powershell -ExecutionPolicy Bypass -File .\scripts\run.ps1
```

构建完成后可直接运行：

```
artifacts\app\DailyPlan.exe
```

如果已经构建过，只想启动：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\run.ps1 -NoBuild
```

### 可选参数

```powershell
# Debug 构建
.\scripts\build.ps1 -Configuration Debug

# 跳过集成测试
.\scripts\build.ps1 -SkipTests
```

## 目录结构

```
DailyPlan/
├─ native/                     C++ 核心
│  ├─ include/dailyplan_core.h C ABI 头文件
│  ├─ src/core.cpp             校验、CRUD、提醒轮询
│  └─ third_party/nlohmann/    JSON 单头文件库
├─ src/
│  ├─ DailyPlan.Client/        C# P/Invoke 封装与领域模型
│  └─ DailyPlan.App/           WPF 界面
│     ├─ MainWindow.xaml       主界面
│     ├─ TaskEditorWindow.xaml 新建 / 编辑任务
│     ├─ Services/             语音、设置、路径
│     └─ Themes/Theme.xaml     统一视觉样式
├─ tests/DailyPlan.Client.Tests/  C++ ↔ C# ↔ SQLite 集成测试
├─ scripts/                    工具链、构建、运行、图标脚本
└─ artifacts/                  构建产物（已忽略）
```

## 数据位置

| 内容 | 路径 |
| --- | --- |
| 数据库 | `%LOCALAPPDATA%\DailyPlan\dailyplan.db` |
| 设置 | `%LOCALAPPDATA%\DailyPlan\settings.json` |
| 错误日志 | `%LOCALAPPDATA%\DailyPlan\error.log` |

主界面底部有“打开数据目录”按钮。数据库使用 WAL 模式，目录中可能同时出现 `-wal` / `-shm` 文件，属于正常现象。

## 提醒规则

- 任务状态为“待办”或“进行中”时才会触发提醒。
- 到开始时间朗读：“上午9点，任务「xxx」开始。”
- 到结束时间朗读：“上午10点，任务「xxx」已到结束时间。”
- 已完成、已取消的任务不会再提醒。
- 每天重复的任务按“当天”的状态判断：某天标记完成或取消后，当天不再提醒，第二天仍会正常提醒。
- 重复任务的时间段最长 24 小时，可以跨午夜（例如 23:00 – 次日 01:00）。
- 软件需要保持运行；应用内提示需要窗口可见，语音提醒在最小化时仍可工作。
- 如果系统没有可用语音包，仍会显示应用内提示，并在底部说明语音不可用。可在 Windows 设置 → 时间和语言 → 语音 中安装中文语音。

## 当前限制与后续可以做的事

- 重复规则目前只支持“每天”，暂不支持每周、工作日或自定义间隔
- 暂不支持系统托盘常驻和开机自启
- 暂不支持任务搜索、分类、标签
- 语音使用本机 SAPI，不同 Windows 版本音色可能不同
- 后续可考虑：每周重复、托盘提醒、数据导出、周视图、统计

## 开发说明

C++ 接口定义见 [dailyplan_core.h](native/include/dailyplan_core.h)，所有字符串均为 UTF-8，返回的字符串需用 `dp_free_string()` 释放。

所有时间统一使用本地时间字符串 `yyyy-MM-ddTHH:mm:ss`，前后端按同一格式传递，核心只负责严格校验与字典序比较。

C++ 核心以 x64 DLL 形式交付，SQLite 与 MinGW 运行库均静态链接，因此只需把 `DailyPlanCore.dll` 放在 `DailyPlan.exe` 同目录即可运行。
