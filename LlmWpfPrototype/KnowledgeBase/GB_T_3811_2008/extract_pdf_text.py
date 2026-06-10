import pdfplumber
from pathlib import Path

# 你的真实路径（我已经帮你填好了）
pdf_path = Path(r"E:\反力架\反力架\三六重工v2\LlmWpfPrototype\KnowledgeBase\GB_T_3811_2008\3811-2008-gbt-e-300.pdf")
out_path = Path(r"E:\反力架\反力架\三六重工v2\LlmWpfPrototype\KnowledgeBase\GB_T_3811_2008\gb_t_3811_2008.txt")

texts = []

print("正在使用排版保留模式读取 PDF，请稍候...")

# 使用 pdfplumber 打开
with pdfplumber.open(pdf_path) as pdf:
    for page_index, page in enumerate(pdf.pages, start=1):
        texts.append(f"\n\n===== Page {page_index} =====\n\n")
        
        # 核心魔法：开启 layout=True，保留表格和多栏的物理排版空格
        text = page.extract_text(layout=True)
        
        if text and text.strip():
            texts.append(text)
        else:
            texts.append("[该页未提取到文本]\n")

out_path.parent.mkdir(parents=True, exist_ok=True)
out_path.write_text("".join(texts), encoding="utf-8")

print(f"🎉 提取彻底成功！已导出到: {out_path}")