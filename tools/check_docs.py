#!/usr/bin/env python3
"""check_docs.py - fails when the agent guide falls behind the code.

Every finding / error code the server can return must be documented in the
rule reference of Api/AgentGuide.cs. Runs in CI before the container build
and locally before a release:  python tools/check_docs.py
"""
import os
import re
import sys

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'server', 'src', 'AddonStore.Web')
SOURCES = ['Validation/PackageValidator.cs', 'Services/SubmissionService.cs',
           'Api/ApiEndpoints.cs', 'Auth/ApiTokenAuthHandler.cs',
           'Services/PackageMetaService.cs']
EXTRA_CODES = {'CLIENT_ADMIN_ONLY', 'VERSION_EXISTS', 'VALIDATION_FAILED', 'NOT_OWNER', 'LIVE_VERSION',
               'TOKEN_INVALID', 'TOKEN_REVOKED', 'USER_NOT_ACTIVE', 'NO_PACKAGE',
               'METADATA_INVALID', 'PACKAGE_NOT_FOUND'}

code = ''
for rel in SOURCES:
    with open(os.path.join(ROOT, rel), encoding='utf-8') as f:
        code += f.read()

codes = set(re.findall(r'\.(?:Error|Warn|Info)\("([A-Z0-9_]+)"', code))
codes |= set(re.findall(r'new\("([A-Z0-9_]+)", "(?:error|warning)"', code))
codes |= {c for c in EXTRA_CODES if f'"{c}"' in code}

with open(os.path.join(ROOT, 'Api', 'AgentGuide.cs'), encoding='utf-8') as f:
    guide = f.read()

missing = sorted(c for c in codes if f'| {c} |' not in guide)
if missing:
    print('Agent guide is missing these codes in its rule reference:', ', '.join(missing))
    sys.exit(1)
print(f'OK: all {len(codes)} codes are documented in the agent guide.')
