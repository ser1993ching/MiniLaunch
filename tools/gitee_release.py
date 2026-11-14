#!/usr/bin/env python3
# 用 Gitee 私人令牌创建 Release 并上传附件
#
# 令牌来源（按优先级）：
#   1. 环境变量 GITEE_TOKEN
#   2. 命令行 --token <令牌>
#   3. git credential fill（https://gitee.com）—— 存的必须是「私人令牌」而不是账号密码
#
# 令牌生成：https://gitee.com/personal_access_tokens  → 勾选 projects 权限
# 用法：
#   GITEE_TOKEN=xxxx python tools/gitee_release.py 1.1 "MiniLaunch 1.1" \
#        publish/win-x64/MiniLaunch.exe publish/win-x64/MiniLaunch.zip publish/win-x64/VERSION.txt
import json
import os
import subprocess
import sys
import urllib.parse
import urllib.request
import urllib.error
import uuid

REPO = "yangwei01/mini-launch"
BASE = "https://gitee.com/api/v5"


def get_token():
    for i, a in enumerate(sys.argv):
        if a == "--token" and i + 1 < len(sys.argv):
            return sys.argv[i + 1]
    env = os.environ.get("GITEE_TOKEN")
    if env:
        return env.strip()
    try:
        p = subprocess.run(["git", "credential", "fill"],
                           input=b"protocol=https\nhost=gitee.com\n\n",
                           stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, timeout=30)
        for line in p.stdout.decode("utf-8", "replace").splitlines():
            if line.startswith("password="):
                return line.split("=", 1)[1].strip()
    except Exception:
        pass
    return None


def api(method, url, token, json_body=None):
    hdrs = {"User-Agent": "MiniLaunch-Release", "Accept": "application/json"}
    body = None
    if json_body is not None:
        body = json.dumps(json_body).encode("utf-8")
        hdrs["Content-Type"] = "application/json"
    sep = "&" if "?" in url else "?"
    full = url + sep + "access_token=" + urllib.parse.quote(token)
    req = urllib.request.Request(full, data=body, headers=hdrs, method=method)
    try:
        with urllib.request.urlopen(req, timeout=180) as r:
            raw = r.read()
    except urllib.error.HTTPError as e:
        msg = e.read()[:300].decode("utf-8", "replace")
        if e.code in (401, 403):
            raise SystemExit(
                "Gitee API 鉴权失败（HTTP %s）：%s\n"
                "→ 令牌无效，或 git 凭据里存的是**账号密码**而不是私人令牌。\n"
                "  请到 https://gitee.com/personal_access_tokens 新建令牌（勾选 projects 权限），\n"
                "  然后：GITEE_TOKEN=<令牌> python tools/gitee_release.py ..." % (e.code, msg))
        raise SystemExit("Gitee API %s %s 失败：HTTP %s %s" % (method, url, e.code, msg))
    return json.loads(raw.decode("utf-8")) if raw else None


def upload_asset(token, release_id, path, name):
    boundary = "----MiniLaunch" + uuid.uuid4().hex
    with open(path, "rb") as f:
        payload = f.read()
    body = b"".join([
        ("--%s\r\n" % boundary).encode(),
        ('Content-Disposition: form-data; name="file"; filename="%s"\r\n' % name).encode("utf-8"),
        b"Content-Type: application/octet-stream\r\n\r\n",
        payload,
        ("\r\n--%s--\r\n" % boundary).encode(),
    ])
    url = ("%s/repos/%s/releases/%s/attach_files?name=%s&size=%d"
           % (BASE, REPO, release_id, urllib.parse.quote(name), len(payload)))
    url += "&access_token=" + urllib.parse.quote(token)
    req = urllib.request.Request(url, data=body, method="POST", headers={
        "User-Agent": "MiniLaunch-Release",
        "Content-Type": "multipart/form-data; boundary=%s" % boundary,
    })
    try:
        with urllib.request.urlopen(req, timeout=900) as r:
            raw = r.read()
    except urllib.error.HTTPError as e:
        msg = e.read()[:300].decode("utf-8", "replace")
        raise SystemExit("上传 %s 失败：HTTP %s %s" % (name, e.code, msg))
    return json.loads(raw.decode("utf-8")) if raw else None


def main():
    argv = [a for a in sys.argv[1:] if not a.startswith("--")]
    token = get_token()
    if not token:
        raise SystemExit("没有找到 Gitee 令牌：设置环境变量 GITEE_TOKEN 后重试")
    tag = argv[0] if len(argv) > 0 else "1.1"
    title = argv[1] if len(argv) > 1 else "MiniLaunch " + tag
    files = argv[2:]

    body = """Windows 极简启动器，单文件免安装（自包含 + 裁剪，约 11.8 MB）。

- `MiniLaunch.exe`：直接双击运行，无需安装 .NET
- `MiniLaunch-<版本>-win-x64.zip`：同上打包

若 Windows 弹出「已保护你的电脑」蓝色窗口，点 **更多信息 → 仍要运行** 即可。
"""

    existing = api("GET", "%s/repos/%s/releases" % (BASE, REPO), token)
    rel = None
    for r in (existing or []):
        if r.get("tag_name") == tag:
            rel = r
            break
    if rel:
        print("已存在 Release id=%s tag=%s" % (rel["id"], rel["tag_name"]))
    else:
        rel = api("POST", "%s/repos/%s/releases" % (BASE, REPO), token, json_body={
            "tag_name": tag, "name": title, "body": body,
            "prerelease": False, "target_commitish": "master",
        })
        print("已创建 Release id=%s tag=%s" % (rel["id"], rel["tag_name"]))

    have = {a.get("name") for a in (rel.get("assets") or [])}
    for path in files:
        name = os.path.basename(path)
        if name in have:
            print("跳过（已存在）%s" % name)
            continue
        r = upload_asset(token, rel["id"], path, name)
        print("已上传 %s → %s" % (name, (r or {}).get("browser_download_url")))
    print("Release 页面：https://gitee.com/%s/releases/tag/%s" % (REPO, tag))
    return 0


if __name__ == "__main__":
    sys.exit(main())
