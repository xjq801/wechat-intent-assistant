# 参与项目开发

欢迎改进群聊里的吃饭、出行推荐、Windows OCR、界面和测试。

## 修改并提交代码

1. 在本仓库页面点击 **Fork**，将项目复制到自己的 GitHub 账号。
2. 克隆自己的 Fork，创建一个分支，修改代码。
3. 按 `desktop/README.md` 构建并运行与改动相关的测试。
4. 把分支推送到自己的 Fork，然后向本仓库的 `main` 提交 **Pull Request（合并请求）**。
5. 在合并请求中说明问题、修改内容和验证结果。维护者检查后合并。

公开仓库允许所有人通过 Fork 和 Pull Request 参与；直接推送到本仓库需要由仓库所有者授予协作者权限。

## 本地验证

在仓库根目录执行，桌面项目需要 Windows 和 .NET 9 SDK：

```powershell
dotnet build desktop/ChatAssistant/ChatAssistant.csproj -c Release
dotnet run --project desktop/ChatAssistant.Core.Tests -c Release
dotnet run --project desktop/ChatAssistant.InputCheck -c Release
dotnet run --project desktop/ChatAssistant.PlatformCheck -c Release
python -X utf8 -m unittest discover -s desktop/bridge -p 'test_*.py'
```

调整桌面功能后，可用 `desktop/publish.cmd` 重新生成发布程序。实际千问调用可能产生费用，常规测试使用模拟响应。

请用合成聊天复现问题，不要在 Issue、截图、测试样例或代码中提交真实聊天、API 密钥、`.local` 配置、账户数据和上传文件。基础模型不放入 Git，按 `模型/README.md` 单独准备；使用模型或第三方依赖时保留其许可信息。

发现问题可提交 Issue，描述复现步骤、预期结果、实际结果，以及 Windows 版本、显示缩放比例等必要环境信息。
