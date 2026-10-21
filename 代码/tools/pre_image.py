import base64
import os
import sys
#sys.path.append(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from tools.config import Doubao_API_KEY

def ask_image(image_path):

    client = Doubao_API_KEY()
    # 把图片转成 Base64
    with open(image_path, "rb") as f:
        base64_data = base64.b64encode(f.read()).decode("utf-8")
        image_base64 = f"data:image/png;base64,{base64_data}"

    # 调用多模态识图
    response = client.responses.create(
        thinking={"type": "disabled"},
        model="doubao-seed-1-6-flash-250828",
        input=[
            {
                "role": "user",
                "content": [
                    {
                        "type": "input_image",
                        "image_url": image_base64
                    },
                    {
                        "type": "input_text",
                        "text": '''
                                担任视觉分析助手。你需要按照以下规则，对提供的图片和视频内容进行整体总结：
                                仅对素材做整体概括。
                                不得添加额外细节与主观评价。
                                只说明核心主题，不做细节描述。
                                要求：
                                仅输出素材简洁的整体总结。
                                做到简短、清晰、准确。
                                '''
                    }
                ]
            }
        ]
    )

    ans_text = ""
    for out in response.output:
        if out.type == "message":
            ans_text = out.content[0].text
            break
    return ans_text

if __name__ == '__main__':
    ans_text=ask_image("D:/bishe2/111.png")
    print(ans_text)
