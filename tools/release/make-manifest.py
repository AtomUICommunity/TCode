#!/usr/bin/env python3
"""生成并签名更新清单 manifest.json（docs/design/auto-update.md §4）。

扫描产物目录中各平台的安装程序（zip / setup.exe / tar.gz），写出 manifest.json，
再用 openssl 以 ECDSA P-256 + SHA-256 签名得到 manifest.json.sig（DER，客户端用 Rfc3279DerSequence 验证）。
只用标准库与 openssl 命令行。用法：

  python3 -I tools/release/make-manifest.py --version 1.2.0 --dir dist --key update-signing-key.pem [--prerelease]
"""
import argparse
import datetime
import hashlib
import json
import pathlib
import subprocess
import sys

REPO = "AtomUICommunity/TKCode"
# 每个运行时标识对应的安装程序文件名与最低系统版本
ASSETS = {
    "osx-arm64": ("TKCode-{v}-osx-arm64.zip", "11.0"),
    "osx-x64": ("TKCode-{v}-osx-x64.zip", "11.0"),
    "win-x64": ("TKCode-{v}-win-x64-setup.exe", None),
    "linux-x64": ("TKCode-{v}-linux-x64.tar.gz", None),
}


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--version", required=True, help="不带 v 前缀，例如 1.2.0 或 1.2.0-beta.1")
    parser.add_argument("--dir", required=True, help="安装程序所在目录；manifest.json 与 .sig 也写到这里")
    parser.add_argument("--key", required=True, help="ECDSA P-256 私钥（PEM）")
    parser.add_argument("--prerelease", action="store_true", help="预览版（对应 GitHub prerelease）")
    args = parser.parse_args()

    out = pathlib.Path(args.dir)
    assets = {}
    for rid, (pattern, min_os) in ASSETS.items():
        path = out / pattern.format(v=args.version)
        if not path.exists():
            print(f"skip {rid}: {path.name} not found", file=sys.stderr)
            continue
        entry = {"name": path.name, "size": path.stat().st_size, "sha256": sha256(path)}
        if min_os:
            entry["minOs"] = min_os
        assets[rid] = entry
    if not assets:
        sys.exit("no installers found")

    manifest = {
        "schema": 1,
        "version": args.version,
        "channel": "preview" if args.prerelease else "stable",
        "publishedAt": datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "notesUrl": f"https://github.com/{REPO}/releases/tag/v{args.version}",
        "assets": assets,
    }
    manifest_path = out / "manifest.json"
    manifest_path.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    subprocess.run(["openssl", "dgst", "-sha256", "-sign", args.key, "-out", str(out / "manifest.json.sig"), str(manifest_path)], check=True)
    print(json.dumps(manifest, indent=2))


if __name__ == "__main__":
    main()
