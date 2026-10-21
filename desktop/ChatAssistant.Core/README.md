# 会话跟踪核心

纯 .NET 9，无额外依赖，不联网、不保存聊天或截图。输入角色固定为 `self`、`other`、`unknown`。`Observe` 的 `key` 应由联系人标题图像的会话解析器提供，不能一直使用应用窗口名称“微信”作为联系人身份，也不应直接使用容易变化的 OCR 文字作为身份。

- 连续两次相同的消息观察才建立稳定状态；初次画面仅建立基线，可以手动求助，不触发自动提醒。
- 每次新画面第一帧即递增 `Version` 并令 `CanSuggest=false`。界面应在版本改变时取消或废弃原建议。
- 联系人变化或无法读取联系人标题时立即清除旧会话。新联系人建立自己的基线。
- 连续尾部消息可追加；重复帧、已见子序列和向前展开的旧消息不重复追加。跳变或难以确定的短消息重叠要求用户确认。
- `ConfirmEdited` 显式采用用户校正的内容，保留原 OCR 锚点，避免下一张相同截图覆盖校正。仍有 `unknown` 角色时不会允许旧版个人回复建议；群聊推荐按 `IsStable` 判断。
- 快照只保留最近 20 条。`CanSuggest` 且 `NewMessages.Count > 0` 可用于新消息提醒；稳定首屏或当前画面模式也可刷新本地草稿，但不能称其为新消息。云端求助仍由界面的明确操作决定。
- 纯文本不能分辨没有唯一上下文锚点的重复“好的”是历史还是新消息，此时不自动触发。更可靠的时间戳/气泡位置可在后续版本补充。

`ArrangementHints` 是独立的本地规则提示，不是 LoRA 推理结果。它只提示餐饮/出行安排可能缺少的项目及多个时间说法，不表示已经核实营业、班次或价格。地点规则覆盖有限，用户可核对并忽略。

安排分析按最新连贯话题进行：用餐问候、已完成的用餐回顾和新的普通问题不会沿用旧饭局；取消会结束旧安排，后面的明确邀请可以重新开始。无新时间的改期会重新提示确认时间。`RelevantMessages` 提供这一分析用的对话片段，保持原始聊天文字不变。它仍是有限规则，不应当作通用语义理解或完整任务管理。

从仓库根目录运行行为验证：

```powershell
dotnet run --project desktop/ChatAssistant.Core.Tests/ChatAssistant.Core.Tests.csproj --configuration Release
```

用例全部为合成内容，覆盖首屏基线、重复画面、上下滚动、会话切换、OCR 抖动、人工校正、版本失效、未知角色、长度上限及本地提醒。
# Current-frame fallback

`ConversationTracker.ObserveCurrentFrame(messages)` is the fallback when a title image is
missing or blank. It waits for two consecutive equal observations, immediately invalidates
pending replies when the visible content changes, and publishes only the current viewport.
It never appends historical messages or reports new-message events. Reliable self/other roles
are required only for the legacy personal-reply path; group recommendations use text stability.
Corrections survive an unchanged raw OCR frame, but do not carry over
to a changed viewport. The original `Observe(key, messages)` API remains available for
recognized, image-anchored conversations.

`SessionIdentityResolver` lives in the desktop recognition layer. It compares a fixed title
image anchor using background-relative foreground masks, independently of OCR labels.
Small pixel brightness changes are ignored; local tile checks detect changed characters even
in a long title. Blank images use the isolated current-frame route. Window/source/ROI changes
must call `Reset`; pausing to edit the same image should retain the anchor.

# 群聊推荐入口

`GroupRecommendationEngine.Analyze(snapshot)` 读取最多 20 条当前稳定消息，聚合吃饭或出行需求。`IsStable` 仅表示文字稳定；`CanSuggest` 继续保留旧回复功能的说话人门禁。群聊推荐允许 `unknown` 角色，绝不通过发言条数推算人数。

输出包括已提到的条件、缺项、2–3 个可比较方向、取舍及原文证据。方案均为本地规划方向，不包含未经查询的真实店名、价格、班次或余票。少量插话不打断需求，取消撤回、改期和显式条件更正会更新结果；回顾、问候及普通聊天不会独立触发安排。

`RecommendationCoordinator.Observe(snapshot, now)` 在稳定需求后等待 2 秒静默，连续讨论最多等待 8 秒；UI 每秒调用 `Tick(now)`。`Refresh(snapshot, now)` 是用户手动立即分析，`IgnoreCurrent()` 忽略当前需求指纹，`Reset()` 在暂停、换源或会话清空时清理状态。相同条件只更新上下文版本，不重复提醒。`Current` 可在等待新画面时保留展示；复制、云端生成或应用云端结果前，必须检查 `!IsPending`、`StableSnapshot` 及需求指纹/版本仍有效。

静止窗口只提供一次物理帧时，调用方可在确认 OCR 候选、来源 generation 与处理队列均未变化的静默间隔后调用 `ConfirmStableObservation(expectedVersion)`。它确认的是一段时间没有变化，不宣称收到第二帧；旧版本计时器不能确认新候选。
