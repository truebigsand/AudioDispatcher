# AudioDispatcher 技术规格文档

版本:V0.9(设计定稿) | 日期:2026-09-03 | 平台:Windows 10/11 x64 | 目标框架:.NET 10

---

## 1. 产品概述

AudioDispatcher 是一个常驻托盘的 Windows 桌面工具:**捕获进入 VB-Audio Cable 虚拟声卡的全部声音,镜像分发到用户勾选的多个输出设备同时播放**(扬声器 + 耳机 + 蓝牙音箱等)。

- 名称:AudioDispatcher
- 形态:WPF 主窗口 + WinForms 托盘图标(完整托盘方案)
- 技术栈:C# / .NET 10 / WPF / NAudio 2.x / WinForms.NotifyIcon
- 发布:自包含单文件 exe 与框架依赖双配置
- UI 语言:简体中文

## 2. 信号链路(端到端)

```
[用户侧设置] 系统默认输出设备 = "CABLE Input (VB-Audio Virtual Cable)"
       │
其他应用(浏览器/播放器/游戏) ──播放──> CABLE Input
       │ VB-Audio 虚拟声卡内部连线(安装即存在,无需我们驱动)
       ▼
CABLE Output(捕获端点)
       │
       ▼
AudioDispatcher 源捕获(WASAPI 共享模式事件驱动)
       │ 内部统一 float32 格式
       ├──> [环形缓冲 #1] ── 增益/静音 ── 重采样(如需要)── 渲染 ──> 扬声器
       ├──> [环形缓冲 #2] ── 增益/静音 ── 重采样(如需要)── 渲染 ──> 耳机
       └──> [环形缓冲 #N] ── 增益/静音 ── 重采样(如需要)── 渲染 ──> 蓝牙音箱
```

要点:
- 源只支持 VB-Audio Cable 系列(免费版单线 CABLE;付费版 CABLE A/B/C/D、HiFi Cable 同样识别),**不做**系统默认设备 loopback。
- 系统默认输出设备的切换由用户手动完成(应用只给引导提示,不写系统设置)。
- 所有目标设备从同一分发器取数,设备间相对延迟只差各自渲染缓冲(可压到 10–20ms 一致),不存在"原声 vs 复制声"混叠。

## 3. 项目文件结构

```
AudioDispatcher/
├─ AudioDispatcher.sln
├─ src/AudioDispatcher/
│  ├─ AudioDispatcher.csproj          # net10.0-windows, UseWPF, UseWindowsForms
│  ├─ Program.cs                      # 单实例检查 → App
│  ├─ App.xaml / App.xaml.cs          # 资源、异常兜底、启动 TrayIcon
│  ├─ MainWindow.xaml / MainWindow.xaml.cs
│  ├─ Audio/
│  │  ├─ DeviceService.cs             # 端点枚举、热插拔监听、防环过滤
│  │  ├─ SourceCapture.cs             # WASAPI 捕获 CABLE Output(NAudio WasapiCapture)
│  │  ├─ SampleRingBuffer.cs          # 每设备独立环形缓冲(float32)
│  │  ├─ RingBufferWaveProvider.cs    # IWaveProvider 适配:缓冲 → NAudio WasapiOut
│  │  ├─ TargetOutput.cs              # 单设备渲染流:增益/静音/补偿/电平/统计
│  │  ├─ DriftCompensator.cs          # 时钟漂移补偿算法
│  │  ├─ DispatcherEngine.cs          # 核心编排:启动/停止/设备增删/状态事件
│  │  ├─ TestToneGenerator.cs         # 1kHz 测试音(设备识别用)
│  │  └─ MixFormatHelper.cs           # 设备格式查询、float32 转换器工厂
│  ├─ UI/
│  │  ├─ TrayIcon.cs                  # NotifyIcon 封装、双态图标
│  │  ├─ ViewModels/
│  │  │  ├─ MainViewModel.cs
│  │  │  ├─ DeviceItemViewModel.cs
│  │  │  └─ SourceStatusViewModel.cs
│  │  ├─ Icons/                       # 托盘双态 .ico(构建脚本生成)
│  │  └─ Converters/
│  ├─ Settings/
│  │  ├─ AppSettings.cs               # 设置模型(JSON 序列化)
│  │  └─ SettingsService.cs           # 读写 %AppData%\AudioDispatcher\settings.json
│  ├─ SingleInstance.cs
│  └─ Logging/AppLog.cs               # 滚动文件日志
├─ scripts/
│  ├─ publish-fd.ps1                  # 框架依赖发布
│  ├─ publish-standalone.ps1          # 自包含单文件发布
│  └─ make-icons.ps1                  # 生成托盘图标(绿=分发中/灰=暂停)
└─ docs/spec.md                       # 本文档副本
```

## 4. 音频子系统规格

### 4.1 设备枚举与防环(DeviceService)

- 枚举全部渲染端点(DataFlow.Render,Role 任意),供目标设备列表。
- 源设备在**捕获端点**(DataFlow.Capture)中按名称匹配 `CABLE Output`;找不到时 UI 显示引导。
- 防环过滤规则(应用于目标列表):
  1. 排除 FriendlyName 以 `CABLE` 开头且以 `Input` 结尾的端点(VB-Audio 虚拟线的 render 端,含付费版 CABLE A/B/C/D 与 HiFi Cable);
  2. 排除与当前源设备同名设备 ID 前缀相同的端点(兜底);
  3. 规则留扩展点:settings.json 中 `blockedDeviceNames[]` 可追加。
- 热插拔:实现 `IMMNotificationClient` 回调(NAudio `MMDeviceEnumerator` + `MMNotificationClient`),设备增删/状态变化 → 通知 UI 刷新;被勾选设备消失 → 自动停该路、行置灰"已断开",托盘气泡提示一次。

### 4.2 源捕获(SourceCapture)

- NAudio `WasapiCapture`,`ShareMode.Shared`,设备 = CABLE Output 捕获端点。
- 音频格式:读取设备 MixFormat;以捕获设备的原生采样率/通道数接收(float32 由 NAudio 转换或按 MixFormat 位深接收后转 float32),**内部统一 float32 立体声或跟随源通道数**(≥2 按 2 声道分发,>2 下混到 2)。
- 静音检测:捕获线程内对每 250ms 窗口计算 RMS;RMS < -60 dBFS 判定"无输入",更新 `SourceStatusViewModel`(UI 黄色提示)。
- 失败重试:启动失败(设备被占用/不存在)按 1s → 5s → 30s 退避重试,期间 UI 保持可操作。
- 采样率变更(用户在 VB-Cable 属性里改格式)→ 触发引擎级重建(见 4.6)。

### 4.3 分发缓冲与渲染流(TargetOutput)

每个已启用目标设备 = 一个 `TargetOutput`,内含:

- `SampleRingBuffer`:容量 = 用户设置的 `bufferMs`(10–500ms 数值输入,整数)。写端唯一 = 捕获线程;读端唯一 = 该设备渲染线程。索引用 `Interlocked` 前后沿指针,float32 数组,容量对齐 4 帧。
- 增益链:设备音量(0–1 映射滑块 0–150%,>100% 允许但提示削波)+ 静音开关,均为 `volatile` 字段,渲染线程每回调读取,无锁。
- `RingBufferWaveProvider`:把环形缓冲适配为 NAudio `WasapiOut` 的输入 `IWaveProvider`,输出 WaveFormat = **目标设备 MixFormat**(采样率/位深/声道数),内部做 float32 → PCM 位深转换;采样率不同时先经重采样。
- 渲染端:NAudio `WasapiOut`(`ShareMode.Shared`,事件同步模式),每设备独立实例。初始化失败(被独占等)→ 该行标记"无法启动:设备被占用",不影响其他设备。

### 4.4 时钟漂移补偿(DriftCompensator)

捕获端与每个渲染设备各有独立时钟,长期运行必然漂移。补偿按"缓冲水位"执行:

```
每渲染回调(事件粒度 ≈ 10ms = N 帧):
  需求帧数 dN = N
  可读帧数 avail = ring.Available()
  ── 欠载(avail < dN):
     读尽 avail,补静音 (dN - avail);underrunCount += (dN - avail)
  ── 溢出(avail > 容量 × 0.92):
     丢弃最旧 (avail - 容量×0.85) 帧(整块丢,按水位差计算);overrunCount += 丢弃帧数
  ── 正常区间(0.5 ~ 0.92 容量):
     正常读取 dN 帧
```

- 丢/补以**帧**为单位计数并累计到 UI(每秒刷新一次显示),不弹窗。
- 补偿粒度说明:阈值式整块补偿在溢出时会产生极轻微可闻 pop;V1 接受此方案,统计页面持续观察丢补频率,若不可接受再升级为"每回调微调 1 帧"的渐进补偿。
- 用户调小 `bufferMs` 会即时重建该设备环形缓冲与渲染流(生效于下次设备重启或立即热重建)。

### 4.5 重采样

- 渲染目标采样率 ≠ 内部采样率时启用重采样。实现:每设备一个基于 NAudio `WdlResamplingSampleProvider`(高通质量)的流式实例,置于环形缓冲读取与位深转换之间。
- 重采样输入输出均为 float32,目标采样率 = 设备 MixFormat 采样率。固定 44.1k 与 48k 交叉场景为最常见,WD 算法质量足够。

### 4.6 采样率/格式变更处理

捕获设备 MixFormat 变化(用户改 VB-Cable 格式):
1. 停止全部 TargetOutput(保留环形缓冲内容?不保留——格式变了);
2. 重建 SourceCapture 与内部格式;
3. 按新内部格式重启全部启用的 TargetOutput;
4. 状态栏提示"检测到源格式变更,已自动重建"。

### 4.7 电平表(LevelMeter)

- 源:捕获线程 250ms 窗口 RMS → `volatile float`,UI 采样显示。
- 每设备:渲染线程对该回调实际输出帧算峰值/RMS → `volatile float`。
- UI 用 `DispatcherTimer` 50ms(20Hz)轮询 ViewModel,进度条按对数刻度(log 映射,-60dB..0dB)显示。

### 4.8 测试音(设备识别)

- 每设备行提供"播放测试音"按钮:向**该设备专用**的测试音通道注入 1kHz 正弦 200ms(-18 dBFS,自带淡入淡出 5ms 防爆音),经同一增益/渲染路径输出,便于用户确认"哪个设备是哪台音箱"。
- 实现:`TestToneGenerator` 作为环形缓冲写入端的第二数据源(与捕获写入互斥,测试音期间捕获写入暂停)。

## 5. 线程模型

| 线程 | 职责 | 说明 |
|---|---|---|
| UI(STA) | WPF 窗口、托盘 | 永不直接触碰音频缓冲 |
| 捕获线程 | WASAPI 事件回调 → 写所有环形缓冲 | NAudio WasapiCapture 回调线程(MMCSS 音频任务优先级) |
| 渲染线程 ×N | 各自 WasapiOut 事件回调 → 读自己环形缓冲 → 增益 → 电平 → 重采样 → 写设备 | 每设备独立;漂移补偿在该线程内做 |
| 定时器 | DispatcherTimer(50ms)轮询电平/统计到 UI | 读 volatile,不锁 |

线程间通信:
- 命令(UI→引擎):经 `DispatcherEngine` 的同步方法(引擎内部状态锁保护)或 `Channel`;音量/静音/暂停等热参数走 volatile 字段直接生效,不排队。
- 状态(引擎→UI):事件 + `SynchronizationContext.Post`,不可变快照(计数、状态枚举)。

停止顺序(退出时):引擎先停止捕获(写端)→ 再逐个停止渲染(读端)→ 释放 COM 资源 → 保存设置 → 退出。

## 6. UI 规格(简体中文)

### 6.1 主窗口(约 680×540,可缩放,最小 600×480)

```
┌ AudioDispatcher ──────────────────────────────────────┐
│ ① 源状态卡                                             │
│    [● 已连接] CABLE Output · 48000 Hz · 2ch            │
│    未收到音频数据时:黄色横幅"没有声音进入 CABLE,请…"   │
│    VB-Cable 缺失时:横幅 + 超链接"下载 VB-Audio Cable"  │
│    [开始分发] [停止] 按钮(引擎总开关)                   │
├──────────────────────────────────────────────────────┤
│ ② 目标设备列表(每行):                                  │
│    ☑ 扬声器 (Realtek …)   48kHz/24bit   [🔊音量 ──] [静音] [电平████] 丢0 补0 [♪测试]│
│    ☐ 耳机 (…)             (同上结构)                  │
│    ☐ 已断开设备…(置灰,复选框不可用)                    │
├──────────────────────────────────────────────────────┤
│ ③ 缓冲/延迟区:                                         │
│    缓冲大小 [  50  ] ms (10–500 整数,非法输入回退上次值)│
│    理论延迟 ≈ 捕获10ms + 缓冲 + 渲染事件10ms = 约70ms   │
│    漂移补偿统计:总丢帧/总补帧(每秒刷新)                 │
├──────────────────────────────────────────────────────┤
│ 状态栏:引擎状态 · 分发中(3 设备) · 捕获 48kHz · 单实例  │
└──────────────────────────────────────────────────────┘
```

- 设备行控件:CheckBox(勾选即时启停该路)、音量 Slider(0–150%,默认 100;拖动防抖 150ms 后应用并持久化)、静音 ToggleButton、电平 ProgressBar、丢/补文本、测试音按钮。
- "启用全部 / 停用全部"按钮位于列表标题行。

### 6.2 托盘(TrayIcon)

- 左键单击:显示主窗口(已在则置前)。
- 右键菜单:`打开主窗口` / `启用全部` / `停用全部` / `暂停分发(勾选项)` / `开机自启(勾选项,默认关)` / `退出`。
- 图标双态:绿(分发中)/ 灰(暂停或未启动);悬浮 Tooltip 显示 "AudioDispatcher — 分发中(2 设备)"。
- 关闭窗口 = 最小化到托盘(拦截 Closing);退出仅走托盘菜单(带一次确认弹窗)。
- 图标由 `scripts/make-icons.ps1` 生成两个 16×16/32×32 多尺寸 .ico 嵌入资源。

### 6.3 使用引导(首次运行)

- 首次运行(settings.json 不存在)自动打开主窗口并显示引导横幅(非模态,可关闭):
  1. 下载并安装 VB-Audio Cable(vb-audio.com/Cable,免费);
  2. 系统"声音设置 → 输出"把默认设备选为 **CABLE Input (VB-Audio Virtual Cable)**;
  3. 回到 AudioDispatcher,勾选要同时出声的设备,点"开始分发"。
- 引导横幅持续显示在源状态卡,直到检测到 CABLE 端点。

## 7. 配置持久化(settings.json)

路径:`%AppData%\AudioDispatcher\settings.json`,任何改动防抖 500ms 保存。

```json
{
  "version": 1,
  "bufferMs": 50,
  "engineAutoStart": false,
  "startWithWindows": false,
  "minimizeToTray": true,
  "sourceDeviceId": null,
  "blockedDeviceNames": [],
  "targets": [
    { "deviceId": "{0.0.0.00000000}.{...}", "enabled": true,
      "volume": 1.0, "muted": false }
  ],
  "windowTop": null, "windowLeft": null,
  "windowWidth": 680, "windowHeight": 540
}
```

- 恢复策略:启动时按 deviceId 恢复目标行状态;设备不存在则行置灰但保留配置(防止临时插拔丢失设置)。
- 开机自启:注册表 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 写入 `AudioDispatcher = <exe路径>`(框架依赖发布时指向 exe;自包含同理),取消勾选即删除键值。

## 8. 单实例与生命周期

- 单实例:`CreateMutex("AudioDispatcher_SingleInstance")`;第二实例启动 → 通知已运行实例显示主窗口后退出自身。
- 应用退出流程:引擎 StopAll → 若本应用曾自动切换过默认输出则恢复(本项目不做自动切换,无此逻辑)→ 保存设置 → 托盘图标销毁 → 退出。
- 崩溃兜底:AppDomain/DispatcherUnhandledException 记录日志 + 托盘气泡提示;不自动重启(避免声音循环崩溃)。

## 9. 日志

- `%AppData%\AudioDispatcher\logs\app-yyyyMMdd.log`,按天滚动,保留 7 天。
- 级别:Info(启停、设备增删、格式变更)/ Warn(欠载、溢出、重试)/ Error(异常堆栈)。
- 统计区与日志共享埋点:丢补计数在 Warn 级只记"开始发生"与"恢复",避免刷屏。

## 10. 边界情况清单(V1 覆盖)

| 情况 | 行为 |
|---|---|
| VB-Cable 未安装 | 源状态横幅引导下载;目标列表仍可配置 |
| CABLE 存在但无声音进入 | 源状态黄色"未收到音频数据";渲染流照跑(静音) |
| 目标设备被其他应用独占 | 该路启动失败提示"设备被占用",其他路不受影响 |
| 蓝牙音箱断开 | IMMNotification 事件 → 停该路、行置灰、托盘气泡一次 |
| 源格式被用户修改(如 44.1→96k) | 自动重建全部(4.6) |
| Windows 音频服务重启 | 设备枚举变空 → 引擎进入等待,周期重扫(30s),恢复后按配置重启 |
| 音量 > 100% 削波 | 滑块区显示警告符号(不阻止) |
| 第二实例 | 唤起主窗口后退出 |
| 目标列表含 VB 线 Input | 防环规则永久排除(4.1) |

## 11. 发布构建

csproj 关键属性:`net10.0-windows`、`UseWPF`、`UseWindowsForms`、`InvariantGlobalization`、`Nullable`、`PublishSingleFile` 由 publish 参数控制。

```
# 框架依赖(体积 ~1–3MB,目标机需 .NET 10 Desktop Runtime)
scripts/publish-fd.ps1        → dist/framework-dependent/AudioDispatcher.exe + deps

# 自包含单文件(免运行时,~80–150MB)
scripts/publish-standalone.ps1 → dist/standalone/AudioDispatcher.exe(单文件)+ zip
```

产物同时复制到桌面 Claude Outputs 对应子目录。

## 12. 本机验证计划

前置条件:本机需已安装 VB-Audio Cable 与 .NET 10 SDK(实现阶段先核查;若缺 VB-Cable,向用户确认后自行下载安装,或先用其他捕获端点受限验证)。

1. **格式链路**:启动 → 确认捕获 48kHz;播放音乐 → 所有勾选设备同时出声。
2. **防环**:确认目标列表不含 "CABLE Input"。
3. **测试音**:逐设备点 ♪,确认每个按钮只响对应设备(多设备识别)。
4. **漂移**:连续播放 30 分钟,观察丢/补统计不持续增长(每分钟 < 数帧)。
5. **缓冲调节**:缓冲 10ms 与 500ms 两端值切换,无崩溃、延迟差异可感。
6. **热插拔**:播放中拔掉 USB 声卡/蓝牙 → 对应行置灰不蓝屏;插回 → 恢复可选。
7. **格式变更**:VB-Cable 属性改 44100Hz → 引擎自动重建,声音不断流超过 2s。
8. **托盘与退出**:关窗最小化;托盘退出后确认系统默认输出仍为 CABLE Input 时无声(预期行为,引导页已说明恢复方法)。
9. **单实例**:二次启动唤起主窗。

## 13. V1 范围外(后续候选)

按应用路由(如 EarTrumpet);自建签名虚拟驱动;EQ/滤波器;每设备独立延迟微调;音频录制/回放缓存;网络投送;开机自启失败通知;多语言。
