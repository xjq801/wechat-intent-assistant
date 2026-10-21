using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ChatAssistant.Core;

public sealed record RecommendationFact(string Label, string Value);
public sealed record RecommendationOption(string Title, string Reason, string Tradeoff);
public sealed record RecommendationEvidence(string MessageId, string Text);
public sealed record GroupRecommendation(
    string ConversationKey, long Version, string Intent, string Title, string Summary,
    IReadOnlyList<RecommendationFact> Facts, IReadOnlyList<string> Missing,
    IReadOnlyList<RecommendationOption> Options, IReadOnlyList<RecommendationEvidence> Evidence,
    string Fingerprint, bool IsCancelled, string Source);

/// <summary>
/// Aggregates only the supplied stable viewport/history. These are planning directions, not
/// verified venues, prices or timetables. Group participants are never inferred from message count.
/// </summary>
public static class GroupRecommendationEngine
{
    private static readonly Regex Dining = R("吃饭|聚餐|饭局|约饭|吃(?:火锅|烤肉|烧烤|自助|午饭|晚饭|早饭|早餐|中饭|粤菜|川菜|日料|西餐|什么|啥)|餐厅|饭店");
    private static readonly Regex Travel = R("出行|旅游|旅行|出差|订票|机票|高铁|火车|航班|飞机|自驾|坐车|去.{1,10}(?:玩|旅游)");
    private static readonly Regex Plan = R("一起|我们|咱们|大家|要不要|准备|打算|计划|想去|想吃|去吃|约|订|定|找|选|安排|哪家|怎么走|吃什么|吃啥");
    private static readonly Regex Greeting = R("吃(?:过)?(?:早|午|晚|中)?饭(?:了没(?:有)?|了吗|了没有|没(?:有)?|了么)|(?:早|午|晚)饭吃了吗|吃(?:过|了)(?:没(?:有)?|吗|么)");
    private static readonly Regex Recollection = R("吃(?:完|过|了)|吃(?:早|午|晚|中)?饭了|去过|坐过|刚(?:刚)?.{0,8}(?:回来|下车|到家)|(?:昨天|前天|上周|上次|之前|去年).{0,16}(?:吃|聚餐|旅游|旅行|出差|火车|高铁|航班|从|去|到)");
    private static readonly Regex Unrelated = R("报告|文件|作业|论文|课题|考试|成绩|游戏|手机|电脑|代码|快递|工资|PPT|ppt|开会|会议|上课");
    private static readonly Regex Correction = R("改成|改为|换成|定为|改到|说错了|更正|改一下|调整为|还是|就定|确定为|最终");
    private static readonly Regex Time = R("(?:[0-9]{4}年)?[0-9]{1,2}月[0-9]{1,2}[日号]?|[0-9]{1,2}[/-][0-9]{1,2}|今天|明天|后天|今晚|明晚|(?:这|本|下)?周[一二三四五六日末天]|星期[一二三四五六日天]|(?:早上|上午|中午|下午|晚上|傍晚)?[0-9一二两三四五六七八九十]{1,3}(?:点(?:半|一刻|三刻)?|[:：][0-9]{2})|早上|上午|中午|下午|晚上|傍晚");
    private static readonly Regex People = R("(?<!人均)[0-9一二两三四五六七八九十百]{1,3}(?:个)?[人位]|我们俩|咱俩");
    private static readonly Regex Budget = R("(?:(?:人均|每人|每位)(?:的)?预算|预算(?:人均|每人|每位)|总预算|预算|人均|每人|每位|总共|合计)(?:控制在|不超过|最多|大概|大约|约|是|在|为)?(?:人均|每人|每位)?[0-9零一二两三四五六七八九十百千万]+(?:[-到至][0-9零一二两三四五六七八九十百千万]+)?(?:元|块)?(?:以内|以下|左右)?");
    private static readonly Regex Diet = R("(?:不(?:能|要)?|别|少|忌|不能吃|不吃)?(?:吃辣|辣|海鲜|花生|香菜|牛肉|羊肉|猪肉)|素食|清真|清淡|过敏|火锅|烤肉|烧烤|粤菜|川菜|日料|西餐|自助");
    private static readonly Regex Transport = R("(?:不(?:想|要|能)?(?:坐|开)?|别坐|优先(?:坐)?|首选(?:坐)?|想坐|坐|选择|选)?(?:高铁|火车|飞机|航班|自驾|大巴|公交|地铁|打车|拼车)|晕车|少换乘|直达");
    private static readonly Regex Route = R(@"从(?<from>[\p{IsCJKUnifiedIdeographs}A-Za-z]{1,12}?)(?:去|到|出发去)(?<to>[\p{IsCJKUnifiedIdeographs}A-Za-z]{1,12})");
    private static readonly Regex Place = R("(?:地点|位置)(?:在|是|选在|定在|改为|改到)?(?<place>[^，,。.!！？?；;]{2,18})|(?:在|去|到|选|定|约)(?<place>.{1,16}?(?:店|餐厅|饭店|路|街|商场|附近|广场|区|公园|城|中心|大学|车站|站|馆))|(?<place>学校|公司|食堂|望京|三里屯|国贸|五道口)(?:附近)?");
    private static Regex R(string pattern) => new(pattern, RegexOptions.Compiled, TimeSpan.FromMilliseconds(80));
    private static string Normalize(string value) => Regex.Replace(value.Normalize(NormalizationForm.FormKC), @"\s+", "");

    public static GroupRecommendation? Analyze(ConversationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if ((!snapshot.IsStable && !snapshot.CanSuggest) || snapshot.Messages.Count == 0 || string.IsNullOrWhiteSpace(snapshot.Key)) return null;
        var topic = new Topic();
        int unrelatedMessages = 0;
        foreach (var message in snapshot.Messages.TakeLast(ConversationTracker.MaximumMessages))
        {
            if (string.IsNullOrWhiteSpace(message.Text)) continue;
            var bounded = message.Text.Length > 2000 ? message.Text[..2000] : message.Text;
            string normalized = Normalize(bounded);
            bool relevant = false;
            var leading = new List<string>();
            foreach (var clause in Regex.Split(normalized, "[，,。.!！？?；;\n]+"))
            {
                if (clause.Length == 0) continue;
                if (IsCancellation(clause))
                {
                    if (topic.Intent.Length > 0)
                    {
                        topic.Cancelled = true; topic.Facts.Clear(); topic.Evidence.Clear();
                        topic.Evidence.Add(new(message.Id, bounded)); relevant = true;
                    }
                    leading.Clear();
                    continue;
                }
                bool recalled = Greeting.IsMatch(clause) || Recollection.IsMatch(clause);
                bool dining = !recalled && Dining.IsMatch(clause)
                    && (Plan.IsMatch(clause) || Time.IsMatch(clause) || Regex.IsMatch(clause, "^(?:吃饭|聚餐|饭局|约饭|吃火锅)[吧吗呀啊]*$"));
                bool travel = !recalled && !dining && IsTravel(clause);
                bool anchor = dining || travel;
                if (anchor)
                {
                    string intent = dining ? "吃饭" : "出行";
                    bool separate = topic.Cancelled || topic.Intent != intent || Regex.IsMatch(clause, "另外|另约|下次|新计划|重新安排");
                    if (separate) topic = new Topic { Intent = intent };
                    else topic.Intent = intent;
                    topic.Cancelled = false;
                    if (separate) foreach (var prefix in leading) Extract(topic, prefix);
                    Extract(topic, clause); relevant = true;
                    leading.Clear();
                    continue;
                }
                // A side conversation must not provide the dinner's time, place or budget.
                if (recalled || Unrelated.IsMatch(clause)) continue;
                if (topic.Intent.Length > 0 && !topic.Cancelled && Extract(topic, clause)) relevant = true;
                leading.Add(clause);
            }
            if (relevant)
            {
                unrelatedMessages = 0;
                if (!topic.Evidence.Any(e => e.MessageId == message.Id)) topic.Evidence.Add(new(message.Id, bounded));
            }
            else if (!Regex.IsMatch(normalized, "^(?:好(?:的|呀|啊)?|嗯+|行|可以|没问题|收到|哈哈+|[Oo][Kk]|知道了|是的|对)[呢啊呀吧。！!]*$"))
                ++unrelatedMessages;
        }
        if (topic.Intent.Length == 0 || (!topic.Cancelled && unrelatedMessages >= 4)) return null;
        var evidence = topic.Evidence.TakeLast(8).ToArray();
        if (topic.Cancelled)
            return new(snapshot.Key, snapshot.Version, topic.Intent, "安排已取消", "群聊中出现取消安排的表述，已撤下之前的方案。",
                [], [], [], evidence, Fingerprint(topic), true, "本地群聊需求分析");
        var facts = topic.Facts.Select(pair => new RecommendationFact(pair.Key, string.Join(" / ", pair.Value))).ToArray();
        var required = topic.Intent == "吃饭"
            ? new[] { "时间", "地点", "人数", "预算", "饮食偏好" }
            : new[] { "时间", "出发地", "目的地", "人数", "预算", "交通偏好" };
        var missing = required.Where(key => !topic.Facts.ContainsKey(key)).ToArray();
        string summary = facts.Length == 0 ? "已出现共同安排的意向，可以先比较以下方向。" :
            "讨论中已提到：" + string.Join("；", facts.Select(f => $"{f.Label}：{f.Value}")) + "。";
        bool alternatives = topic.Facts.Any(pair => pair.Key is "时间" or "人数" or "预算" && pair.Value.Select(value => CanonicalFact(pair.Key, value)).Distinct().Count() > 1);
        if (alternatives) summary += "部分条件有多个说法，先作为候选条件，尚不视为全员确认。";
        return new(snapshot.Key, snapshot.Version, topic.Intent, topic.Intent == "吃饭" ? "聚餐方案可以这样选" : "出行方案可以这样比较",
            summary, facts, missing, Options(topic), evidence, Fingerprint(topic), false,
            "本地群聊需求分析 · 方案方向，未查询实时商家或班次");
    }

    private static bool IsTravel(string clause)
    {
        bool route = Route.IsMatch(clause) && !Regex.IsMatch(clause, "第.{0,5}[页章段行]|[一二两三四五六七八九十0-9]+点");
        return route || Travel.IsMatch(clause) && (Plan.IsMatch(clause) || Time.IsMatch(clause)
            || Regex.IsMatch(clause, "出行|旅游|旅行|出差|订票|^坐(?:高铁|火车|飞机)|怎么走"));
    }
    private static bool IsCancellation(string clause)
    {
        string value = Regex.Replace(clause, "(?:不要|不用|不必|别|不)取消", "按原计划");
        // A participant declining or cancelling one ticket does not cancel the whole group.
        if (!Regex.IsMatch(value, "^我(?!们)") && Regex.IsMatch(value, "不(?:再)?(?:聚餐|吃饭|约饭|出行|旅游|旅行)了")) return true;
        return Regex.IsMatch(value, "^(?:那|还是|我们|大家|今晚|明天|周末)*(?:就|先)?(?:算了|不去了|不聚了|不吃了|先不安排|先别安排|取消)[吧了啊]?$|(?:聚餐|饭局|聚会|行程|出行|旅行)(?:就|先)?取消|取消(?:这次|本次|今晚|明天|周末)?(?:聚餐|饭局|聚会|行程|出行|旅行)");
    }
    private static bool Extract(Topic topic, string clause)
    {
        bool relevant = false;
        bool replace = Correction.IsMatch(clause);
        var time = Time.Matches(clause).Select(m => m.Value).ToArray();
        if (Regex.IsMatch(clause, "改期|时间再定|时间还没定|还没定时间|换个时间|另约|推迟|改天") && time.Length == 0)
        { topic.Facts.Remove("时间"); relevant = true; }
        if (time.Length > 0)
        {
            if (Regex.IsMatch(clause, "没空|没时间|不方便|不行|不能|有事|赶不上")) Add(topic, "时间限制", clause, false);
            else Add(topic, "时间", string.Concat(time), replace);
            relevant = true;
        }
        var people = People.Matches(clause).Select(m => m.Value).Distinct().ToArray();
        // "我带两个人" describes one member's party, not the whole group's attendance.
        if (people.Length > 0 && !Regex.IsMatch(clause, "^(?:我(?!们)|本人|我和|我带|我这边)"))
        {
            string count = Regex.IsMatch(clause, "至少|最多|不止|大概|预计|左右") ? clause : string.Join(" / ", people);
            Add(topic, "人数", count, replace || Regex.IsMatch(clause, "一共|总共|总计|最后|我们现在")); relevant = true;
        }
        if (Regex.IsMatch(clause, "人数(?:还)?没定|几个人还不知道")) { topic.Facts.Remove("人数"); relevant = true; }
        var budgets = Budget.Matches(clause).Select(m => m.Value).ToArray();
        if (budgets.Length > 0) { Add(topic, "预算", string.Join(" / ", budgets), replace); relevant = true; }
        if (Regex.IsMatch(clause, "预算(?:还)?没定")) { topic.Facts.Remove("预算"); relevant = true; }
        if (topic.Intent == "吃饭")
        {
            var places = Place.Matches(clause).Select(m => CleanPlace(m.Groups["place"].Value)).Where(p => p.Length > 0).Distinct().ToArray();
            if (places.Length > 0) { Add(topic, "地点", string.Join(" / ", places), replace); relevant = true; }
            if (Diet.IsMatch(clause))
            {
                // Keep the original constraint wording: negations and allergies matter more than a cuisine label.
                bool restriction = Regex.IsMatch(clause, "不|别|少|忌|过敏|素食|清真|清淡");
                if (replace && topic.Facts.TryGetValue("饮食偏好", out var previous))
                    previous.RemoveAll(value => !Regex.IsMatch(value, "不|别|少|忌|过敏|素食|清真|清淡"));
                Add(topic, "饮食偏好", restriction ? clause : string.Join("、", Diet.Matches(clause).Select(m => m.Value).Distinct()), false);
                relevant = true;
            }
        }
        else if (topic.Intent == "出行")
        {
            var route = Route.Match(clause);
            if (route.Success)
            {
                string origin = CleanDestination(route.Groups["from"].Value), destination = CleanDestination(route.Groups["to"].Value);
                if (origin.Length > 0) Add(topic, "出发地", origin, replace);
                if (destination.Length > 0) Add(topic, "目的地", destination, replace);
                relevant = true;
            }
            else
            {
                var origin = Regex.Match(clause, "(?:从|出发地(?:是|在)?)(?<place>[\\p{IsCJKUnifiedIdeographs}A-Za-z]{1,12}?)(?:出发|走|$)");
                if (origin.Success) { Add(topic, "出发地", CleanDestination(origin.Groups["place"].Value), replace); relevant = true; }
                var destination = Regex.Match(clause, "(?:去|到|前往|目的地(?:是|在)?)(?<place>[\\p{IsCJKUnifiedIdeographs}A-Za-z]{1,12})");
                if (destination.Success)
                {
                    var place = CleanDestination(destination.Groups["place"].Value);
                    if (place.Length > 0) { Add(topic, "目的地", place, replace); relevant = true; }
                }
            }
            if (Transport.IsMatch(clause))
            {
                if (replace && topic.Facts.TryGetValue("交通偏好", out var previous))
                    previous.RemoveAll(value => !Regex.IsMatch(value, "不|别|晕车|少换乘"));
                Add(topic, "交通偏好", string.Join("、", Transport.Matches(clause).Select(m => m.Value).Distinct()), false); relevant = true;
            }
        }
        return relevant;
    }
    private static string CleanPlace(string value) => Regex.Replace(value, "^(?:就|还是|改到|定在|选在|在|去|到)", "");
    private static string CleanDestination(string value)
    {
        value = Regex.Split(value, "出差|旅游|旅行|游玩|玩|我们|大家|一起|坐|乘|自驾|怎么样|好不好|出发|吧|吗|呢|的")[0];
        return value is "吃饭" or "那里" or "哪儿" or "哪里" or "什么地方" or "开会" ? "" : value;
    }
    private static void Add(Topic topic, string label, string value, bool replace)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        if (replace || !topic.Facts.TryGetValue(label, out _)) topic.Facts[label] = [];
        var list = topic.Facts[label];
        if (!list.Contains(value)) list.Add(value);
        if (list.Count > 4) list.RemoveAt(0);
    }
    private static string Fingerprint(Topic topic)
    {
        string value = topic.Intent + "|" + topic.Cancelled + "|" + string.Join("|", topic.Facts.OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => p.Key + ":" + string.Join("/", p.Value.Select(value => CanonicalFact(p.Key, value)).Distinct().Order(StringComparer.Ordinal))));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..20];
    }
    private static string CanonicalFact(string label, string value)
    {
        value = Normalize(value);
        if (label is not ("时间" or "人数" or "预算")) return value;
        value = value.Replace("我们俩", "2人").Replace("咱俩", "2人").Replace("星期", "周");
        value = Regex.Replace(value, "[零〇一二两三四五六七八九十百千万]+", match => ChineseNumber(match.Value).ToString());
        if (label == "人数") return value.Replace("个", "").Replace("位", "人");
        if (label == "预算") return value.Replace("预算", "").Replace("元", "").Replace("块", "").Replace("每人", "人均").Replace("每位", "人均");
        value = Regex.Replace(value, @"(\d{1,2})点半", "$1:30");
        value = Regex.Replace(value, @"(\d{1,2})点", "$1:00");
        return value;
    }
    private static int ChineseNumber(string value)
    {
        const string digits = "零一二三四五六七八九";
        int total = 0, section = 0, current = 0;
        foreach (char character in value)
        {
            int digit = character is '两' ? 2 : character is '〇' ? 0 : digits.IndexOf(character);
            if (digit >= 0) { current = digit; continue; }
            int unit = character switch { '十' => 10, '百' => 100, '千' => 1000, '万' => 10000, _ => 1 };
            if (unit == 10000) { total += (section + current) * unit; section = current = 0; }
            else { section += (current == 0 ? 1 : current) * unit; current = 0; }
        }
        return total + section + current;
    }
    private static IReadOnlyList<RecommendationOption> Options(Topic topic)
    {
        string Known(string key, string fallback) => topic.Facts.TryGetValue(key, out var values) ? string.Join(" / ", values) : fallback;
        string budget = Known("预算", "预算尚未明确");
        string people = Known("人数", "最终人数待确认");
        if (topic.Intent == "吃饭")
        {
            string preferences = Known("饮食偏好", "口味与忌口尚未明确");
            bool restricted = Regex.IsMatch(preferences, "不.*辣|别.*辣|少辣|清淡|过敏|素食|清真|不吃");
            string area = Known("地点", "大家都方便到达的区域");
            var bookingMissing = new List<string>();
            if (!topic.Facts.ContainsKey("人数")) bookingMissing.Add("人数");
            if (!topic.Facts.ContainsKey("时间")) bookingMissing.Add("时间");
            string bookingCheck = bookingMissing.Count > 0
                ? "先补齐" + string.Join("和", bookingMissing)
                : "核实预约时间和最终到场人数是否有变化";
            var result = new List<RecommendationOption>
            {
                new(restricted ? "先筛选能满足忌口的聚餐店" : "先比较家常菜或可共享的菜式",
                    $"适合把 {people} 的选择放在一起比较；优先筛选 {area}。饮食条件：{preferences}。",
                    $"先核实菜单、忌口处理和多人座位，再按 {budget} 比较；未确认大家口味前不定菜。"),
                new("用同一区域的两家候选做小范围投票",
                    $"地点范围按「{area}」筛选，给群里比较人均支出、到达方式和座位条件。",
                    $"选择更容易比较，{bookingCheck}；需要自行查询真实门店、营业时间及可订座情况。")
            };
            if (Regex.IsMatch(preferences, "火锅|烤肉|烧烤"))
                result.Add(new("把群里提到的菜系保留为备选",
                    $"讨论里提到「{preferences}」，可与前两种方向比较。",
                    restricted ? "先确认是否能分锅、分餐或避免交叉接触；存在过敏时必须向商家核实，不默认适合全员。" : "需核实通风、等待时间、菜单和总支出；菜系偏好尚不代表全员同意。"));
            return result;
        }
        string route = Known("出发地", "出发地待定") + " → " + Known("目的地", "目的地待定");
        string transport = Known("交通偏好", "交通偏好待确认");
        bool preferGround = Regex.IsMatch(transport, "(?:优先|首选)(?:坐)?(?:高铁|火车)|少换乘");
        var options = new List<RecommendationOption>();
        if (!Regex.IsMatch(transport, "(?:不(?:想|要|能)?(?:坐)?|别坐)高铁"))
            options.Add(new("先比较铁路或其他公共交通直达方案", $"路线按「{route}」核对，优先比较是否直达与全程耗时。",
                $"还需查询实际车次、站点接驳和票价，再对照 {budget}；不能仅比较车上时间。"));
        if (preferGround)
            options.Add(new("比较不同出发时段与接驳组合", $"结合群里「{transport}」的偏好，对比铁路或地面公共交通的可用时段和接驳次数。",
                "先查询实际可用方案，再比较门到门耗时与总支出；更早出发或不同站点未必更省时间，不能只看车程。"));
        else if (!Regex.IsMatch(transport, "(?:不(?:想|要|能)?(?:坐)?|别坐)(?:飞机|航班)"))
            options.Add(new("距离较远时再比较航空方案", "把机场往返、候机和行李条件一起计入出行比较。",
                "仅在路线有可用航班且总耗时或价格合适时采用；未查询实时航班、余票和价格。"));
        if (Regex.IsMatch(transport, "自驾|拼车") && !Regex.IsMatch(transport, "不(?:想|要|能)?(?:开)?自驾"))
            options.Add(new("核实车辆与司机后比较自驾", $"群聊提到自驾或拼车；人数依据是「{people}」，不能用发言条数代替人数。",
                "先确认车辆、座位和驾驶安排，再核算路费、停车与路程；不默认群内有人愿意开车。"));
        if (options.Count < 2)
            options.Add(new("先把出发时段和交通限制列成比较表", $"当前交通表述是「{transport}」，先按大家明确接受的方式筛选。",
                "信息不足时先保留可选方向，补全起终点和日期后再查实际可用方案。"));
        return options.Take(3).ToArray();
    }
    private sealed class Topic
    {
        public string Intent { get; set; } = "";
        public bool Cancelled { get; set; }
        public Dictionary<string, List<string>> Facts { get; } = new();
        public List<RecommendationEvidence> Evidence { get; } = [];
    }
}
