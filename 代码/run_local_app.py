"""Load and verify the trained local model before serving the chat UI."""
import os
import runpy
from pathlib import Path

ROOT = Path(__file__).resolve().parent
os.chdir(ROOT)
os.environ.setdefault("CHAPTER2_HOST", "127.0.0.1")
os.environ.setdefault("CHAPTER2_PORT", "5000")

from model_loader import get_predictor
from tools.config import Qwen_API_KEY

if __name__ == "__main__":
    qwen_config = Qwen_API_KEY()
    if qwen_config["api_key"]:
        print(f"CLOUD_QWEN_CONFIGURED model={qwen_config['model']} endpoint={qwen_config['base_url']}", flush=True)
    predictor = get_predictor()
    predictor.load()
    print("LOCAL_INTENT_MODEL_READY", flush=True)
    runpy.run_path(str(ROOT / "app.py"), run_name="__main__")
