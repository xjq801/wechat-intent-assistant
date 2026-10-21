import os
import dashscope
import sys
sys.path.append(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from tools.config import Qwen_API_KEY

def ask_audio(path_audio):

    api_key= Qwen_API_KEY()["api_key"]
    dashscope.base_http_api_url = Qwen_API_KEY()["dashscope_base_url"]

    messages = [
        {
            "role": "system",
            "content": [
                {"text": "将音频转换为文字"},
            ]
        },
        {
            "role": "user",
            "content": [
                {"audio": path_audio},
            ]
        }
    ]
    response = dashscope.MultiModalConversation.call(
        api_key=api_key,
        model="qwen3-asr-flash",
        messages=messages,
        result_format="message",
        asr_options={
            "enable_lid":True,
            "enable_itn":False
        }
    )

    if response.status_code == 200:
        text = response.output.choices[0].message.content[0]['text']
        return text
    else:
        print(f"识别失败：{response.message}")

if __name__ == '__main__':
    path_audio="D:/bishe2/111.mp3"
    print(ask_audio(path_audio))
