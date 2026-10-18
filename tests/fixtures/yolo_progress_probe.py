"""Subprocess protocol fixture: exercises Python-to-.NET encoding without GPU or training."""
import json
import sys

print(json.dumps({"stage": "训练", "epoch": 1, "epochs": 2}, ensure_ascii=False), flush=True)
print(json.dumps({"stage": "预测", "processed": 1, "total": 2}, ensure_ascii=False), flush=True)
print("中文错误详情", file=sys.stderr, flush=True)
