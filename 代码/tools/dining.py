from cozepy import Coze, TokenAuth, COZE_CN_BASE_URL
import json
import sys
import os
import json
import re
sys.path.append(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
from tools.config import Coze_API_KEY_DINING
from tools.qwen_client import qwen_chat_completion

#解析Coze工作流返回的周边餐饮门店数据
def parse_dining_data(workflow_result):

    raw_json_str = workflow_result.data
    clean_str = re.sub(r'":\s*"([^"]*?)\s*"', r'":"\1"', raw_json_str)

    try:
        raw_data = json.loads(clean_str)
    except json.JSONDecodeError as e:
        print(f"JSON解析失败：{str(e)}")
        return []

    output = raw_data.get("output", {})
    shop_list = output.get("results", [])
    if not shop_list:
        return []

    res = []
    for shop in shop_list:
        shop_item = {
            "name": shop.get("name", ""),          # 门店名称
            "province": shop.get("province", ""),  # 省份
            "city": shop.get("city", ""),          # 城市
            "area": shop.get("area", ""),          # 行政区
            "full_address": shop.get("address", ""),# 完整地址
            "location": shop.get("location", "")   # 经纬度 逗号分隔字符串
        }
        res.append(shop_item)
    return res

def qwen_ask(agent_prompt,user_text):
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

def dining_ask(user_text):
    agent1_prompt = '''
                    你是餐饮需求整合提取助手，严格遵循以下全部规则：
                    1. 忽略人名、用餐时间等无关信息；
                    2. 优先提取用户想吃的食物大类，麻辣锅底、鸳鸯锅、微辣等火锅相关细分描述统一合并为单个核心品类词；
                    3. 若用户未说明具体食物，仅表达不吃辣、不要辣、口味温和等无辣需求，统一输出「清淡」，严禁使用“不辣”这类反义词；
                    4. 最终仅输出一个纯关键词，无标点、无换行、无任何多余解释、无额外文字。
                    5. 如果出现不同的饮食类型，则使用少数服从多数的原则

                    示例1
                    输入：小夏：菜系火锅、周日中午；阿泽：喜欢麻辣锅底；晚晚：微辣鸳鸯锅
                    输出：火锅

                    示例2
                    输入：小红：不吃辣，想要温和口味的食物
                    输出：清淡
                    '''
    text=qwen_ask(agent1_prompt,user_text)
    return text

def dining_answer(user_text):
    agent1_prompt = '''
                    你是门店智能筛选助手，严格遵守所有执行规则：
                    1. 依据用户需求关键词，从饭店目录里挑选匹配度最高的1家门店；
                    2. 仅输出饭店名称、完整详细地址两项内容，两项信息分行展示；
                    3. 不要JSON、不要经纬度、不要省市区域拆分、不要任何多余说明文字。

                    输出固定格式：
                    饭店名称：xxx
                    详细地址：xxx

                    '''
    text=qwen_ask(agent1_prompt,user_text)
    return text

# 调用coze查询饭店
def run_workflow_sdk(location,keyword,radius):
    config = Coze_API_KEY_DINING()
    coze = Coze(auth=TokenAuth(config["API_TOKEN"]), base_url=COZE_CN_BASE_URL)
    # 调用工作流并解析
    result = coze.workflows.runs.create(
        workflow_id=config["WORKFLOW_ID"],
        parameters={
            "location": location,
            "keyword": keyword,
            "radius":radius
        },
        is_async=False
    )
    shop_data = parse_dining_data(result)
    #print(result)
    #train_data = parse_train_data(result)
    # 转为格式化JSON字符串
    #json_str = json.dumps(train_data, ensure_ascii=False, indent=2)
    return shop_data

def dining_ret(text1):
    text=dining_ask(text1)
    location="中国传媒大学"
    keyword=f"饭店 {text}"
    radius=10000
    text=run_workflow_sdk(location,keyword,radius)
    text=f"饭店信息；\n 用户要求"
    text=dining_answer(text+text1)
    return text



# ------------------- 调用示例 -------------------
if __name__ == "__main__":

    #找关键词进行查询
    require_text="火锅"#关键词
    location="中国传媒大学"
    keyword=f"饭店 {require_text}"
    radius=10000
    shop_list=run_workflow_sdk(location,keyword,radius)
    shop_str = json.dumps(shop_list, ensure_ascii=False, indent=2)

    text=f"饭店信息；{shop_str}\n 用户要求:{require_text}"#饭店筛选
    print(text)
    text=dining_answer(text)
    print(text)
