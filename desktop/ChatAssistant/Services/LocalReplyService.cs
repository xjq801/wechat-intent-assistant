using System.Text.RegularExpressions;
using ChatAssistant.Core;

namespace ChatAssistant.Services;

public sealed record LocalReplyResult(string Summary, IReadOnlyList<string> Replies, string Source);

/// <summary>Small, predictable drafts. No network, model loading, persistence or send action.</summary>
public static class LocalReplyService
{
    private const string Source = "本地回复草稿（未调用云端）";
    private static string Clean(string text) => Regex.Replace(text.Normalize(), @"\s+", "");
    private static bool Has(string text, string pattern) => Regex.IsMatch(text, pattern, RegexOptions.CultureInvariant);
    private static LocalReplyResult Result(string summary, params string[] replies) =>
        new(summary, replies.Distinct().Take(2).ToArray(), Source);

    public static LocalReplyResult Generate(ConversationSnapshot snapshot)
    {
        if (!snapshot.CanSuggest)
            return Result("识别内容还未确认。请先查看识别文字，确认当前会话和发言方后再生成草稿。");
        var messages = snapshot.Messages.TakeLast(20).Where(m => !string.IsNullOrWhiteSpace(m.Text)).ToArray();
        if (messages.Length == 0)
            return Result("还没有识别到聊天文字。可以选择聊天窗口，或粘贴一段对话后生成草稿。");
        if (messages.Any(m => m.Speaker is not ("self" or "other")))
            return Result("有消息还分不清是我还是对方。请先修正发言方，避免替错人回复。");
        var lastOther = Array.FindLastIndex(messages, m => m.Speaker == "other");
        if (lastOther < 0)
            return Result("目前只有你发出的消息。等对方回复，或补充一条对方消息后再生成草稿。");

        var latest = Clean(messages[lastOther].Text);
        var last = Clean(messages[^1].Text);
        if (messages[^1].Speaker == "self" && IsCancellation(last))
            return Result("你最近的消息已经提出取消安排，可以先等对方回复。");
        if (messages[^1].Speaker == "self" && lastOther < messages.Length - 1 && Has(last, "[？?]|几点|哪天|哪里|哪儿|几个人"))
            return Result("你已经向对方提问，可以先等对方补充，避免重复追问。");

        if (IsCancellation(latest))
            return Result("对方提到取消或暂缓，先确认是否要改期。",
                "你是想先取消这次安排，还是换个时间？",
                "这次先不安排的话，要等你方便时再商量吗？");
        if (Has(latest, "改期|改天|换个时间|改到|推迟|提前|改成.{0,8}(?:点|号|周)|改为.{0,8}(?:点|号|周)"))
            return Has(latest, "[0-9一二两三四五六七八九十]{1,3}点|明天|后天|周[一二三四五六日天末]|星期")
                ? Result("对方提出调整时间，草稿先询问其他安排，不替你承诺新时间。",
                    "这个时间我还需要确认一下，地点有没有变化？",
                    "除了时间，其他安排也需要一起调整吗？")
                : Result("对方想改期，但新时间还没有说清楚。",
                    "你想改到哪天、几点？我们再一起确认一下。",
                    "你接下来哪个时间段比较方便？");

        // Answer the latest conversational need before using older arrangement context.
        if (Has(latest, "不能吃辣|不吃辣|不太能吃辣|忌口|过敏"))
            return Result("对方说的是自己的饮食限制，草稿保留这个归属。",
                "你还有其他忌口吗？选店时可以一起考虑。",
                "你有没有比较放心的餐厅或菜系？我们可以先从这些里面选。");
        if (Has(latest, "^(?:你|您|你们|大家)?(?:今天|现在|还)?(?:吃(?:过|了)(?:午饭|晚饭|早饭|饭)?(?:了|吗|没|没有|么|呢)?|吃(?:饭|午饭|晚饭|早饭)(?:了吗|了没|没有|没))[，。？！?!~～]*$"))
            return Result("对方在问你的用餐情况；本地草稿不会替你编造是否吃过。",
                "你呢，吃过了吗？", "你是想约个时间一起吃吗？");
        if (Has(latest, "^(你好|您好|嗨|哈喽|hello|hi|在吗|在不在|早上好|早安|晚上好)[呀啊呢吗，。！？!?~～]*$"))
            return Result("先自然回应对方的问候。", "在呢，怎么啦？", "你好呀，找我有什么事吗？");
        if (Has(latest, "谢谢|多谢|感谢你"))
            return Result("对方在表达感谢，可以简短回应。", "不客气～", "不用客气呀。");
        if (Has(latest, "最近怎么样|最近还好吗|过得怎么样"))
            return Result("可以先回应关心，再把话题接下去。", "谢谢惦记，你最近怎么样？");
        if (Has(latest, "好累|很累|难过|不开心|烦死|心情不好"))
            return Result("先接住对方表达的感受，不急着替对方下结论。",
                "发生什么了，愿意和我说说吗？", "你想聊聊，还是先休息一会儿？");
        if (Has(latest, "帮我.{0,10}(?:看|检查)|看看(?:这个|这份)|帮忙看看"))
            return Result("先问清对方希望你看什么，避免直接承诺完成。",
                "你希望我重点看哪一部分？", "需要重点关注什么，有没有时间要求？");

        var hints = ArrangementHints.Analyze(messages);
        bool hasArrangementTopic = Has(latest, "吃饭|聚餐|约饭|火锅|烤肉|烧烤|餐厅|饭店|出行|旅游|旅行|出差|高铁|火车|机票|自驾");
        bool isFollowup = Has(latest, "几点|哪天|什么时候|哪里|哪儿|哪家|几个人|多少人|预算|人均|价格|出发|到达|明天|后天|周[一二三四五六日天末]|星期|[0-9一二两三四五六七八九十]{1,3}点|地点|时间|人数|^可以[呀啊呢，。！？!?]*$|^都行[呀啊呢，。！？!?]*$")
            && !Has(latest, "文件|材料|论文|报告|邮件|代码|作业|题目|考试|工作进度");
        var hint = !hasArrangementTopic && !isFollowup ? null : hints.FirstOrDefault(h =>
            h.Intent == "吃饭" ? Has(latest, "吃|饭|餐|火锅|烤肉") : Has(latest, "出行|旅游|旅行|出差|车|票|航班"))
            ?? hints.FirstOrDefault();

        if (hint is not null)
        {
            if (hint.Conflicts.Count > 0)
                return Result("出现了多个时间说法，可以先确认最终时间。",
                    "前面提到了几个时间，最后你更方便哪一个？",
                    "我们先把日期和具体时间再确认一下，好吗？");
            if (Has(latest, "多少钱|贵不贵|(?:预算|人均|价格).{0,8}(?:多少|怎么样|怎么定)|预算呢"))
                return Result("对方在问预算，先协商范围，不编造你的预算。",
                    "你能接受的大概预算是多少？我们先按这个范围比较。",
                    "要不要先定一个预算上限，再看具体选择？");
            if (Has(latest, "几个人|多少人|谁一起"))
                return Result("人数还需要双方核对，不替你确定参加名单。",
                    "你那边大概有几个人？我们先把人数核对一下。");
            if (hint.Missing.Count > 0)
            {
                var questions = hint.Missing.Select(field => Question(field, hint.Intent)).Distinct().ToArray();
                string first = string.Concat(questions.Take(2));
                string second = questions.Length > 2 ? questions[2] :
                    hint.Intent == "吃饭" ? "你有什么忌口，或比较想吃的菜系吗？" : "交通方式有偏好吗，比如高铁、自驾或飞机？";
                return Result($"可以先补齐{hint.Intent}安排：{string.Join("、", hint.Missing)}。", first, second);
            }
            return hint.Intent == "吃饭"
                ? Result("主要安排信息已经出现，可以再核对饮食偏好和预算。",
                    "你还有什么忌口，或人均预算方面的要求吗？",
                    "刚才提到的时间、地点和人数，还需要调整哪一项吗？")
                : Result("时间和路线有大致方向，可以继续比较交通偏好。",
                    "你更看重路上时间、花费，还是到达时间？",
                    "交通方式有偏好吗？我们再按预算比较一下。");
        }
        if (Has(latest, "[？?]|吗[。！!]*$|能不能|可不可以|怎么办|为什么"))
            return Result("这是普通聊天问题，本地草稿先澄清；需要更贴合上下文时可用千问优化。",
                "你能再具体说说吗？我想先了解清楚。",
                "你最希望先确认哪一部分？");
        return Result("可以用一个简短问题接续话题；草稿没有替你编造经历或承诺。",
            "你愿意再多说一点吗？", "你对这件事是怎么想的？");
    }

    private static string Question(string field, string intent) => field switch
    {
        "时间" => "你哪天、几点方便？",
        "地点" => "你想在哪个区域或哪家店见面？",
        "人数" => "大概有几个人一起？",
        "出发时间" => "你计划哪天、几点出发？",
        "出发地" => "你准备从哪里出发？",
        "目的地" => "你想去哪个城市或目的地？",
        _ => $"{field}有什么想法吗？"
    };

    private static bool IsCancellation(string text) =>
        !Has(text, "不要取消|不用取消|不取消|别取消") &&
        Has(text, "取消(?:了|吧|这次|安排|计划|聚餐|行程)|不去了|不聚了|不吃了|先不安排|先别安排|改天再说|^(?:那|还是)?算了");
}
