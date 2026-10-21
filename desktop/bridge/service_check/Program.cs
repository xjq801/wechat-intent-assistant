using System.Diagnostics;
using System.Text;
using ChatAssistant.Core;
using ChatAssistant.Services;

if (args.Length < 1) throw new ArgumentException("Pass the Python executable path.");
var python = args[0];
var temp = Directory.CreateTempSubdirectory("chat-assistant-service-").FullName;
int checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    Console.WriteLine("PASS: " + name); checks++;
}
ConversationSnapshot Snapshot(string text = "normal", long version = 7, bool canSuggest = true) =>
    new("synthetic-only", version, new[] { new ChatMessage("1", "other", text) }, canSuggest, "合成测试");
try
{
    File.WriteAllText(Path.Combine(temp, "assistant_features.py"), "# synthetic test fixture");
    var script = Path.Combine(temp, "fake_worker.py");
    File.WriteAllText(script, """
import json,sys,time
print(json.dumps({'ready': True, 'protocol': 1}), flush=True)
for raw in sys.stdin:
    req=json.loads(raw); text=req['messages'][0]['text']
    if text == 'slow': time.sleep(10)
    if text == 'malformed': print('not-json',flush=True); continue
    result={'version':req['version'] + (1 if text == 'wrong-version' else 0),'intent':'无意图','summary':'fixture','missing':[],'replies':[],'source':'synthetic-worker','durationMs':0,'warning':''}
    if req['op'] == 'recommend':
        result={'version':req['version'] + (1 if text == 'wrong-version' else 0),
                'summary':'group:' + str(req.get('cloudConsent')) + ':' + req['messages'][0]['speaker'],
                'options':[{'title':'方向一','description':'合成方案一','tradeOff':'需确认条件一'},
                           {'title':'方向二','description':'合成方案二','tradeOff':'需确认条件二'}],
                'missing':['确认人数'],'source':'synthetic-group-worker','durationMs':0,'warning':''}
        if text == 'no-arrangement': result['options']=[]
        if text == 'missing-tradeoff': del result['options'][0]['tradeOff']
        if text == 'action-field': result['command']='book-ticket'
        if text == 'nested-action': result['options'][0]['url']='https://example.org'
        if text == 'duplicate-options': result['options'][1]=dict(result['options'][0])
        if text == 'null-option': result['options'][1]=None
        if text == 'long-title': result['options'][0]['title']='x'*61
        if text == 'many-options': result['options']*=2
    print(json.dumps({'requestId': 'other-request' if text == 'wrong-request' else req['requestId'], 'result':result}), flush=True)
""");
    var options = new AssistantOptions(python, temp, script);
    using var service = new AssistantService(options);
    var normal = await service.AnalyzeAsync(Snapshot(), false, CancellationToken.None);
    Check(normal.Source == "synthetic-worker" && normal.Version == 7, "persistent JSONL round-trip");
    var invalid = await service.AnalyzeAsync(Snapshot("wrong-version"), false, CancellationToken.None);
    Check(invalid.Source == "未生成" && invalid.Replies.Count == 0, "stale worker version rejected");
    var restarted = await service.AnalyzeAsync(Snapshot(version: 8), false, CancellationToken.None);
    Check(restarted.Source == "synthetic-worker" && restarted.Version == 8, "restart after corrupt protocol");
    var malformed = await service.AnalyzeAsync(Snapshot("malformed"), false, CancellationToken.None);
    Check(malformed.Source == "未生成", "malformed JSON rejected");
    using (var cancel = new CancellationTokenSource(250))
    {
        var watch = Stopwatch.StartNew(); bool cancelled = false;
        try { await service.AnalyzeAsync(Snapshot("slow"), false, cancel.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled && watch.ElapsedMilliseconds < 3000, "in-flight cancellation terminates old worker promptly");
    }
    var fresh = await service.AnalyzeAsync(Snapshot(version: 9), false, CancellationToken.None);
    Check(fresh.Version == 9 && fresh.Source == "synthetic-worker", "cancelled response cannot appear in next request");
    using (var firstCancel = new CancellationTokenSource(500))
    using (var queuedCancel = new CancellationTokenSource(80))
    {
        var first = service.AnalyzeAsync(Snapshot("slow"), false, firstCancel.Token);
        bool queuedCancelled = false;
        try { await service.AnalyzeAsync(Snapshot(), false, queuedCancel.Token); }
        catch (OperationCanceledException) { queuedCancelled = true; }
        try { await first; } catch (OperationCanceledException) { }
        Check(queuedCancelled, "queued request cancellation does not enter worker");
    }
    var blocked = await service.ReplyAsync(Snapshot(canSuggest: false), CancellationToken.None);
    Check(blocked.Source == "未生成", "unconfirmed context blocked before worker");
    var unknown = new ConversationSnapshot("fixture", 1, new[] { new ChatMessage("1", "unknown", "text") }, true, "");
    Check((await service.ReplyAsync(unknown, CancellationToken.None)).Source == "未生成", "unknown speaker blocked before cloud operation");
    var unstableGroup = await service.RecommendAsync(unknown with { CanSuggest = false }, CancellationToken.None);
    Check(unstableGroup.Source == "未生成" && unstableGroup.Options.Count == 0, "unstable group frame blocked before cloud operation");
    var stableGroup = unknown with { CanSuggest = false, IsStable = true };
    var group = await service.RecommendAsync(stableGroup, CancellationToken.None);
    Check(group.Options.Count == 2 && group.Source == "synthetic-group-worker" && group.Summary == "group:True:unknown",
        "stable unknown group messages reach recommendation operation with cloud consent, without reply attribution");
    Check(group.Options.All(x => x.TradeOff.Length > 0) && group.Missing.SequenceEqual(new[] { "确认人数" }),
        "group IPC preserves comparable options and conditions to verify");
    foreach (var bad in new[] { "wrong-version", "wrong-request", "missing-tradeoff", "action-field", "nested-action", "duplicate-options", "null-option", "long-title", "many-options" })
    {
        var rejected = await service.RecommendAsync(Snapshot(bad), CancellationToken.None);
        Check(rejected.Source == "未生成" && rejected.Options.Count == 0, "invalid group response discarded: " + bad);
    }
    var noArrangement = await service.RecommendAsync(Snapshot("no-arrangement"), CancellationToken.None);
    Check(noArrangement.Source == "synthetic-group-worker" && noArrangement.Options.Count == 0,
        "no active arrangement can return a valid empty recommendation");
    using (var groupCancel = new CancellationTokenSource(200))
    {
        bool cancelled = false;
        var watch = Stopwatch.StartNew();
        try { await service.RecommendAsync(Snapshot("slow"), groupCancel.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled && watch.ElapsedMilliseconds < 3000, "group request cancellation promptly stops its worker");
    }
    var freshGroup = await service.RecommendAsync(Snapshot(version: 18), CancellationToken.None);
    Check(freshGroup.Version == 18 && freshGroup.Source == "synthetic-group-worker", "cancelled group output cannot appear in a later conversation");
    var emptyGroup = await service.RecommendAsync(Snapshot() with { Messages = Array.Empty<ChatMessage>() }, CancellationToken.None);
    Check(emptyGroup.Options.Count == 0 && emptyGroup.Source == "未生成", "empty stable group does not start a cloud request");
    using (var unavailable = new AssistantService(options with { PythonPath = Path.Combine(temp, "missing.exe") }))
        Check((await unavailable.AnalyzeAsync(Snapshot(), false, CancellationToken.None)).Source == "未生成", "missing Python produces visible degradation");
    var many = Enumerable.Range(0, 30).Select(i => new ChatMessage(i.ToString(), "other", i.ToString())).ToList();
    var prepared = AssistantService.PrepareMessages(many);
    Check(prepared.Count == 20 && prepared[0].Text == "10", "upload includes only most recent twenty messages");
    var emojis = AssistantService.PrepareMessages(new[] { new ChatMessage("1", "other", string.Concat(Enumerable.Repeat("😀", 2200))) });
    Check(emojis[0].Text.EnumerateRunes().Count() == 2000 && !emojis[0].Text.Contains('�'), "upload truncation preserves Unicode codepoints");
    var oversized = Enumerable.Range(0, 10).Select(i => new ChatMessage(i.ToString(), "other", new string('字', 2500))).ToList();
    Check(AssistantService.PrepareMessages(oversized).Sum(x => x.Text.EnumerateRunes().Count()) == 8000, "total context capped at eight thousand codepoints");

    ConversationSnapshot Conversation(params (string Role, string Text)[] content) =>
        new("synthetic-only", 5, content.Select((x, i) => new ChatMessage(i.ToString(), x.Role, x.Text)).ToArray(), true, "合成测试");
    var draft = LocalReplyService.Generate(Conversation(("other", "一起吃饭吗？")));
    Check(draft.Replies.Count is >= 1 and <= 2 && draft.Replies.Any(x => x.Contains("几点") && x.Contains("区域")),
        "local meal drafts ask for missing time and location without Python or cloud");
    Check(draft.Source.Contains("未调用云端") && draft.Replies.All(x => !x.Contains("我同意") && !x.Contains("已经预订")),
        "local source is explicit and drafts do not invent agreement or bookings");
    var spacedMeal = LocalReplyService.Generate(Conversation(("other", "周 六 晚 上 一 起 吃 饭 吗？")));
    Check(spacedMeal.Replies.Count > 0 && !spacedMeal.Replies.Any(x => x.Contains("哪天")),
        "OCR spacing still finds supplied meal time");
    var travel = LocalReplyService.Generate(Conversation(("other", "明天去上海出差")));
    Check(travel.Replies.Any(x => x.Contains("从哪里出发")) && travel.Replies.All(x => !x.Contains("帮你订票")),
        "travel draft asks for departure instead of promising tickets");
    var cancelDraft = LocalReplyService.Generate(Conversation(("other", "明天吃饭吧"), ("other", "算了，不去了")));
    Check(cancelDraft.Replies.Any(x => x.Contains("取消")) && !cancelDraft.Replies.Any(x => x.Contains("哪家店")),
        "cancellation takes precedence over earlier dining context");
    var stillGoing = LocalReplyService.Generate(Conversation(("other", "明天吃饭吧"), ("other", "不要取消，还是按原计划")));
    Check(!stillGoing.Summary.Contains("取消或暂缓") && stillGoing.Replies.Count > 0,
        "negated cancellation preserves arrangement suggestions");
    var reschedule = LocalReplyService.Generate(Conversation(("other", "明天吃饭吧"), ("other", "改到周六八点吧")));
    Check(reschedule.Replies.Any(x => x.Contains("确认一下")) && !reschedule.Replies.Any(x => x.Contains("哪天")),
        "explicit new time is acknowledged without committing user's availability");
    var diet = LocalReplyService.Generate(Conversation(("other", "一起吃饭吗"), ("other", "我不能吃辣")));
    Check(diet.Replies.Count > 0 && diet.Replies.All(x => !x.Contains("我不吃辣") && !x.Contains("我不能吃辣")),
        "diet constraint belongs to other and is not transferred to self");
    var greeting = LocalReplyService.Generate(Conversation(("other", "一起吃饭吗"), ("other", "在吗？")));
    Check(greeting.Replies.Contains("在呢，怎么啦？") && !greeting.Summary.Contains("吃饭"),
        "latest greeting supersedes an older arrangement topic");
    var ordinaryQuestion = LocalReplyService.Generate(Conversation(("other", "你觉得这件事怎么办？")));
    Check(ordinaryQuestion.Replies.Count > 0 && ordinaryQuestion.Summary.Contains("普通聊天问题"),
        "ordinary questions produce useful clarification drafts locally");
    var emptyDraft = LocalReplyService.Generate(Conversation());
    Check(emptyDraft.Replies.Count == 0 && emptyDraft.Summary.Contains("聊天文字"), "empty capture gives a concrete recovery hint");
    var unknownDraft = LocalReplyService.Generate(Conversation(("unknown", "明天吃饭吗")));
    Check(unknownDraft.Replies.Count == 0 && unknownDraft.Summary.Contains("发言方"), "unknown speaker never gets an invented reply");
    var unconfirmedDraft = LocalReplyService.Generate(Snapshot(canSuggest: false));
    Check(unconfirmedDraft.Replies.Count == 0, "unconfirmed or switched conversation blocks local draft generation");
    var selfOnly = LocalReplyService.Generate(Conversation(("self", "明天吃饭吗")));
    Check(selfOnly.Replies.Count == 0, "self-only transcript does not impersonate the other person");
    var alreadyAsked = LocalReplyService.Generate(Conversation(("other", "明天吃饭吗"), ("self", "几点见？")));
    Check(alreadyAsked.Replies.Count == 0 && alreadyAsked.Summary.Contains("已经向对方提问"),
        "already sent clarification is not drafted again");
    var mealStatus = LocalReplyService.Generate(Conversation(("other", "你吃饭了吗？")));
    Check(mealStatus.Replies.Count > 0 && mealStatus.Summary.Contains("用餐情况") && !mealStatus.Replies.Any(x => x.Contains("哪家店")),
        "meal status greeting is not mistaken for an invitation");
    var newTopic = LocalReplyService.Generate(Conversation(("other", "明天一起吃饭吗？"), ("self", "先看看时间"), ("other", "报告什么时候交？")));
    Check(newTopic.Summary.Contains("普通聊天问题") && !newTopic.Replies.Any(x => x.Contains("餐厅") || x.Contains("哪家店")),
        "unrelated latest topic does not inherit old dining drafts");
    var statedBudget = LocalReplyService.Generate(Conversation(("self", "今晚一起吃饭吗？"), ("other", "好呀，我们三个人，预算人均一百元。")));
    Check(statedBudget.Replies.Any(x => x.Contains("哪家店")) && !statedBudget.Replies.Any(x => x.Contains("预算是多少")),
        "a supplied budget is not mistaken for a budget question");
    if (args.Length >= 3)
    {
        using var live = new AssistantService(new AssistantOptions(python, args[1], args[2]));
        var offline = await live.AnalyzeAsync(Snapshot("明天晚上两个人在北京朝阳吃火锅，人均100元。"), false, CancellationToken.None);
        Check(offline.Intent == "吃饭" && offline.Source == "本地规则" && offline.Warning == "", "real Python bridge local-only field extraction");
    }
    Console.WriteLine($"SERVICE_CHECKS_PASSED={checks}");
}
finally
{
    // Only remove the exact temporary fixture directory created above.
    if (Path.GetDirectoryName(temp) == Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar)
        && Path.GetFileName(temp).StartsWith("chat-assistant-service-", StringComparison.Ordinal))
        Directory.Delete(temp, recursive: true);
}
