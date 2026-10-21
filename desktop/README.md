# 聊伴 · 群聊推荐

在微信或 QQ 群聊旁观察可见文字，汇总吃饭、出行的时间、地点、预算和偏好，在侧栏展示可比较的方案。普通闲聊不主动推荐；新增条件会更新方案，明确取消安排时会撤下旧推荐。

目前提供的是**方案方向**，尚未接入实时商家、菜单、交通班次、价格或库存数据。模型结果也需要核实，不表示群成员已达成共识。

## 使用

仓库随附 `desktop/app/ChatAssistant.exe` 及所需应用文件，可直接双击根目录的 **启动群聊推荐.cmd**。这是 framework-dependent Windows 发布版，运行机器仍需安装 **.NET 9 Desktop Runtime**。如需重新生成，先关闭正在运行的桌面程序，再按下方“构建与启动”操作。

1. 点击 **观察群聊窗口**，在 Windows 选择器中选择目标聊天窗口。
2. 检查推荐的识别范围：绿色框只包含消息，排除左侧会话列表和下方输入框；有偏差时点击 **调整绿色消息框**。点击 **确认并开始识别**。
3. 保持 **侧栏自动推荐** 开启。出现聚餐或出行需求后，讨论稍作停顿，侧栏会自动整理条件与方案；点击 **为什么推荐 · 群聊依据** 可查看来源文字。
4. 需要主动重新分析时点击 **立即分析当前讨论**；不需要当前推荐时点击 **这次不推荐**。点击 **复制这个方案** 或 **复制推荐汇总** 后，可自行粘贴到群里。

识字有误时，在“群聊文字”表格中双击修改，再点 **采用校对** 或下方分析按钮；编辑期间会暂停采集。发言人未标注也能汇总群聊需求。群名识别不出来时，程序可只分析当前可见画面，不累计其他会话的历史。

也可以点击 **粘贴群聊** 导入文字，或展开 **演示与处理状态**，点击 **体验模拟群聊**，测试补充预算、切换出行群和取消安排。演示使用内置合成内容，不读取微信。

## 千问与数据范围

- 默认自动推荐在本地运行。Windows 本地 OCR 识别截图，程序不自动发送消息、付款或预订。
- **千问细化方案** 会发送表格里的当前群聊文字。**自动使用千问** 默认关闭，只有主动开启后才自动调用；相同需求去重，并限制自动请求频率。可随时关闭。
- 每次最多发送最近 20 条、单条 2000 字、合计 8000 字。发送内容只有文字与归属类别，不含截图、群标题、窗口外的内容或内部消息 ID。
- 服务沿用已有网页项目配置，固定地址 `https://maas.qianwenaiapi.com/compatible-mode/v1`、模型 `qwen3.8-flash`。云端失败时保留本地结果，并显示原因。密钥无需写进本项目代码。
- 点击 **暂停观察** 会停止采集并清空当前上下文。切换群聊、修改文字或需求变化时会使旧请求失效；已经送达服务商的请求不保证停止计算或计费。

## 构建与启动

目标为 Windows 10 19041 及以上，使用 .NET 9 WPF。编译需要 .NET 9 SDK，运行需要对应 Desktop Runtime；识字需要安装 Windows 中文 OCR 语言组件。安装 SDK 后，可双击本目录的 **publish.cmd**，或在仓库根目录执行：

```powershell
dotnet publish desktop/ChatAssistant/ChatAssistant.csproj -c Release -o desktop/app
& ./desktop/app/ChatAssistant.exe
```

本地推荐不依赖 Python、LoRA、基础模型或云端密钥。千问桥接需要已安装网页依赖的 Python 环境和自己的密钥；无需启动网页服务器，也无需下载基础模型。首次配置可参照根目录 [README](../README.md)。程序会自动寻找仓库内 `代码/.venv/Scripts/python.exe`，也可以用启动参数指定：

```powershell
& ./desktop/app/ChatAssistant.exe `
  --python '网页项目\.venv\Scripts\python.exe' `
  --web-root '网页项目完整路径' `
  --bridge-path '本仓库完整路径\desktop\bridge\assistant_bridge.py'
```

也可在仓库 `.local/desktop-settings.json` 中配置 `python`、`web_root`、`bridge` 三个路径。该目录不应提交到 Git。详细桥接契约见 [bridge/README.md](bridge/README.md)。

## 回归验证

下列测试使用合成聊天和模拟云端响应，不调用真实千问：

```powershell
dotnet run --project desktop/ChatAssistant.Core.Tests -c Release
dotnet run --project desktop/ChatAssistant.PlatformCheck -c Release
dotnet run --project desktop/ChatAssistant.InputCheck -c Release
python -X utf8 -m unittest discover -s desktop/bridge -p 'test_*.py'
dotnet run --project desktop/bridge/service_check -c Release -- 'Python可执行文件完整路径'
```

这些检查覆盖群聊条件提取、推荐时机、取消与去重、OCR 合成画面、IPC 格式和旧请求隔离；不能替代不同微信/QQ 版本、缩放比例和实际群聊布局下的人工验收。区域选择错误、窗口隐藏、字体过小或主题变化仍可能影响识别，可调整范围或使用粘贴入口。
