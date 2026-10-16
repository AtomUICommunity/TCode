#!/usr/bin/env python3
"""把 models.dev 的 api.json 裁剪成 TCode 的内置模型目录（src/TCode.AgentHost/Catalog/catalog.json）。

只保留能映射到 TCode 三种协议的厂商，以及支持工具调用、未弃用、输出文本的模型。
规则与 src/TCode.AgentHost/Catalog/ModelsDevTrimmer.cs（运行时在线同步）一致，修改时两处同步，
并用 TCODE_MODELSDEV_FILE=<同一份 api.json> 运行 TrimmerParityTests 验证。
用法：
  curl -sSL https://models.dev/api.json -o /tmp/models.dev.json
  python3 -I tools/gen_catalog.py /tmp/models.dev.json
"""
import datetime
import json
import pathlib
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
OUT = ROOT / "src/TCode.AgentHost/Catalog/catalog.json"

# npm 包 → (协议, 缺省 Base URL)
NPM = {
    "@ai-sdk/openai-compatible": ("chat", None),
    "@openrouter/ai-sdk-provider": ("chat", "https://openrouter.ai/api/v1"),
    "@ai-sdk/xai": ("chat", "https://api.x.ai/v1"),
    "@ai-sdk/groq": ("chat", "https://api.groq.com/openai/v1"),
    "@ai-sdk/cerebras": ("chat", "https://api.cerebras.ai/v1"),
    "@ai-sdk/deepinfra": ("chat", "https://api.deepinfra.com/v1/openai"),
    "@ai-sdk/openai": ("responses", "https://api.openai.com/v1"),
    "@ai-sdk/anthropic": ("messages", "https://api.anthropic.com"),
}


def model_entry(m):
    if not m.get("tool_call") or m.get("status") == "deprecated" or m.get("type") in ("embedding", "reranking"):
        return None
    modalities = m.get("modalities") or {}
    if "text" not in (modalities.get("output") or ["text"]):
        return None
    out = {"name": m.get("name") or m["id"]}
    limit = m.get("limit") or {}
    if limit.get("context"):
        out["contextWindow"] = limit["context"]
    if limit.get("output"):
        out["maxTokens"] = limit["output"]
    if m.get("reasoning"):
        out["reasoning"] = True
        for option in m.get("reasoning_options") or []:
            if option.get("type") == "effort" and option.get("values"):
                out["efforts"] = option["values"]
            if option.get("type") == "toggle":
                out["canDisableThinking"] = True
    if "image" in (modalities.get("input") or []):
        out["images"] = True
    interleaved = m.get("interleaved")
    if isinstance(interleaved, dict) and interleaved.get("field") == "reasoning_content":
        out["reasoningContent"] = True
    if m.get("release_date"):
        out["released"] = m["release_date"]
    cost = m.get("cost") or {}
    if cost.get("input") is not None and cost.get("output") is not None:
        out["cost"] = [cost.get("input"), cost.get("output"), cost.get("cache_read"), cost.get("cache_write")]
    return out


def main():
    source = json.loads(pathlib.Path(sys.argv[1]).read_text(encoding="utf-8"))
    providers = {}
    for pid, p in sorted(source.items()):
        mapping = NPM.get(p.get("npm"))
        if mapping is None:
            continue
        api, default_url = mapping
        base_url = p.get("api") or default_url
        if not base_url:
            continue
        # AI SDK 的 Anthropic baseURL 含 /v1（再拼 /messages），TCode 的 Messages 适配器自己拼 /v1/messages
        if api == "messages" and base_url.rstrip("/").endswith("/v1"):
            base_url = base_url.rstrip("/")[:-3]
        models = {}
        for mid, m in p.get("models", {}).items():
            # 单个模型可能改用其它 SDK（例如同一厂商下的 Anthropic 兼容模型），这类模型跳过
            if (m.get("provider") or {}).get("npm") not in (None, p.get("npm")):
                continue
            entry = model_entry(m)
            if entry:
                models[mid] = entry
        if not models:
            continue
        providers[pid] = {
            "name": p.get("name") or pid,
            "api": api,
            "baseUrl": base_url,
            "env": p.get("env") or [],
            "models": models,
        }
    doc = {
        "source": "https://models.dev/api.json",
        "generated": datetime.date.today().isoformat(),
        "providers": providers,
    }
    OUT.write_text(json.dumps(doc, ensure_ascii=False, separators=(",", ":")), encoding="utf-8")
    count = sum(len(p["models"]) for p in providers.values())
    print(f"{len(providers)} providers, {count} models, {OUT.stat().st_size // 1024} KB -> {OUT.relative_to(ROOT)}")


if __name__ == "__main__":
    main()
