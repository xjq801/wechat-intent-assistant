"""Store a local Qwen API key using Windows DPAPI for the current user."""

import ctypes
from ctypes import wintypes
import os
from pathlib import Path
import sys
import tempfile


class _DataBlob(ctypes.Structure):
    _fields_ = [
        ("cbData", wintypes.DWORD),
        ("pbData", ctypes.POINTER(ctypes.c_ubyte)),
    ]


def default_secret_path() -> Path:
    return Path(__file__).resolve().parent.parent / ".local" / "qwen_api_key.dpapi"


def _crypt(data: bytes, *, decrypt: bool) -> bytes:
    if os.name != "nt":
        raise RuntimeError("Local credentials require Windows DPAPI.")

    try:
        crypt32 = ctypes.WinDLL("crypt32", use_last_error=True)
        kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
        blob_pointer = ctypes.POINTER(_DataBlob)
        crypt32.CryptProtectData.argtypes = [
            blob_pointer, wintypes.LPCWSTR, blob_pointer, ctypes.c_void_p,
            ctypes.c_void_p, wintypes.DWORD, blob_pointer,
        ]
        crypt32.CryptProtectData.restype = wintypes.BOOL
        crypt32.CryptUnprotectData.argtypes = [
            blob_pointer, ctypes.POINTER(wintypes.LPWSTR), blob_pointer,
            ctypes.c_void_p, ctypes.c_void_p, wintypes.DWORD, blob_pointer,
        ]
        crypt32.CryptUnprotectData.restype = wintypes.BOOL
        kernel32.LocalFree.argtypes = [ctypes.c_void_p]
        kernel32.LocalFree.restype = ctypes.c_void_p
    except Exception:
        raise RuntimeError("Windows DPAPI is unavailable.") from None

    input_buffer = (ctypes.c_ubyte * len(data)).from_buffer_copy(data)
    input_blob = _DataBlob(len(data), input_buffer)
    output_blob = _DataBlob()
    try:
        # CRYPTPROTECT_UI_FORBIDDEN only: never use CRYPTPROTECT_LOCAL_MACHINE.
        operation = crypt32.CryptUnprotectData if decrypt else crypt32.CryptProtectData
        if not operation(
            ctypes.byref(input_blob), None, None, None, None, 0x01,
            ctypes.byref(output_blob),
        ):
            raise RuntimeError("Windows DPAPI operation failed.")
        return ctypes.string_at(output_blob.pbData, output_blob.cbData)
    finally:
        ctypes.memset(input_buffer, 0, len(data))
        if output_blob.pbData:
            ctypes.memset(output_blob.pbData, 0, output_blob.cbData)
            kernel32.LocalFree(ctypes.cast(output_blob.pbData, ctypes.c_void_p))


def save_qwen_key(secret: str, path: Path | None = None) -> None:
    """Encrypt and atomically replace the key; no plaintext is written to disk."""
    if not isinstance(secret, str) or not secret.strip() or "\n" in secret or "\r" in secret:
        raise ValueError("The API key must be a non-empty single line.")
    try:
        encoded = secret.encode("utf-8")
    except UnicodeError:
        raise ValueError("The API key must contain valid UTF-8 text.") from None
    encrypted = _crypt(encoded, decrypt=False)
    temporary_path = None
    try:
        destination = Path(path) if path is not None else default_secret_path()
        destination.parent.mkdir(parents=True, exist_ok=True)
        with tempfile.NamedTemporaryFile(
            mode="wb", dir=destination.parent, prefix=".qwen-", suffix=".tmp",
            delete=False,
        ) as temporary:
            temporary_path = Path(temporary.name)
            temporary.write(encrypted)
            temporary.flush()
            os.fsync(temporary.fileno())
        os.replace(temporary_path, destination)
        temporary_path = None
    except Exception:
        raise RuntimeError("Unable to save the encrypted API key.") from None
    finally:
        if temporary_path is not None:
            try:
                temporary_path.unlink(missing_ok=True)
            except OSError:
                pass


def load_qwen_key(path: Path | None = None) -> str:
    """Return an empty string when missing; otherwise decrypt as the current user."""
    try:
        source = Path(path) if path is not None else default_secret_path()
        encrypted = source.read_bytes()
    except FileNotFoundError:
        return ""
    except Exception:
        raise RuntimeError("Unable to read the encrypted API key.") from None
    decrypted = _crypt(encrypted, decrypt=True)
    try:
        secret = decrypted.decode("utf-8")
    except UnicodeError:
        raise RuntimeError("The encrypted API key contains invalid text.") from None
    if not secret.strip() or "\n" in secret or "\r" in secret:
        raise RuntimeError("The encrypted API key contains invalid text.")
    return secret


def main() -> int:
    # Never echo rejected arguments: a caller may accidentally pass a key there.
    if sys.argv[1:] != ["--save"]:
        print("Usage: python -m tools.local_credentials --save", file=sys.stderr)
        return 2
    try:
        secret = sys.stdin.readline().rstrip("\r\n")
        save_qwen_key(secret)
    except Exception:
        print("Unable to save the encrypted API key.", file=sys.stderr)
        return 1
    print("SAVED")
    print(default_secret_path())
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
