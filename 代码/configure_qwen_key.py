"""Configure the current Windows user's encrypted Qwen key without echoing it."""
import getpass
from tools.local_credentials import save_qwen_key

if __name__ == "__main__":
    key = getpass.getpass("Qwen API key (hidden): ").strip()
    if not key:
        raise SystemExit("No key entered.")
    save_qwen_key(key)
    print("Saved encrypted key for the current Windows user.")
