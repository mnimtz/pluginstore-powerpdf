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
SOURCES = ['Validation/PackageValidator.cs', 'Validation/ReportView.cs', 'Services/SubmissionService.cs',
           'Api/ApiEndpoints.cs', 'Auth/ApiTokenAuthHandler.cs',
           'Services/PackageMetaService.cs', 'Services/CategoryService.cs', 'Services/SourceService.cs',
           'Services/CustomerService.cs', 'Services/TemplateService.cs', 'Program.cs']
EXTRA_CODES = {'CLIENT_ADMIN_ONLY', 'VERSION_EXISTS', 'VALIDATION_FAILED', 'NOT_OWNER', 'LIVE_VERSION',
               'TOKEN_INVALID', 'TOKEN_REVOKED', 'USER_NOT_ACTIVE', 'NO_PACKAGE',
               'METADATA_INVALID', 'PACKAGE_NOT_FOUND', 'SOURCE_REJECTED', 'SOURCE_MISSING', 'ADMIN_ONLY',
               'QUERY_INVALID', 'VERSION_NOT_FOUND', 'AI_REVIEW_MISSING', 'AI_OFF', 'AI_FAILED',
               'CUSTOMER_INVALID', 'CUSTOMER_NOT_FOUND', 'DELIVERY_NOT_FOUND', 'DELIVERY_EXISTS', 'DELIVERY_INVALID',
               'PROMOTE_NOTHING', 'CODE_NOT_FOUND', 'VISIBILITY_INVALID', 'VISIBILITY_KEPT', 'CSRF_CHECK', 'RATE_LIMITED', 'BUNDLE_INVALID', 'BLOCK_INVALID'}

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

# Every hard package rule (severity exactly "error" in the rule reference) must be
# in the pre-flight checklist: the manual upload has no dry run, so the checklist
# is the only check an assistant can run there (S1.0.7).
ref = guide[guide.index('## Complete rule reference'):guide.index('## Good citizenship')]
hard = re.findall(r'^\| ([A-Z0-9_]+) \| error \|', ref, re.M)
pre = guide[guide.index('## Pre-flight checklist'):guide.index('## Compliance audit')]
gap = sorted(c for c in hard if c not in pre)
if gap:
    print('Pre-flight checklist is missing these hard rules:', ', '.join(gap))
    sys.exit(1)
print(f'OK: all {len(hard)} hard rules are in the pre-flight checklist.')

# Resource keys are case-insensitive: "Warning" next to "warning" makes the
# lookup return either one (S1.0.9). Every key must be unique ignoring case.
import glob
from collections import Counter
dups = {}
for res in glob.glob(os.path.join(ROOT, 'Resources', 'SharedResource.*.resx')):
    with open(res, encoding='utf-8-sig') as f:
        names = re.findall(r'<data name="([^"]+)"', f.read())
    d = [k for k, n in Counter(x.lower() for x in names).items() if n > 1]
    if d:
        dups[os.path.basename(res)] = d
if dups:
    print('Resource keys that differ only in case:', dups)
    sys.exit(1)
print('OK: resource keys are unique ignoring case.')

# Every /api route must be described in the vendor-neutral OpenAPI document
# (Api/OpenApiDoc.cs), so assistants other than Claude see it too.
with open(os.path.join(ROOT, 'Api', 'ApiEndpoints.cs'), encoding='utf-8') as f:
    endpoints = f.read()
with open(os.path.join(ROOT, 'Api', 'OpenApiDoc.cs'), encoding='utf-8') as f:
    openapi = f.read()


def norm(path):
    path = re.sub(r'\{\*\*(\w+)\}', r'{\1}', path)
    return re.sub(r'\{(\w+):\w+\}', r'{\1}', path).rstrip('/') or '/'


routes = {(m.upper(), norm('/api' + p)) for m, p in re.findall(r'api\.Map(Get|Post|Put|Patch|Delete)\("([^"]*)"', endpoints)}
routes |= {(m.upper(), norm('/api' + p)) for p, m in re.findall(r'api\.MapMethods\("([^"]*)", new\[\] \{ "(\w+)" \}', endpoints)}
described = {(m.upper(), norm(p)) for m, p in re.findall(r'new\("(get|post|put|patch|delete)", "([^"]+)"', openapi)}
undocumented = sorted(f'{m} {p}' for m, p in routes - described)
if undocumented:
    print('OpenAPI document (Api/OpenApiDoc.cs) is missing these routes:', ', '.join(undocumented))
    sys.exit(1)
print(f'OK: all {len(routes)} API routes are described in the OpenAPI document.')
