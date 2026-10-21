using System.Text.RegularExpressions;

namespace ChatAssistant.Core;

/// <summary>Local rule hints, not model predictions or verified bookings. No network or persistence.</summary>
public static class ArrangementHints
{
    private static readonly Regex Dining = new("吃饭|聚餐|约饭|吃火锅|吃烤肉|吃烧烤|吃午饭|吃晚饭|吃早饭|吃早餐|吃中饭|餐厅|饭店", RegexOptions.Compiled);
    private static readonly Regex Travel = new("出行|旅游|旅行|出差|订票|机票|高铁|火车|航班|自驾|坐车|去.{1,10}(?:玩|旅游)", RegexOptions.Compiled);
    private static readonly Regex Time = new("今天|明天|后天|今晚|明晚|周[一二三四五六日末天]|星期[一二三四五六日天]|[0-9一二两三四五六七八九十]{1,3}(?:点|[:：][0-9]{2})|上午|中午|下午|晚上|傍晚", RegexOptions.Compiled);
    private static readonly Regex Place = new("(?:在|去|到|选|定|约).{1,18}(?:店|餐厅|饭店|路|街|商场|附近|广场|区|园|城|中心|大学|站|馆)|学校|公司|食堂|望京|三里屯|国贸|五道口", RegexOptions.Compiled);
    private static readonly Regex People = new("[0-9一二两三四五六七八九十]{1,3}(?:个)?人|我们俩|咱俩|两位|三位|四位", RegexOptions.Compiled);
    private static readonly Regex Hour = new("(?<hour>[0-9一二两三四五六七八九十]{1,3})(?:点(?:半|一刻|三刻)?|[:：][0-9]{2})", RegexOptions.Compiled);
    private static readonly Regex Cancel = new("^(?:那|还是)?算了|取消|不去了|不聚了|不吃了|先不安排|先别安排|改天再说", RegexOptions.Compiled);
    private static readonly Regex Correction = new("改成|改为|换成|定为|改到|说错了|更正|还是", RegexOptions.Compiled);
    private static readonly Regex Plan = new("一起|我们|咱们|要不要|准备|打算|计划|想去|想吃|去吃|约|订|定|找|选|安排|哪家|吃什么|吃啥", RegexOptions.Compiled);
    private static readonly Regex MealGreeting = new("吃(?:过)?(?:早|午|晚|中)?饭(?:了没(?:有)?|了吗|了没有|没(?:有)?|了么)|吃(?:过|了)(?:没(?:有)?|吗|么)", RegexOptions.Compiled);
    private static readonly Regex MealPast = new("吃(?:完|过|了)|吃(?:早|午|晚|中)?饭了|刚.{0,8}(?:火锅|烤肉|烧烤)|(?:昨天|前天|上周|上次|之前|以前|去年).{0,14}(?:吃|聚餐|餐厅|饭店)", RegexOptions.Compiled);
    private static readonly Regex UnrelatedTopic = new("报告|文件|作业|论文|课题|考试|成绩|电影|游戏|照片|手机|电脑|代码|天气|感冒|快递|工资|PPT|ppt", RegexOptions.Compiled);
    private static readonly Regex FollowUp = new("时间|几点|哪天|什么时候|地点|哪里|哪儿|哪个区|几个人|多少人|人数|谁一起|预算|人均|多少钱|贵不贵|价格|交通|路线|出发|目的地|高铁|飞机|火车|机票|航班|自驾|忌口|过敏|吃辣|素食|口味|菜系|改期|换个时间|改天|推迟|提前|按原计划", RegexOptions.Compiled);
    private static readonly Regex Acknowledgement = new("^(?:好(?:的|呀|啊)?|嗯+|行|可以|没问题|收到|[Oo][Kk]|知道了|是的|对|就这样|再看看|先看看时间|到时候再说|不要取消|不用取消|别取消|不取消)[呢啊呀吧]*$", RegexOptions.Compiled);

    public static IReadOnlyList<ArrangementHint> Analyze(IReadOnlyList<ChatMessage> messages)
    {
        if (messages.Count == 0 || messages.Any(m => m.Speaker == "unknown")) return Array.Empty<ArrangementHint>();
        var recent = RelevantMessages(messages);
        if (recent.Count == 0) return Array.Empty<ArrangementHint>();
        string text = string.Join("\n", recent.Select(m => m.Text));
        var hints = new List<ArrangementHint>();
        var conflicts = FindTimeConflicts(recent);
        bool hasTime = HasCurrentTime(recent);
        if (recent.Any(m => DiningAnchor(m.Text)))
        {
            var missing = new List<string>();
            if (!hasTime) missing.Add("时间");
            if (!Place.IsMatch(text)) missing.Add("地点");
            if (!People.IsMatch(text)) missing.Add("人数");
            hints.Add(Build("吃饭", missing, conflicts));
        }
        if (recent.Any(m => TravelAnchor(m.Text)))
        {
            var missing = new List<string>();
            if (!hasTime) missing.Add("出发时间");
            if (!Regex.IsMatch(text, "从.{1,16}(?:到|出发|走|去)")) missing.Add("出发地");
            if (!Regex.IsMatch(text, "(?:去|到|前往).{1,12}(?:[，。！？\\s]|$)")) missing.Add("目的地");
            hints.Add(Build("出行", missing, conflicts));
        }
        return hints;
    }

    /// <summary>
    /// Use only the current arrangement thread. Clauses are analyzed in order, so a cancellation
    /// ends an old plan while a later explicit invitation can start a new one in the same message.
    /// Ordinary questions, meal greetings and recollections do not inherit old arrangement slots.
    /// Returned text is an analysis copy; the editable transcript is never rewritten.
    /// </summary>
    public static IReadOnlyList<ChatMessage> RelevantMessages(IReadOnlyList<ChatMessage> messages)
    {
        var relevant = new List<ChatMessage>();
        string activeIntent = "";
        foreach (var message in messages.TakeLast(10))
        {
            var normalized = Regex.Replace(message.Text.Normalize(), @"\s+", "");
            var leadingSlots = new List<ChatMessage>();
            bool messageHasAnchor = false;
            foreach (var clause in Regex.Split(normalized, "[，,。.!！？?；;]+"))
            {
                if (clause.Length == 0) continue;
                if (IsCancellation(clause))
                { relevant.Clear(); leadingSlots.Clear(); activeIntent = ""; continue; }

                bool dining = DiningAnchor(clause), travel = !dining && TravelAnchor(clause);
                if (dining || travel)
                {
                    string intent = dining ? "吃饭" : "出行";
                    if (activeIntent != intent)
                    {
                        relevant.Clear();
                        relevant.AddRange(leadingSlots);
                    }
                    leadingSlots.Clear(); messageHasAnchor = true;
                    activeIntent = intent;
                    relevant.Add(message with { Text = clause });
                    continue;
                }

                bool unrelated = UnrelatedTopic.IsMatch(clause) || MealGreeting.IsMatch(clause) || MealPast.IsMatch(clause);
                bool followUp = !unrelated && (Time.IsMatch(clause) || Place.IsMatch(clause)
                    || People.IsMatch(clause) || FollowUp.IsMatch(clause) || Acknowledgement.IsMatch(clause)
                    || Regex.IsMatch(clause, "(?:不要|不用|不必|别|不)取消"));
                if (!messageHasAnchor && followUp) leadingSlots.Add(message with { Text = clause });
                if (activeIntent.Length > 0 && followUp)
                    relevant.Add(message with { Text = clause });
                else
                {
                    relevant.Clear(); activeIntent = "";
                    if (!followUp) leadingSlots.Clear();
                }
            }
        }
        return relevant.ToArray();
    }

    private static bool DiningAnchor(string text)
    {
        if (!Dining.IsMatch(text) || MealGreeting.IsMatch(text) || MealPast.IsMatch(text)) return false;
        return Plan.IsMatch(text) || Time.IsMatch(text) || Regex.IsMatch(text, "^(?:吃饭|吃火锅|聚餐|约饭)[吧吗呀啊]*$");
    }

    private static bool TravelAnchor(string text)
    {
        bool route = Regex.IsMatch(text, "从.{1,10}(?:去|到).{1,10}");
        if (!Travel.IsMatch(text) && !route) return false;
        if (Regex.IsMatch(text, "去过|坐过|刚(?:刚)?.{0,8}(?:回来|下车|到家)|(?:昨天|前天|上周|上次|去年).{0,16}(?:旅游|旅行|出差|火车|高铁|航班)")) return false;
        return route || Plan.IsMatch(text) || Time.IsMatch(text)
            || Regex.IsMatch(text, "订票|出行|旅游|旅行|出差|自驾|^坐(?:火车|高铁|飞机|车)");
    }

    private static bool IsCancellation(string text)
    {
        string affirmative = Regex.Replace(text, "(?:不要|不用|不必|别|不)取消", "按原计划");
        return Cancel.IsMatch(affirmative);
    }

    private static bool IsUnscheduled(string text) =>
        Regex.IsMatch(text, "改期|换个时间|改天|时间再定|时间还没定|待定|还没定时间|另约") && !Time.IsMatch(text);

    private static bool HasCurrentTime(IEnumerable<ChatMessage> messages)
    {
        bool found = false;
        foreach (var message in messages)
        {
            if (IsUnscheduled(message.Text)) found = false;
            if (Time.IsMatch(message.Text)) found = true;
        }
        return found;
    }

    private static ArrangementHint Build(string intent, IReadOnlyList<string> missing, IReadOnlyList<string> conflicts)
    {
        string title = conflicts.Count > 0 ? $"{intent}时间需要确认" :
            missing.Count > 0 ? $"{intent}安排还可以补充信息" : $"可以确认一下{intent}安排";
        string reason = conflicts.Count > 0 ? "对话里出现了不止一个时间说法，可能需要再确认。" :
            missing.Count > 0 ? $"当前画面尚未明确：{string.Join("、", missing)}。" :
            "当前画面已提到主要安排信息，可以再向对方确认；具体营业、班次和价格仍需另行核实。";
        return new(intent, title, reason, missing.ToArray(), conflicts.ToArray());
    }

    private static IReadOnlyList<string> FindTimeConflicts(IEnumerable<ChatMessage> messages)
    {
        var values = new Dictionary<string, string>();
        foreach (var message in messages)
        {
            if (IsUnscheduled(message.Text)) { values.Clear(); continue; }
            var matches = Hour.Matches(message.Text);
            if (matches.Count == 0) continue;
            if (Correction.IsMatch(message.Text)) values.Clear();
            foreach (Match match in matches)
            {
                string minutes = match.Value.Contains('半') ? "30" :
                    match.Value.Contains("一刻") ? "15" : match.Value.Contains("三刻") ? "45" :
                    match.Value.Contains(':') || match.Value.Contains('：') ? match.Value[^2..] : "00";
                string key = NormalizeHour(match.Groups["hour"].Value) + ":" + minutes;
                values[key] = match.Value;
            }
        }
        return values.Count > 1 ? new[] { $"时间有多个说法：{string.Join(" / ", values.Values)}" } : Array.Empty<string>();
    }

    private static string NormalizeHour(string value)
    {
        if (int.TryParse(value, out int parsed)) return parsed.ToString();
        var numbers = new Dictionary<char, int> { ['一'] = 1, ['二'] = 2, ['两'] = 2, ['三'] = 3, ['四'] = 4, ['五'] = 5, ['六'] = 6, ['七'] = 7, ['八'] = 8, ['九'] = 9 };
        int index = value.IndexOf('十');
        if (index >= 0)
        {
            int tens = index == 0 ? 1 : numbers.GetValueOrDefault(value[0]);
            int units = index + 1 < value.Length ? numbers.GetValueOrDefault(value[index + 1]) : 0;
            return (tens * 10 + units).ToString();
        }
        return value.Length == 1 && numbers.TryGetValue(value[0], out int number) ? number.ToString() : value;
    }
}
