from cozepy import Coze, TokenAuth, COZE_CN_BASE_URL
import json
import sys
import os
import re

sys.path.append(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
from tools.config import Coze_API_KEY_TRAVEL
from tools.qwen_client import qwen_chat_completion


# 处理文本转json格式
def parse_model_json(raw_content: str) -> dict:
    json_block = re.search(r'```json\s*(.*?)\s*```', raw_content, re.DOTALL)
    if json_block:
        json_str = json_block.group(1)
    else:
        left = raw_content.find("{")
        right = raw_content.rfind("}")
        if left == -1 or right == -1:
            return {}
        json_str = raw_content[left:right + 1]

    try:
        return json.loads(json_str)
    except json.JSONDecodeError:
        return {}


# 大模型调用
def qwen_ask(agent_prompt, user_text):
    messages = [
        {
            "role": "system",
            "content": agent_prompt
        },
        {
            "role": "user",
            "content": user_text
        }
    ]
    return qwen_chat_completion(messages)


# 具体功能实现--将文本信息转换为json形式的文本
from datetime import datetime


def train_ask(user_text):
    local_date = datetime.now().strftime("%Y-%m-%d")
    agent1_prompt = f'''你是出行信息整理专员，接收用户出行描述后，仅输出固定JSON结构，不额外输出任何文字。固定字段：出发地、目的地、预算范围、出行日期、当日时段；信息空缺统一填null。
                    规范要求：
                    1. 出行日期格式严格为XXXX-XX-XX；识别相对时间词自动换算标准日期，基准日期为{local_date}：
                    - 今天 = 2026-07-19
                    - 明天 = 2026-07-20
                    - 后天 = 2026-07-21
                    若用户同时给出具体数字日期+后天/明天等描述，以相对时间词换算结果为准；
                    2. 当日时段可填具体时刻（如08:30）或时段描述（早上/下午/夜晚），无信息填null；
                    3. 预算范围统一带上单位，如1000元以内、2000-3000元；
                    4. 不新增字段、不加多余解释文字，只返回JSON。

                    示例：
                    {{
                        "出发地": "宁德",
                        "目的地": "上海",
                        "预算范围": "4000-6000元",
                        "出行日期": "2026-07-22",
                        "当日时段": "早上09:00"
                    }}
                    '''
    text = qwen_ask(agent1_prompt, user_text)
    text = parse_model_json(text)
    return text


def train_answer(user_text):
    agent1_prompt = """你是专业高铁筛选助手，任务：根据用户出行需求（预算上限、出发时段），从给出的车次JSON数组筛选符合条件车次，严格按指定格式输出，无匹配输出“暂无符合你预算和时段要求的车次”。
                    规则要求：
                    1. 筛选条件为可选，非强制：
                    ① 用户给出预算上限 → 仅保留存在座位价格 ≤ 预算的车次；未给预算则不限制价格；
                    ② 用户给出出发时段（早上/中午/下午/晚上/具体时刻） → 仅保留发车时间落在对应时段的车次；未给出时段则不限制发车时间；
                    ③ 同时提供预算+时段：两个条件同时满足才保留车次；只给其中一个，仅校验对应条件；两个都没给，全部车次输出。
                    2. 时段时间划分标准：
                    早上：06:00-11:59
                    中午：12:00-13:59
                    下午：14:00-17:59
                    晚上：18:00-24:00
                    3. 座位过滤规则：
                    seat_number为"0000"直接忽略；seat_type为null的座位直接剔除；只输出剩余有效、价格符合预算的座位。
                    4. 单条车次固定输出格式，多条车次换行分隔：
                    火车编号：xxx
                    出发地：xxx
                    目的地：xxx
                    出发时间：xx月xx号xxx
                    到达时间：xx月xx号xxx
                    座位信息：[座位类型：xxx，价格：xxx，剩余票数：xxx]
                    5. 硬性约束：只输出1个车次、禁止输出多余解释、markdown、注释、JSON代码块，只输出筛选后的车次或固定无匹配文案。
                    6. 用户输入分为两部分：第一部分完整车次JSON，第二部分用户出行需求（预算、时段、起止地、日期）。
                    """
    text = qwen_ask(agent1_prompt, user_text)
    return text


# 判断是否缺失信息
def check_travel_info(travel_data: dict):
    # 定义字段与对应询问话术
    check_list = [
        ("出发地", "请问你的出发地是哪里？"),
        ("目的地", "请问你的目的地是哪里？"),
        ("出行日期", "请问你的出行日期是什么时候？")
    ]
    ask_msg = []
    for key, tip in check_list:
        val = travel_data.get(key)
        if val is None or str(val).strip() == "":
            ask_msg.append(tip)
    ask_text = "\n".join(ask_msg)
    # 存在缺失信息返回True，全部齐全返回False
    has_missing = len(ask_text.strip()) > 0
    return ask_text, has_missing


# 对coze返回的高铁信息进行解析
def parse_train_data(workflow_result):
    """
    解析Coze工作流返回的高铁数据，提取有效车次信息
    :param workflow_result: coze.workflows.runs.create 返回的结果对象
    :return: list[dict] 格式化后的车次信息列表
    """
    raw_data_str = workflow_result.data
    raw_data = json.loads(raw_data_str)
    train_list = raw_data.get("output", [])

    result = []
    for train in train_list:
        train_info = {
            "train_code": train.get("station_train_code", ""),
            "start_station": train.get("start_station", "").strip(),
            "end_station": train.get("end_station", "").strip(),
            "start_time": train.get("start_time", ""),
            "arrive_time": train.get("arrive_time", ""),
            "start_train_date": train.get("start_train_date", ""),
            "seat_info": []
        }

        seat_infos = train.get("seat_infos", [])
        for seat in seat_infos:
            seat_type = seat.get("seat_type")
            if seat_type is not None:
                seat_type = str(seat_type).strip()
            seat_price = seat.get("seat_price")
            seat_number = str(seat.get("seat_number", "")).strip()

            seat_item = {
                "seat_type": seat_type,
                "seat_price": seat_price,
                "seat_number": seat_number
            }
            train_info["seat_info"].append(seat_item)

        result.append(train_info)
    return result


# 调用coze查询高铁信息
def run_workflow_sdk(where_from, where_to, date):
    config = Coze_API_KEY_TRAVEL()
    coze = Coze(auth=TokenAuth(config["API_TOKEN"]), base_url=COZE_CN_BASE_URL)
    # 调用工作流并解析
    result = coze.workflows.runs.create(
        workflow_id=config["WORKFLOW_ID"],
        parameters={
            "from": where_from,
            "to": where_to,
            "train_date": date
        },
        is_async=False
    )
    train_data = parse_train_data(result)
    # 转为格式化JSON字符串
    json_str = json.dumps(train_data, ensure_ascii=False, indent=2)
    return json_str


def train_ret(text1):
    text = train_ask(text1)
    where_from = text["出发地"]
    where_to = text["目的地"]
    date = text["出行日期"]
    text, text_bool = check_travel_info(text)
    if text_bool == False:  # 信息齐全
        train_json = run_workflow_sdk(where_from, where_to, date)
        # print("===== JSON格式车次信息 =====")
        # print(train_json)
        train_json += text1
        return train_answer(train_json)
    else:  # 信息不全
        return text


if __name__ == "__main__":
    text1 = '''
    目的地上海，出发地北京，车费1000以内，当日时间是2026年7月19日，打算26号去，早上9点之后的车
    '''
    print("根据你的出行条件，为你匹配到最合适的列车：")
    print(train_ret(text1))
