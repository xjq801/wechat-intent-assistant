using ChatAssistant;
using ChatAssistant.Core;

int checks = 0;
void Check(bool value, string description)
{
    if (!value) throw new Exception(description);
    Console.WriteLine("PASS " + description); checks++;
}

var mixed = ChatTextImport.Parse("我：周六聚餐吧\n小王：人均预算100元\n小李：我不能吃辣\n群友：我们四个人", false);
Check(mixed.Count == 4, "Named group participants are separate messages after self");
Check(mixed[0].Speaker == "self" && mixed[1].Speaker == "unknown" && mixed[2].Speaker == "unknown" && mixed[3].Speaker == "other", "Unrecognised nicknames are never attributed to self");
Check(mixed[1].Text == "小王：人均预算100元" && mixed[2].Text.Contains("小李"), "Nickname evidence is retained");
Check(ChatTextImport.HasSpeakerLabels("群友：明天去天津玩") && ChatTextImport.HasSpeakerLabels("self: hello"), "Both group and legacy known prefixes are recognised");
var all = ChatTextImport.Parse("小王：周六一起吃饭\n小李：地点还没定", true);
Check(all.All(m => m.Speaker == "other") && all[0].Text.StartsWith("小王"), "Explicit raw-group mode preserves all text");
var wrapped = ChatTextImport.Parse("我：第一行\n继续说明\n对方：好的", false);
Check(wrapped.Count == 2 && wrapped[0].Text.Contains("继续说明"), "Unlabelled continuation is kept with the current message");
Check(ChatTextImport.Parse(" \n\t\n", true).Count == 0, "Empty imports do not create messages");
var many = ChatTextImport.Parse(string.Join('\n', Enumerable.Range(1, 24).Select(i => "消息" + i)), true);
Check(many.Count == 20 && many[0].Text == "消息5" && many[^1].Text == "消息24", "Only the latest twenty messages are retained");
var snapshot = new ConversationSnapshot("group", 1, mixed.Select((m, i) => new ChatMessage("m"+i, m.Speaker, m.Text)).ToArray(), false, "") { IsStable = true };
var recommendation = GroupRecommendationEngine.Analyze(snapshot);
Check(recommendation is not null && recommendation.Intent == "吃饭" && recommendation.Options.Count >= 2, "Anonymous imported group text can produce recommendations");
Check(recommendation!.Evidence.Any(e => e.Text.Contains("小李")), "Imported member constraints retain their evidence");
Console.WriteLine($"{checks} checks passed");
