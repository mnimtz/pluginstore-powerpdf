# get_version.py - prints the client version from common\version.h.
import os
import re

h = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "common", "version.h")
m = re.search(r'#define\s+FP_VERSION_A\s+"([\d.]+)"', open(h, encoding="utf-8").read())
if not m:
    raise SystemExit("FP_VERSION_A not found in common/version.h")
print(m.group(1))
