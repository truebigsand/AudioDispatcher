# AudioDispatcher

Windows 多音频设备输出工具:捕获进入 **VB-Audio Cable** 虚拟声卡的全部系统声音,同步镜像分发到多个输出设备(扬声器 + 耳机 + 蓝牙音箱等)同时播放。

- 技术栈:.NET 10 / C# / WPF / NAudio 2.x / WinForms 托盘
- 运行环境:Windows 10/11 x64
- UI 语言:简体中文

## 快速开始

1. **安装 VB-Audio Cable**(免费):从 [vb-audio.com/Cable](https://vb-audio.com/Cable/) 下载 `VBCABLE_Driver_Pack45.zip`,解压后按其中 readme 说明安装驱动(**装完需重启**)。装好后系统会多出两个端点:
   - `CABLE Input (VB-Audio Virtual Cable)`(播放)
   - `CABLE Output (VB-Audio Virtual Cable)`(录制)
2. 打开 AudioDispatcher(首次运行会显示引导)。
3. 系统 **声音设置 → 输出** 把默认播放设备选为 **CABLE Input (VB-Audio Virtual Cable)**——所有系统声音由此进入分发器。
4. 回到 AudioDispatcher,勾选要同时出声的设备(可逐个调音量/静音/听测试音),点 **开始分发**。

> 注意:分发器运行期间系统声音都走 CABLE,退出 AudioDispatcher 后如仍指向 CABLE Input 会没有声音——请先切回原设备再退出(退出确认框会提醒)。

## 功能

- 一路输入 → N 路同步输出,内部统一 float32,自动重采样适配各设备格式;
- **音量滑块 = 该设备的系统音量(0–100%,双向同步)**:拖动应用滑块即调系统音量,在系统托盘调音量滑块自动跟随;静音按钮 = 设备静音;
- 每设备实时电平表、丢/补帧统计、200ms 测试音(♪);
- **打开应用自动开始分发**(无需手动点开始;无源时显示引导);
- 缓冲大小 10–500ms 数值可调(主界面输入,理论延迟实时提示);
- 源无声音内容自动静默(内容级检测,播放器暂停/停止即静默),恢复自动退出;源设备断开自动重建捕获;
- 目标设备热插拔:断开自动停止该路,插回按配置自动恢复;
- VB-Audio 虚拟线自动防环排除(含付费版 CABLE A/B/C/D 与新版 16ch 驱动);
- 托盘常驻:双态图标(绿=分发中/灰=暂停),左键开主窗,右键菜单(全部勾选/取消、暂停、开机自启、退出),关闭窗口最小化到托盘;
- 单实例、配置持久化(`%AppData%\AudioDispatcher\settings.json`)、滚动日志(`%AppData%\AudioDispatcher\logs\`)、开机自启(注册表 Run)。

## 构建与发布

```bash
# 调试
dotnet build src/AudioDispatcher/AudioDispatcher.csproj

# 发布(脚本自动复制产物到 桌面\Claude Outputs\AudioDispatcher\)
powershell -ExecutionPolicy Bypass -File scripts/publish-fd.ps1          # 框架依赖,~1MB(需 .NET 10 Desktop Runtime)
powershell -ExecutionPolicy Bypass -File scripts/publish-standalone.ps1  # 自包含单文件,~67MB(免运行时)
```

## 架构速览

```
其他应用 → CABLE Input → (VB-Audio 虚拟声卡) → CABLE Output
                                                    │ WASAPI 共享模式事件驱动捕获
                                                    ▼
AudioDispatcher: float32 2ch ──→ 每目标设备独立环形缓冲(10-500ms 可调)
                                     ├── 漂移补偿:水位 >92% 丢最旧至 85%,欠载补静音,实时统计
                                     ├── 重采样(WDL)至设备采样率(如不同)
                                     ├── 位深/声道转换至设备 MixFormat
                                     └── WasapiOut 事件驱动渲染
```

- 源缺失:主界面横幅引导 + 状态提示;引擎 1s 无数据静默、6s 重建捕获(10s 限频)。
- 设计规格见 [docs/spec.md](docs/spec.md)。

## 已知限制(V1)

- 源仅支持 VB-Audio Cable 系列(不做系统默认设备 loopback 捕获);
- 系统默认输出切换需手动完成(应用只引导,不写系统设置);
- 漂移补偿为阈值式整块丢/补,极端溢出可能有轻微可闻 pop(统计页可观察,后续可升级渐进式);
- 超过 2 声道的目标设备:多余通道复制左声道(兜底);源 >2ch 时平均下混为 2ch。
