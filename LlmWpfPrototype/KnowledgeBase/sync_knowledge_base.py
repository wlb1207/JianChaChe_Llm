# -*- coding: utf-8 -*-
"""
可扩展多标准知识库同步工具 (KnowledgeBase/sync_knowledge_base.py)

用途：
1. 自动扫描 KnowledgeBase/ 下所有标准子目录（包含 *_key_rules.json / key_rules.json 或自由添加的 .md/.txt 文档）；
2. 对新放入的 .pdf / .docx 原始文件按需提取纯文本 .txt（若本机安装 PyMuPDF / python-docx）；
3. 生成/更新 KnowledgeBase/knowledge_base_manifest.json 统一清单（记录所有标准、条目数与内容指纹）；
4. 使用 --sync-disclosure 参数时，基于全部已核对规则卡片重新生成 DisclosurePolicy/approved-sources.json，
   并输出新的 PolicyHash 以便人工审核后更新 KnowledgeDisclosureGuard.cs。

示例：
    python sync_knowledge_base.py
    python sync_knowledge_base.py --sync-disclosure
"""
import argparse
import hashlib
import json
from datetime import datetime, timezone
from pathlib import Path

KB_ROOT = Path(__file__).resolve().parent
APP_ROOT = KB_ROOT.parent
DISCLOSURE_PATH = APP_ROOT / "DisclosurePolicy" / "approved-sources.json"
MANIFEST_PATH = KB_ROOT / "knowledge_base_manifest.json"
LEGACY_GB3811_FOLDER = "GB_T_3811_2008"


def sha256_file(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest().upper()


def extract_new_sources(pack_dir: Path) -> list[str]:
    extracted = []
    # Skip auto-extracting PDF/DOCX if this pack already has a non-empty full-text .txt archive.
    existing_txts = [p for p in pack_dir.glob("*.txt") if p.stat().st_size > 0]
    if existing_txts:
        return extracted

    for pdf in sorted(pack_dir.glob("*.pdf")):
        txt_target = pdf.with_suffix(".txt")
        if txt_target.exists() and txt_target.stat().st_size > 0:
            continue
        try:
            import fitz  # PyMuPDF
            doc = fitz.open(str(pdf))
            parts = []
            for idx, page in enumerate(doc):
                t = page.get_text("text").strip()
                if t:
                    parts.append(f"=== Page {idx + 1} ===\n{t}")
            if parts:
                txt_target.write_text("\n\n".join(parts) + "\n", encoding="utf-8")
                extracted.append(txt_target.name)
        except Exception as exc:
            print(f"[WARN] Failed extracting PDF {pdf.name}: {exc}")

    for docx_file in sorted(pack_dir.glob("*.docx")):
        txt_target = docx_file.with_suffix(".txt")
        if txt_target.exists() and txt_target.stat().st_size > 0:
            continue
        try:
            import docx
            d = docx.Document(str(docx_file))
            lines = [p.text.strip() for p in d.paragraphs if p.text.strip()]
            for table in d.tables:
                for row in table.rows:
                    cells = [c.text.strip().replace("\n", " ") for c in row.cells]
                    if any(cells):
                        lines.append(" | ".join(cells))
            if lines:
                txt_target.write_text("\n".join(lines) + "\n", encoding="utf-8")
                extracted.append(txt_target.name)
        except Exception as exc:
            print(f"[WARN] Failed extracting DOCX {docx_file.name}: {exc}")
    return extracted


def find_key_rules_file(pack_dir: Path) -> Path | None:
    candidates = sorted(pack_dir.glob("*key_rules.json"))
    if candidates:
        return candidates[0]
    fallback = pack_dir / "rules.json"
    return fallback if fallback.exists() else None


def discover_packs() -> tuple[list[dict], list[dict]]:
    packs = []
    approved_entries = []

    # Sort with GB_T_3811_2008 first so legacy verified-rule-0001..0012 chunk IDs remain 100% stable!
    subdirs = [d for d in KB_ROOT.iterdir() if d.is_dir() and not d.name.startswith(".")]
    subdirs.sort(key=lambda d: (0 if d.name == LEGACY_GB3811_FOLDER else 1, d.name.lower()))

    global_order = 0
    for pack_dir in subdirs:
        extracted = extract_new_sources(pack_dir)
        if extracted:
            print(f"[INFO] Extracted text in {pack_dir.name}: {', '.join(extracted)}")

        rules_file = find_key_rules_file(pack_dir)
        is_legacy_3811 = pack_dir.name == LEGACY_GB3811_FOLDER
        pack_entry = {
            "packId": pack_dir.name,
            "standard": pack_dir.name,
            "name": pack_dir.name,
            "description": "",
            "rulesFile": "",
            "ruleCount": 0,
            "looseMarkdownCount": 0,
            "rules": [],
        }

        referenced_rel_files = set()
        if rules_file is not None:
            data = json.loads(rules_file.read_text(encoding="utf-8"))
            pack_entry["standard"] = data.get("standard") or pack_dir.name
            pack_entry["name"] = data.get("name") or pack_dir.name
            pack_entry["description"] = data.get("description") or ""
            pack_entry["rulesFile"] = f"{pack_dir.name}/{rules_file.name}"
            rules = data.get("rules") or data.get("sampleRules") or []
            for idx, rule in enumerate(rules, start=1):
                rel_file = (rule.get("file") or "").replace("\\", "/").lstrip("/")
                if not rel_file:
                    continue
                md_path = pack_dir / rel_file
                if not md_path.exists():
                    print(f"[WARN] Missing markdown referenced by {rules_file}: {rel_file}")
                    continue
                referenced_rel_files.add(rel_file.lower())
                global_order += 1
                chunk_id = (
                    f"verified-rule-{idx:04d}"
                    if is_legacy_3811
                    else f"{pack_dir.name.lower()}-rule-{idx:04d}"
                )
                source_doc = rel_file if is_legacy_3811 else f"{pack_dir.name}/{rel_file}"
                file_hash = sha256_file(md_path)
                rule_meta = {
                    "id": rule.get("id") or f"{pack_dir.name}-{idx:02d}",
                    "chunkId": chunk_id,
                    "sourceDocument": source_doc,
                    "topic": rule.get("topic") or rule.get("title") or md_path.stem,
                    "clause": rule.get("clause") or "",
                    "sha256": file_hash,
                }
                pack_entry["rules"].append(rule_meta)
                approved_entries.append({
                    "file": source_doc,
                    "sha256": file_hash,
                    "chunkId": chunk_id,
                    "clause": rule_meta["clause"],
                    "standard": pack_entry["standard"],
                })

        # Discover loose Markdown files not listed in key_rules.json (e.g., user-dropped notes/docs)
        loose_files = []
        for md in sorted(pack_dir.rglob("*.md")):
            rel = md.relative_to(pack_dir).as_posix()
            if rel.lower() in referenced_rel_files:
                continue
            loose_files.append(f"{pack_dir.name}/{rel}")
        pack_entry["ruleCount"] = len(pack_entry["rules"])
        pack_entry["looseMarkdownCount"] = len(loose_files)
        pack_entry["looseMarkdownFiles"] = loose_files

        if pack_entry["ruleCount"] > 0 or pack_entry["looseMarkdownCount"] > 0:
            packs.append(pack_entry)

    return packs, approved_entries


def main() -> None:
    parser = argparse.ArgumentParser(description="Sync extensible multi-standard KnowledgeBase manifest.")
    parser.add_argument("--sync-disclosure", action="store_true", help="Regenerate DisclosurePolicy/approved-sources.json")
    args = parser.parse_args()

    packs, approved_entries = discover_packs()
    manifest = {
        "schemaVersion": 2,
        "generatedAtUtc": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "packCount": len(packs),
        "totalRuleCount": sum(p["ruleCount"] for p in packs),
        "packs": packs,
    }
    MANIFEST_PATH.write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"[OK] Wrote {MANIFEST_PATH} ({manifest['packCount']} packs, {manifest['totalRuleCount']} rules)")

    if args.sync_disclosure:
        endpoint = "https://api.kgnas.top/v1/chat/completions"
        if DISCLOSURE_PATH.exists():
            try:
                old = json.loads(DISCLOSURE_PATH.read_text(encoding="utf-8"))
                endpoint = old.get("endpoint") or endpoint
            except Exception:
                pass
        policy = {
            "schemaVersion": 2,
            "approval": "User-confirmed multi-standard engineering knowledge base (GB/T 3811-2008, JT/T 1430-2022, GB 50017-2017, GB/T 6728-2025, GB/T 1591-2018, GB 50661-2011)",
            "endpoint": endpoint,
            "documents": approved_entries,
        }
        DISCLOSURE_PATH.parent.mkdir(parents=True, exist_ok=True)
        DISCLOSURE_PATH.write_text(json.dumps(policy, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        policy_hash = sha256_file(DISCLOSURE_PATH)
        print(f"[OK] Updated {DISCLOSURE_PATH} ({len(approved_entries)} approved rule cards)")
        print(f"[HASH] New PolicyHash = {policy_hash}")


if __name__ == "__main__":
    main()
