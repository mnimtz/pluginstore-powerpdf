"""Deletes old container versions of ghcr.io/<owner>/<package>, keeping the newest server releases.

Run by .github/workflows/ghcr-prune.yml (manual start only). Environment:
  GH_TOKEN   token with packages:write (the workflow's GITHUB_TOKEN)
  OWNER      package owner (user), PACKAGE  container package name
  KEEP       number of S<major>.<minor>.<patch> releases to keep (default 15)
  DRY_RUN    "true" lists only; anything else deletes

Kept: the KEEP newest S-tagged versions (by version number, not by date), every version tagged
"latest", every version with another named tag (not S*, not sha-*), and ALL child manifests of
each kept OCI index (platform image and attestation). The children are untagged versions of
their own in the Packages API; deleting them would break the kept images.
"""
import base64, json, os, re, sys, urllib.error, urllib.request

TOKEN = os.environ['GH_TOKEN']
OWNER = os.environ.get('OWNER', 'mnimtz')
PACKAGE = os.environ.get('PACKAGE', 'pluginstore-powerpdf')
KEEP = int(os.environ.get('KEEP') or 15)
DRY = os.environ.get('DRY_RUN', 'true').strip().lower() != 'false'
API = 'https://api.github.com/users/%s/packages/container/%s/versions' % (OWNER, PACKAGE)
REPO = '%s/%s' % (OWNER, PACKAGE)
S_TAG = re.compile(r'^S(\d+)\.(\d+)\.(\d+)$')
INDEX_TYPES = 'application/vnd.oci.image.index.v1+json, application/vnd.docker.distribution.manifest.list.v2+json, ' \
              'application/vnd.oci.image.manifest.v1+json, application/vnd.docker.distribution.manifest.v2+json'


def call(url, method='GET', headers=None):
    req = urllib.request.Request(url, method=method, headers=headers or {})
    with urllib.request.urlopen(req, timeout=60) as r:
        return r.status, r.headers, r.read()


def api(url, method='GET'):
    return call(url, method, {'Authorization': 'Bearer ' + TOKEN, 'Accept': 'application/vnd.github+json',
                              'X-GitHub-Api-Version': '2022-11-28'})


def versions():
    out, page = [], 1
    while True:
        _, _, body = api('%s?per_page=100&page=%d' % (API, page))
        batch = json.loads(body)
        out += batch
        if len(batch) < 100:
            return out
        page += 1


def registry_token():
    basic = base64.b64encode(('x:' + TOKEN).encode()).decode()
    _, _, body = call('https://ghcr.io/token?service=ghcr.io&scope=repository:%s:pull' % REPO,
                      headers={'Authorization': 'Basic ' + basic})
    return json.loads(body)['token']


def children(digest, rtoken):
    _, h, body = call('https://ghcr.io/v2/%s/manifests/%s' % (REPO, digest),
                      headers={'Authorization': 'Bearer ' + rtoken, 'Accept': INDEX_TYPES})
    doc = json.loads(body)
    return [m['digest'] for m in doc.get('manifests', [])]


def main():
    all_v = versions()
    by_digest = {v['name']: v for v in all_v}
    tags = lambda v: v.get('metadata', {}).get('container', {}).get('tags', [])
    released = []
    keep = set()
    other_named = []
    for v in all_v:
        for t in tags(v):
            m = S_TAG.match(t)
            if m:
                released.append((tuple(map(int, m.groups())), t, v['name']))
            elif t == 'latest':
                keep.add(v['name'])
            elif not t.startswith('sha-'):
                keep.add(v['name']); other_named.append(t)
    released.sort(reverse=True)
    kept_releases = []
    for ver, t, d in released:
        if len(kept_releases) >= KEEP:
            break
        if t not in [k[0] for k in kept_releases]:
            kept_releases.append((t, d))
            keep.add(d)
    rtoken = registry_token()
    parents = sorted(keep)
    for d in parents:
        for c in children(d, rtoken):
            if c not in by_digest:
                print('WARNING: child %s of %s is not a package version' % (c, d))
            keep.add(c)
    delete = [v for v in all_v if v['name'] not in keep]
    print('versions now: %d' % len(all_v))
    print('kept releases (%d): %s' % (len(kept_releases), ', '.join(t for t, _ in kept_releases)))
    print('latest: %s' % ', '.join(sorted(t for v in all_v if 'latest' in tags(v) for t in tags(v))))
    if other_named:
        print('also kept for other named tags: %s' % ', '.join(sorted(set(other_named))))
    print('keep: %d (%d tagged parents + their child manifests)' % (len(keep), len(parents)))
    print('delete: %d%s' % (len(delete), ' (dry run, nothing deleted)' if DRY else ''))
    for v in delete[:400]:
        print('  %s %s %s' % ('would delete' if DRY else 'delete', v['name'][:19], ','.join(tags(v)) or '(untagged)'))
    if DRY:
        return 0
    # last safety check: latest stays and every child of it stays
    if not any('latest' in tags(v) for v in all_v if v['name'] in keep):
        print('ERROR: no version tagged latest would remain, stopping.')
        return 1
    done = 0
    for v in delete:
        try:
            api('%s/%d' % (API, v['id']), 'DELETE')
            done += 1
        except urllib.error.HTTPError as e:
            print('ERROR: delete of %s failed with HTTP %d, stopping after %d deletions.' % (v['name'][:19], e.code, done))
            if e.code in (401, 403):
                print('The workflow token may need the Admin role on the package: package settings > '
                      'Manage Actions access > this repository > Admin.')
            return 1
    print('deleted: %d, versions left: %d' % (done, len(all_v) - done))
    return 0


if __name__ == '__main__':
    sys.exit(main())
