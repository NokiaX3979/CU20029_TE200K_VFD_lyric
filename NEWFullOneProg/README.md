# VFDLyric (Sample.CMD)

把原来「SMTCPipe（监听 SMTC）+ testvsnew.py（搜歌词 + 推 VFD）」两个进程合并成一个 CLI 程序。

原来的流程需要手动开两个窗口、还要靠命名管道传文本；现在只剩一个 exe：

```
Sample.CMD.exe
   │
   ├─ WindowsMediaController ── 直接订阅 SMTC 事件（歌名/歌手/专辑/播放状态/时间轴）
   │                              不再经过管道、不再解析文本
   ├─ Lyrics ─────────────────── 网易云搜索 + 歌词抓取 + LRC 解析（含翻译合并）
   └─ Vfd ────────────────────── 语言探测 → 硬件切码 → RS-232 串口 → CU20029-TE200K
```

## 构建

```
cd Sample.CMD
dotnet build
```

产物：`bin\Debug\net472\Sample.CMD.exe`

> 上游的 `WindowsMediaController` 是 net461~net10.0 的多目标库，老版本 SDK（例如 5.0.x）
> 解析不了列表里的新 TFM，会直接报 NETSDK1045。已在
> `WindowsMediaController\WindowsMediaController.csproj` 里把默认目标收窄为 net472；
> 需要还原完整矩阵时用 `dotnet build /p:BuildFullTargetMatrix=true`。

## 运行

```
Sample.CMD.exe [选项]

  --pipe            强制启用命名管道广播（\\.\pipe\MusicInfoPipe），兼容旧的 testvsnew.py
  --config <文件>    指定配置文件路径（默认：exe 同目录 vfdlyric.json）
  --verbose         额外的调试诊断（每秒播放位置、忽略的会话等）
  -h, --help        帮助
```

逐帧歌词输出由配置项 `ShowFrameLog` 控制，不需要命令行参数。

**首次运行**会进入配置向导（列出检测到的 COM 端口），结果写入 exe 同目录的
`vfdlyric.json`；之后每次启动直接读取。若文件缺失或字段非法，会自动重新进入向导。

```json
{
  "SerialPort": "COM4",
  "BaudRate": 38400,
  "Brightness": 3,
  "DisplayWidth": 20,
  "LyricOffsetSeconds": 0.0,
  "ScrollPauseSeconds": 0.6,
  "RefreshIntervalMs": 60,
  "ShowFrameLog": false,
  "IdleDimSeconds": 5,
  "IdleOffSeconds": 30,
  "SessionPollSeconds": 5,
  "FetchLyrics": true,
  "SearchLimit": 5,
  "DurationToleranceSeconds": 8.0,
  "HttpTimeoutSeconds": 6,
  "EnablePipe": false,
  "PipeName": "MusicInfoPipe"
}
```

| 字段 | 说明 |
|---|---|
| `LyricOffsetSeconds` | 歌词整体偏移，正数提前 / 负数延后（同步不准时微调这个） |
| `Brightness` | 1=25% 2=50% 3=75% 4=100%（热键调整后会写回这里） |
| `RefreshIntervalMs` | 渲染帧间隔，60ms ≈ 16 FPS |
| `ShowFrameLog` | 是否把每帧歌词输出到控制台（调试用，默认关闭） |
| `IdleDimSeconds` | 暂停/空闲多少秒后变暗到 25%，`0` = 关闭该级 |
| `IdleOffSeconds` | 暂停/空闲多少秒后关屏（Display Power OFF），`0` = 关闭该级 |
| `SessionPollSeconds` | 播放状态复查间隔，只兜底捕获丢失的暂停/停止事件，`0` = 关闭 |
| `FetchLyrics` | 关掉就只监听不联网（不显示歌词） |
| `EnablePipe` | 是否同时广播命名管道 |

## 热键

单键即时响应，不需要回车（stdin 被重定向时热键自动禁用，只剩 Ctrl+C 可用）。

| 键 | 功能 |
|---|---|
| `↑` / `+` | 亮度 +1（25% → 100% 循环），写回配置 |
| `↓` / `-` | 亮度 -1 |
| `P` | 屏幕电源 开/关 |
| `C` | 光标显示 开/关 |
| `R` | 反色显示 开/关 |
| `H` | 显示热键帮助 |
| `Q` / `Esc` | 退出（等同 Ctrl+C，会先清屏再关串口） |
| 其它任意键 | 唤醒屏幕 |

## 自动息屏（防烧屏）

VFD 的荧光粉长时间显示固定图案会烧出残影。计时**从暂停那一刻开始**
（程序启动后没有音乐时从启动时刻开始）：

```
暂停/空闲 ≥ IdleDimSeconds (默认 5s) → 亮度降到 25%
暂停/空闲 ≥ IdleOffSeconds (默认 30s) → Display Power OFF，屏幕全黑
```

逐级恢复：关屏 → 恢复播放或按任意键 → 先开屏并补画当前歌词 → 再恢复原亮度。
用户设定的亮度会被记住，所以从"变暗"退回时回到原来那一档，不会被永久改成 25%。

按 `P` 手动关屏/开屏后会挂起自动息屏逻辑（状态栏显示 `屏幕关(手动)`），
直到恢复播放或按其它热键，避免手动状态和自动逻辑互相覆盖。

## 底部状态栏

交互式运行时，控制台最后一行常驻显示：

```
播放 | 星夜万華鏡 - Yonder Voice/瑶山百霊 | 01:23 / 04:19 | 亮度 3 | 光标关 | 反色关 | 屏幕开
```

输出被重定向（写日志文件、管道）时自动禁用，不会混入光标定位转义字符。

## 代码结构

| 文件 | 职责 |
|---|---|
| `Program.cs` | 入口：命令行、配置、串口、SMTC 订阅、会话过滤、播放状态复查 |
| `AppConfig.cs` | 配置模型 + 读写 + 首次运行交互向导 |
| `ConsoleLog.cs` | 多线程安全的控制台日志 |
| `VfdLyricController.cs` | 核心：播放锚点、歌词时间轴匹配、滚屏偏移计算、逐帧推送、自动息屏状态机 |
| `Vfd/VfdDisplay.cs` | 串口封装 + VFD 指令 + 语言探测/切码/编码 |
| `HotkeyHandler.cs` | 控制台单键热键 |
| `StatusBar.cs` | 控制台底部常驻状态栏 |
| `Lyrics/LyricService.cs` | 搜索 -> 选最佳匹配 -> 抓歌词，过期结果自动丢弃 |
| `Lyrics/NeteaseClient.cs` | 网易云 HTTP 接口 |
| `Lyrics/LyricParser.cs` | LRC 解析与原文/翻译按时间合并 |
| `Lyrics/StringSimilarity.cs` | `difflib.SequenceMatcher.ratio()` 的等价实现 |
| `PipeBroadcaster.cs` | 原有的命名管道广播（`--pipe` 时启用） |

## 从 Python 版移植时处理掉的几个坑

1. **代码页超集问题**
   `.NET` 的 936/950/949/932 分别是 GBK/CP950/CP949/CP932，都是标准编码的**超集**，
   而屏幕字库只认 GB2312-80 / Big-5 / KSC5601-87 / JIS X0208。直接用 936 会把「體」「後」
   这类 GBK 独有字符编码成屏幕查不到的码位显示乱码。简中档案因此改用代码页 **20936**
   （真正的 GB2312-80，字表恰好 6763 汉字），其余字库再加一层按规格书的字节范围校验。
   测试样例 `繁體中文測試`：Python 判定 Big5，旧写法判定 GB2312（乱码），现在是 Big5。

2. **播放位置的锚点策略**
   实测 foobar2000 的 SMTC `Position` **不会随播放前进**，只返回上一次的值。
   原先加的 5 秒轮询每 5 秒用这个陈旧值重新锚定，导致播放位置每 5 秒倒退约 4 秒、
   歌词反复从头重放。现在：
   - 轮询只刷新播放状态，不碰位置；
   - 真实的时间轴事件带迟滞判断：数值与上次上报完全相同且比外推位置靠后 → 判定为陈旧数据，忽略；
     否则认为是真实的跳播，重新锚定。

3. **多会话干扰**
   浏览器视频、其它播放器会同时上报 SMTC 事件。现在优先跟随"正在播放"的会话，
   其余会话的事件丢弃；接管新会话时主动补拉一次曲目与时间轴。

4. **暂停期间不再外推位置**
   原来的实现里播放锚点在暂停期间照样按墙上时钟外推，一旦暂停很久再恢复，
   位置会凭空跳一大截。现在暂停时冻结进度，恢复播放后从冻结点继续。

5. **缺字显示改为方块**
   Python 用的是 `encode(errors='replace')`，缺字时只能得到编码器写死的 `?`，
   想换就得自己写 error handler。现在编码器由本项目实现，
   缺字直接替换为 `0xDB`（PC437 字符表里的实心方块 █）。

   ⚠️ 已知限制：规格书写明"2 字节字符模式下，若 c1 落在有效范围内就按双字节处理"。
   中日韩三种字库的 c1 有效范围都是 `A1h~FEh`，`0xDB` 正好落在里面，
   所以在 GB2312 / Big5 / KSC5601 模式下单独发出 `0xDB` 会被当成双字节首字节、
   吃掉后面一个字符。只有 ASCII（2 字节模式关闭）和 Shift-JIS（c1 = 81h~9Fh/E0h~EFh）
   能安全单字节直发。缺字本身极少触发，若真遇到并发现"某字后面的字不见了"，
   需要在 `BuildReplacementBytes` 里补一个临时关闭 2 字节字符模式的指令序列。

6. **半成品文件**
   原 `PushToSerial.cs` 是一份编译不过的半成品，已由上面的模块替换；`originalProgram.cs`
   被 `Program.cs` 取代。

## 与 Python 版的行为一致性

语言探测、LRC 解析与合并、`SequenceMatcher` 相似度、字符替换表、滚屏偏移公式
均逐条对齐，并用同一组输入做了 C#/Python 对拍（46 行输出，除两处测试用例的
标签文字外**逐字节一致**）。

已知差异：Python 用 `zhconv` 做简繁转换，C# 用 `ZhConv.Net`（zhconv 的 C# 移植），
后者只做字级转换，没有 `zh-tw` 的台湾用词词表（`软件`→`軟件` 而非 `軟體`）。
对歌词显示没有影响。

## 硬件

CU20029-TE200K，20×2 字符，RS-232，默认 38400 8N1（J1/J2 跳线 OPEN/OPEN）。
用到的指令：

| 指令 | 含义 |
|---|---|
| `0Ch` | Display Clear |
| `1Fh 02h` / `1Fh 03h` | Vertical / Horizontal scroll mode |
| `1Fh 24h x y` | Cursor set（x: 1~20, y: 1~2） |
| `1Fh 28h 67h 02h n` | 2-byte character ON/OFF |
| `1Fh 28h 67h 03h n` | 2-byte character type：00=Shift-JIS 01=KSC5601 02=GB2312 03=Big5 |
| `1Fh 43h n` | Cursor display（0=关，1=块状光标） |
| `1Fh 58h n` | Brightness（1=25% 2=50% 3=75% 4=100%） |
| `1Fh 72h n` | Reverse display（0=关，1=开） |
| `1Fh 28h 61h 40h n` | Display power（0=省电关屏，1=开屏） |