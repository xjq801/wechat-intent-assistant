# 智能意图聊天系统 · 六项功能升级版

这是 2026-10-20 已验证的改进版本。GitHub 中包含源代码、前端、测试、数据集、训练后的 LoRA 权重及升级验收说明。基础模型需另行下载；密钥和私人数据未上传。

## 目录

- `代码/`：聊天服务、AI 处理、测试、训练和评估脚本。
- `模型/Qwen3-0.6B/`：基础模型，保留上游 README 和 LICENSE。
- `代码/model_output/lora_qwen_0.6b_intent/lora_adapter/`：已训练权重。
- `数据/`：原项目训练与测试数据。
- `文档/验收/`：六项升级说明、真实模型/API验收记录及页面截图。
- `desktop/`：后续独立 Windows 聊天助手。网页基线保存在 `web-v1` 标签。

请克隆仓库，保留“代码 / 模型 / 数据”的相对目录关系，不必放回原 D 盘路径。不要直接在压缩包内运行。

## 基础模型

从上游 [Qwen/Qwen3-0.6B](https://huggingface.co/Qwen/Qwen3-0.6B) 下载至 `模型/Qwen3-0.6B/`。也可以在启动前设置 `CHAPTER2_BASE_MODEL` 环境变量，指向已有模型目录。保留上游模型许可证。

## 首次安装（Windows PowerShell）

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

## 配置千问并启动

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

## 六项改进

结构化需求与缺项提示、多轮修改和任务生命周期、可编辑确认卡片、主动询问 AI 与自动识别开关、2–3 个可比较方案、可折叠的来源及耗时记录。

使用流程：发需求 → 补全或修改右侧卡片 → 保存 → 确认生成方案。普通咨询点击“询问 AI”或使用 `@AI`。实际商家、价格、营业状态及票务库存尚未接入实时查询。

## 验收与内容范围

本版 151 项 Python 测试及 28 项前端测试通过，并完成本地 LoRA、真实云端问答和三个云端方案的页面验收。细节见 `文档/验收/六项功能升级说明.md`。该说明记录原机验收过程，其中的原机路径不影响本包主程序按相对目录运行。

本包省略约 4.8 GB 的原机虚拟环境、冗余训练检查点、缓存、服务日志和私人数据。代码、最终 LoRA 权重与数据均已包含，不需重新训练；基础模型需另行准备。
