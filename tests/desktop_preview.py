"""Build and exercise the desktop against a disposable local backend.

Requires .NET 8+ and a graphical desktop (or Xvfb on Linux). Only synthetic
accounts/messages are created. Runtime logs stay in a temporary directory.
"""
import argparse
import base64
import os
from pathlib import Path
import secrets
import socket
import subprocess
import tempfile
import time
import urllib.request


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--out", type=Path, required=True, help="New directory for PNG screenshots")
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--mvp-only", action="store_true", help="Only account/settings/pins/composer checks")
    mode.add_argument("--manual", action="store_true", help="Open a seeded, isolated desktop until the window closes")
    args = parser.parse_args()
    output = args.out.resolve()
    output.mkdir(parents=True, exist_ok=False)
    repo = Path(__file__).resolve().parents[1]
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        port = sock.getsockname()[1]
    address = f"http://127.0.0.1:{port}"
    with tempfile.TemporaryDirectory(prefix="messenger-ui-check-") as directory:
        temp = Path(directory)
        for project in ("backend", "desktop"):
            subprocess.run(["dotnet", "build", project, "-c", "Release", "--nologo", "-v", "quiet",
                            "-o", str(temp / project)], cwd=repo, check=True)
        desktop = temp / "desktop/MessengerDesktop.dll"
        for check in ("--check-preferences", "--check-pins"):
            subprocess.run(["dotnet", str(desktop), check], cwd=repo, check=True)
        env = {**os.environ, "ASPNETCORE_URLS": address, "Db": str(temp / "check.db"),
               "MESSENGER_FILES": str(temp / "files"), "MESSENGER_DEV_SEED": "false",
               "MESSENGER_KEY": base64.b64encode(secrets.token_bytes(32)).decode(),
               "Logging__LogLevel__Default": "Warning"}
        with (temp / "runtime.log").open("w+", encoding="utf-8") as log:
            server = subprocess.Popen(["dotnet", str(temp / "backend/Messenger.Api.dll")],
                                      cwd=temp, env=env, stdout=log, stderr=log,
                                      creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0)
            try:
                for _ in range(100):
                    if server.poll() is not None:
                        raise RuntimeError("Temporary backend exited before becoming ready")
                    try:
                        with urllib.request.urlopen(address + "/messenger/health", timeout=1):
                            break
                    except OSError:
                        time.sleep(.1)
                else:
                    raise RuntimeError("Temporary backend did not become ready")
                if args.manual:
                    print("Opening isolated desktop for manual testing", flush=True)
                    subprocess.run(["dotnet", str(desktop), "--preview-manual", address + "/messenger", str(output)],
                                   cwd=temp, stdout=log, stderr=log, check=True)
                else:
                    for flags in (["--check", address + "/messenger"], ["--preview-mvp" if args.mvp_only else "--preview", address + "/messenger", str(output)]):
                        subprocess.run(["dotnet", str(desktop), *flags], cwd=temp, stdout=log, stderr=log, check=True, timeout=240)
            finally:
                server.terminate()
                try:
                    server.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    server.kill()
                    server.wait()
                log.flush()
                log.seek(0)
                # Never dump raw HTTP/WebSocket logs: they can contain test session tokens.
                for line in log:
                    if line.startswith(("PASS ", "CHECK FAIL ", "PREVIEW FAIL ")):
                        print(line.rstrip())
    print(f"PASS {'isolated desktop closed' if args.manual else 'desktop integration and screenshots'}: {output}")


if __name__ == "__main__":
    main()
