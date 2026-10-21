# 聊伴 · 群聊推荐系统

当前版本是 Windows 桌面侧栏：观察微信或 QQ 群聊窗口中的可见文字，围绕**吃饭和出行**汇总需求，适时给出可比较的方案。它会合并多条消息里的时间、人数、地点、预算与偏好；新增条件会更新推荐，明确取消安排时撤下旧方案。

侧栏默认自动生成本地方案。点击“千问细化方案”，或主动勾选“自动使用千问”后，才会把当前识别文字发送给已配置的千问服务。程序不会自动发群消息、下单或预订；尚未接入实时餐厅、班次、价格和库存查询。

本仓库同时保留原网页系统、训练数据、最终 LoRA 权重及测试。网页基线可从 `web-v1` 标签查看。基础模型、密钥和个人聊天数据不随仓库上传。

欢迎参与修改：Fork 本仓库，在自己的分支开发后提交 Pull Request。具体步骤和验证命令见 [参与开发说明](CONTRIBUTING.md)。

## 项目目录

- [`desktop/`](desktop/README.md)：当前群聊推荐桌面程序、Windows 本地 OCR、推荐逻辑、Python 桥接和测试。
- [`启动群聊推荐.cmd`](启动群聊推荐.cmd)：启动 `desktop/app/ChatAssistant.exe`。
- [`代码/`](代码/)：原网页服务、前端、AI 处理、训练和评估脚本。
- [`数据/`](数据/)：原项目训练集 `train_data.json` 与测试集 `test_data.json`。
- [`代码/model_output/lora_qwen_0.6b_intent/lora_adapter/`](代码/model_output/lora_qwen_0.6b_intent/lora_adapter/)：最终 LoRA 权重及分词器文件，无需重新训练。
- [`模型/README.md`](模型/README.md)：基础模型下载与路径说明。
- [`文档/`](文档/)：版本说明和验收记录。

克隆或解压整个仓库，保留这些相对目录关系。无需放回原来的 D 盘路径，不要直接在压缩包内运行。

## 桌面版：启动和使用

需要 Windows 10 19041 或更新版本、.NET 9 Desktop Runtime，以及 Windows 中文 OCR 语言组件。**本地群聊推荐不依赖 Python、LoRA、基础模型或云端密钥。**

仓库随附 `desktop/app/ChatAssistant.exe` 及所需应用文件，是依赖系统运行时的 Windows 发布版（framework-dependent），**仍需安装 .NET 9 Desktop Runtime**。下载或克隆后，直接双击根目录的 **启动群聊推荐.cmd**。

如需从源码重建，先关闭正在运行的桌面程序，安装 .NET 9 SDK，再双击 [`desktop/publish.cmd`](desktop/publish.cmd)；也可以在仓库根目录运行：

```powershell
dotnet publish desktop/ChatAssistant/ChatAssistant.csproj -c Release -o desktop/app
& .\启动群聊推荐.cmd
```

1. 打开微信或 QQ 目标群聊，点击侧栏的 **观察群聊窗口**，在 Windows 选择器中选择对应窗口。
2. 检查识别范围：绿色框只包含聊天消息，避开会话列表和输入框，然后确认开始识别。
3. 保持 **侧栏自动推荐** 开启。讨论出现聚餐或出行需求、稍作停顿后，查看侧栏的条件汇总、方案和群聊依据。
4. 需要分享时点击 **复制这个方案**，再自行粘贴进群。识字有误可双击表格修改；也可使用 **粘贴群聊** 入口。

想先体验时，展开 **演示与处理状态 → 体验模拟群聊**。演示使用内置文字，不读取微信。完整操作和测试命令见 [`desktop/README.md`](desktop/README.md)。

## 桌面版：启用千问

千问模式沿用 `代码/` 中的 Python 服务与密钥配置，模型为 `qwen3.8-flash`。请按下方网页环境安装步骤，在 `代码/` 内创建 `.venv` 并安装依赖，再运行 `configure_qwen_key.py` 配置自己的密钥。**只使用桌面本地推荐或千问方案生成时，不需要下载 Qwen 基础模型，也不需要启动网页服务器。**

关闭并重新打开桌面程序后，它会自动寻找仓库内的 `代码/.venv/Scripts/python.exe` 和 `desktop/bridge/assistant_bridge.py`。使用其他环境时，按 [`desktop/README.md`](desktop/README.md) 指定路径。勾选 **自动使用千问** 后，系统会自动细化推荐；相同需求去重，自动请求有频率限制。每次最多发送最近 20 条文字，不上传截图或窗口标题。

下面保留原网页版本的安装和运行说明。只有需要运行网页本地 LoRA 意图识别或继续训练时，才需要准备基础模型。

## 网页版：基础模型

从上游 [Qwen/Qwen3-0.6B](https://huggingface.co/Qwen/Qwen3-0.6B) 下载至 `模型/Qwen3-0.6B/`。也可以在启动前设置 `CHAPTER2_BASE_MODEL` 环境变量，指向已有模型目录。保留上游模型许可证。

## 网页版：首次安装（Windows PowerShell）

原机验证环境为 Python 3.13.1。新机器需安装 Python 3.13，并允许 `py` 命令使用它。首次安装依赖需要联网。进入解压后的“代码”目录：

```powershell
py -3.13 -m venv .venv
& .\.venv\Scripts\python.exe -m pip install --upgrade pip
```

若使用支持 CUDA 的 NVIDIA 显卡，可按原机的 PyTorch 版本安装；是否能启用 GPU 仍取决于驱动和硬件：

```powershell
& .\.venv\Scripts\python.exe -m pip install torch==2.9.1 --index-url https://download.pytorch.org/whl/cu126
```

没有适用 NVIDIA 显卡时，改用 CPU 版本（上述两条 PyTorch 安装命令二选一，CPU 推理较慢）：

```powershell
& .\.venv\Scripts\python.exe -m pip install torch==2.9.1 --index-url https://download.pytorch.org/whl/cpu
```

然后安装其余已核对版本的依赖：

```powershell
& .\.venv\Scripts\python.exe -m pip install -r requirements-runtime.txt
```

这些安装命令来自已运行环境的版本清单，本次未在新机器重装验证。`文档/已验证环境.json` 记录完整版本。`train.py`、`test.py` 是原项目旧实验入口，可能另需原版实验依赖；新版运行与本地训练入口分别是 `run_local_app.py` 和 `train_local_lora.py`。

## 网页版：配置千问并启动

压缩包不含 API 密钥、登录账号、聊天记录、上传文件或原机加密配置。需要在新机器重新配置自己的密钥。包内默认服务为用户指定的千问平台兼容端点，模型为 `qwen3.8-flash`。

```powershell
& .\.venv\Scripts\python.exe configure_qwen_key.py
```

按提示输入密钥，不会回显；在 Windows 上以当前用户的 DPAPI 加密保存。不要把密钥写进代码、命令参数或分享包。

```powershell
& .\start_local_app.ps1
```

该脚本会加载本地模型并启动网页。若 PowerShell 不允许运行脚本，可直接启动 Python：

```powershell
& .\.venv\Scripts\python.exe -u run_local_app.py
```

浏览器打开 http://127.0.0.1:5000/login ，注册新账号并进入聊天。默认仅本机访问。首次启动会建立新的用户数据与服务密钥。

不配置云端密钥也可以启动：本地意图识别、规则整理和备用规划仍可用；主动问答需要云端密钥。前端部分依赖通过 CDN 加载，因此此包不保证完全断网可用。

## 网页版：六项改进

结构化需求与缺项提示、多轮修改和任务生命周期、可编辑确认卡片、主动询问 AI 与自动识别开关、2–3 个可比较方案、可折叠的来源及耗时记录。

使用流程：发需求 → 补全或修改右侧卡片 → 保存 → 确认生成方案。普通咨询点击“询问 AI”或使用 `@AI`。实际商家、价格、营业状态及票务库存尚未接入实时查询。

## 网页版：历史验收与内容范围

网页基线版 151 项 Python 测试及 28 项前端测试通过，并完成本地 LoRA、真实云端问答和三个云端方案的页面验收。细节见 `文档/验收/六项功能升级说明.md`。该说明记录原机验收过程，其中的原机路径不影响本包主程序按相对目录运行。

本包省略约 4.8 GB 的原机虚拟环境、冗余训练检查点、缓存、服务日志和私人数据。代码、最终 LoRA 权重与数据均已包含，不需重新训练；基础模型需另行准备。
