#!/usr/bin/env python3
"""sign.py - optional Authenticode signing of the client build outputs.

OFF unless configured: without a configuration it prints one info line and changes nothing,
so the build stays exactly as before. When configured, every given file is signed (SHA-256,
RFC 3161 timestamp) and checked afterwards; any failure ends the build with an error.

Configuration: a JSON file OUTSIDE the repository, found at
    %ADDONSTORE_SIGN_CONFIG%                      (full path; when set, the file must exist)
    %LOCALAPPDATA%\\AddonStoreBuild\\signing.json  (default; missing = signing is off)
It names a method and where its tools are; it never holds keys, passwords, tokens or
certificate files (a value that looks like one is refused). See docs/code-signing.md.

  {"method": "trusted-signing",            Azure Trusted Signing (Artifact Signing), signtool + dlib
   "dlib": "C:\\\\...\\\\bin\\\\x64\\\\Azure.CodeSigning.Dlib.dll",
   "metadata": "C:\\\\...\\\\metadata.json",     Endpoint, CodeSigningAccountName, CertificateProfileName
   "timestampUrl": "http://timestamp.acs.microsoft.com"}

  {"method": "script",                     an external signing script, e.g. the one IT provides:
   "script": "C:\\\\...\\\\Sign-File.ps1"}       called once per file with the file path as its argument

  {"method": "certstore",                  a certificate in the Windows certificate store
   "thumbprint": "<40 hex characters>", "store": "My", "machineStore": false,
   "timestampUrl": "http://timestamp.digicert.com"}

Optional for every method: "signtool" (full path), "description" and "descriptionUrl"
(signtool /d and /du), "expectedSubject" (text the signer's subject must contain, e.g.
"Tungsten Automation"; required for Trusted Signing, because signtool silently falls back to a
certificate from the store when it cannot load the dlib), "expectedIssuer" (text the issuer
must contain), "requireTimestamp" (default true), "powershell" (full path of the
PowerShell that runs the script and the check, default Windows PowerShell 5.1) and
"allowUntrustedRoot" (default false; true ONLY for a local test with a self-signed certificate).

Authentication for Trusted Signing is the Azure identity of the machine or user
(DefaultAzureCredential: az login, Visual Studio, environment, managed identity).

Usage:
  python client\\signing\\sign.py <file> [<file> ...]      sign (nothing when not configured)
  python client\\signing\\sign.py --check <file> [...]     configured: fail unless each file is signed
  python client\\signing\\sign.py --status                 show what is configured
Exit codes: 0 done or not configured, 1 signing or check failed, 2 configuration wrong.
"""
import glob
import json
import os
import re
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.normcase(os.path.normpath(os.path.join(HERE, '..', '..')))
ENV_CONFIG = 'ADDONSTORE_SIGN_CONFIG'
METHODS = ('trusted-signing', 'script', 'certstore')
KNOWN_KEYS = {'method', 'timestampUrl', 'dlib', 'metadata', 'script', 'thumbprint', 'store', 'machineStore',
              'signtool', 'description', 'descriptionUrl', 'expectedSubject', 'requireTimestamp',
              'powershell', 'allowUntrustedRoot', 'timeoutSeconds', 'comment', 'expectedIssuer',
              'tenantId', 'credentialTarget'}
# names that point at secrets: such values never belong in this file
SECRET_HINT = re.compile(r'(pass(word|phrase)?|secret|token|pfx|p12|private|key(?!word))', re.I)
DEFAULT_TS_TRUSTED = 'http://timestamp.acs.microsoft.com'


class ConfigError(Exception):
    pass


class SignError(Exception):
    pass


def info(msg):
    print('[sign] ' + msg, flush=True)


def config_path():
    """(path, explicit): the configuration file and whether the environment named it."""
    env = os.environ.get(ENV_CONFIG, '').strip()
    if env:
        return env, True
    base = os.environ.get('LOCALAPPDATA', '')
    return (os.path.join(base, 'AddonStoreBuild', 'signing.json') if base else ''), False


def inside_repo(path):
    p = os.path.normcase(os.path.normpath(os.path.abspath(path)))
    return p == REPO or p.startswith(REPO + os.sep)


def load_config():
    """The checked configuration, or None when signing is off."""
    path, explicit = config_path()
    if not path or not os.path.isfile(path):
        if explicit:
            raise ConfigError(f'{ENV_CONFIG} names {path}, but that file does not exist')
        return None
    if inside_repo(path):
        raise ConfigError(f'the signing configuration {path} lies inside the repository; keep it outside '
                          '(it would end up in Git)')
    try:
        with open(path, encoding='utf-8-sig') as f:
            cfg = json.load(f)
    except (OSError, ValueError) as e:
        raise ConfigError(f'{path} is not readable JSON ({e.__class__.__name__})')
    if not isinstance(cfg, dict):
        raise ConfigError(f'{path} must hold one JSON object')
    cfg['_path'] = path
    validate(cfg)
    return cfg


def _file(cfg, key, required=True, suffix=None):
    v = cfg.get(key)
    if not v:
        if required:
            raise ConfigError(f'"{key}" is missing for method "{cfg["method"]}"')
        return None
    if not isinstance(v, str) or not os.path.isabs(v):
        raise ConfigError(f'"{key}" must be a full path')
    if not os.path.isfile(v):
        raise ConfigError(f'"{key}": {v} does not exist')
    if suffix and not v.lower().endswith(suffix):
        raise ConfigError(f'"{key}" must name a {suffix} file')
    return v


def _url(cfg, key, default=None):
    v = cfg.get(key) or default
    if not v:
        return None
    if not isinstance(v, str) or not re.match(r'^https?://[A-Za-z0-9.-]+(:[0-9]+)?(/[^\s"]*)?$', v):
        raise ConfigError(f'"{key}" must be an http:// or https:// address')
    return v


def validate(cfg):
    for k, v in cfg.items():
        if k.startswith('_'):
            continue
        if k not in KNOWN_KEYS:
            if SECRET_HINT.search(k):
                raise ConfigError(f'"{k}": keys, passwords and tokens never belong in the signing configuration')
            raise ConfigError(f'unknown setting "{k}"')
        if isinstance(v, str) and re.search(r'\.(pfx|p12|pvk|pem|key)$', v, re.I):
            raise ConfigError(f'"{k}": certificate or key files are not supported; use the certificate store, '
                              'Trusted Signing or a signing script')
    method = cfg.get('method')
    if method not in METHODS:
        raise ConfigError(f'"method" must be one of {", ".join(METHODS)}')
    if method == 'trusted-signing':
        cfg['dlib'] = _file(cfg, 'dlib', suffix='.dll')
        with open(cfg['dlib'], 'rb') as f:
            if f.read(2) != b'MZ':
                # signtool ignores a dlib it cannot load and picks a certificate from the store instead
                raise ConfigError('"dlib" is not a DLL (Azure.CodeSigning.Dlib.dll of the Trusted Signing client)')
        if not cfg.get('expectedSubject'):
            # the guard against that fallback: only a signature by the expected publisher counts
            raise ConfigError('"expectedSubject" is required for Trusted Signing (the publisher name of the '
                              'certificate profile, e.g. "Tungsten Automation")')
        cfg['metadata'] = _file(cfg, 'metadata', suffix='.json')
        try:
            with open(cfg['metadata'], encoding='utf-8-sig') as f:
                meta = json.load(f)
        except (OSError, ValueError) as e:
            raise ConfigError(f'metadata file is not readable JSON ({e.__class__.__name__})')
        if not isinstance(meta, dict):
            raise ConfigError('metadata file must hold one JSON object')
        for k in ('Endpoint', 'CodeSigningAccountName', 'CertificateProfileName'):
            if not isinstance(meta.get(k), str) or not meta.get(k).strip():
                raise ConfigError(f'metadata file lacks "{k}"')
        if not meta['Endpoint'].lower().startswith('https://'):
            raise ConfigError('metadata "Endpoint" must be an https:// address')
        for k in meta:
            if SECRET_HINT.search(k) and k not in ('ExcludeCredentials',):
                raise ConfigError(f'metadata "{k}": credentials never belong in the metadata file '
                                  '(Trusted Signing signs in with the Azure identity)')
        cfg['_meta'] = {k: meta[k] for k in ('Endpoint', 'CodeSigningAccountName', 'CertificateProfileName')}
        cfg['timestampUrl'] = _url(cfg, 'timestampUrl', DEFAULT_TS_TRUSTED)
        # C2.0.2: service principal (Tungsten's Products Operations flow): both keys or neither
        if bool(cfg.get('credentialTarget')) != bool(cfg.get('tenantId')):
            raise ConfigError('"credentialTarget" and "tenantId" go together (service principal), or leave both out for az login')
        if cfg.get('tenantId') and not GUID.match(str(cfg['tenantId']).strip()):
            raise ConfigError('"tenantId" must be the directory (tenant) id, a GUID')
        if cfg.get('credentialTarget') and not re.match(r'^[A-Za-z0-9._/:-]{3,200}$', str(cfg['credentialTarget'])):
            raise ConfigError('"credentialTarget" is the name of a generic Windows credential, e.g. AddonStoreBuild/TrustedSigning')
    elif method == 'certstore':
        tp = str(cfg.get('thumbprint', '')).replace(' ', '').upper()
        if not re.fullmatch(r'[0-9A-F]{40}', tp):
            raise ConfigError('"thumbprint" must be the 40 hex characters of the certificate (SHA-1 thumbprint)')
        cfg['thumbprint'] = tp
        store = cfg.get('store', 'My')
        if not isinstance(store, str) or not re.fullmatch(r'[A-Za-z]{1,32}', store):
            raise ConfigError('"store" must be a store name such as My')
        cfg['store'] = store
        cfg['timestampUrl'] = _url(cfg, 'timestampUrl')
        if not cfg['timestampUrl'] and cfg.get('requireTimestamp', True):
            raise ConfigError('"timestampUrl" is missing (RFC 3161 timestamp server of the certificate vendor)')
    else:
        cfg['script'] = _file(cfg, 'script', suffix='.ps1')
        cfg['timestampUrl'] = None   # the script decides; the check below still asks for a timestamp
    if cfg.get('powershell'):
        cfg['powershell'] = _file(cfg, 'powershell', suffix='.exe')
    if cfg.get('signtool'):
        cfg['signtool'] = _file(cfg, 'signtool', suffix='signtool.exe')
    for k in ('description', 'expectedSubject', 'expectedIssuer'):
        if k in cfg and (not isinstance(cfg[k], str) or len(cfg[k]) > 200 or '"' in cfg[k]):
            raise ConfigError(f'"{k}" must be a short text without quotes')
    if 'descriptionUrl' in cfg:
        cfg['descriptionUrl'] = _url(cfg, 'descriptionUrl')


def windows_powershell():
    return os.path.join(os.environ.get('SystemRoot', r'C:\Windows'), 'System32', 'WindowsPowerShell', 'v1.0',
                        'powershell.exe')


def find_signtool(cfg):
    if cfg.get('signtool'):
        return cfg['signtool']
    kits = os.path.join(os.environ.get('ProgramFiles(x86)', r'C:\Program Files (x86)'), 'Windows Kits', '10')

    def ver(p):
        m = re.search(r'\\bin\\(\d+)\.(\d+)\.(\d+)\.(\d+)\\', p)
        return tuple(int(x) for x in m.groups()) if m else (0,)
    found = sorted(glob.glob(os.path.join(kits, 'bin', '10.*', 'x64', 'signtool.exe')), key=ver, reverse=True)
    if found:
        if cfg['method'] == 'trusted-signing' and ver(found[0]) < (10, 0, 22621, 0):
            info(f'warning: {found[0]} is older than 10.0.22621; Trusted Signing needs a newer Windows SDK signtool')
        return found[0]
    ack = os.path.join(kits, 'App Certification Kit', 'signtool.exe')
    if os.path.isfile(ack):
        return ack
    raise ConfigError('signtool.exe not found: install the Windows SDK (Signing Tools) or set "signtool"')


def run(args, timeout, env=None):
    """Runs a tool; its output is shown (signtool and the scripts print no secrets)."""
    try:
        r = subprocess.run(args, capture_output=True, text=True, timeout=timeout, env=env)
    except (OSError, subprocess.TimeoutExpired) as e:
        raise SignError(f'{os.path.basename(args[0])} could not run ({e.__class__.__name__})')
    out = (r.stdout or '') + (r.stderr or '')
    for line in out.splitlines()[-40:]:
        if line.strip():
            print('       ' + line, flush=True)
    return r.returncode


def signtool_args(cfg, signtool, path):
    a = [signtool, 'sign', '/fd', 'SHA256']
    if cfg['method'] == 'trusted-signing':
        a += ['/dlib', cfg['dlib'], '/dmdf', cfg['metadata']]
    else:
        a += ['/sha1', cfg['thumbprint'], '/s', cfg['store']]
        if cfg.get('machineStore'):
            a.append('/sm')
    if cfg.get('timestampUrl'):
        a += ['/tr', cfg['timestampUrl'], '/td', 'SHA256']
    if cfg.get('description'):
        a += ['/d', cfg['description']]
    if cfg.get('descriptionUrl'):
        a += ['/du', cfg['descriptionUrl']]
    return a + [path]


def signature_of(cfg, path):
    """Get-AuthenticodeSignature of path as a dict (the path goes in through the environment)."""
    ps = cfg.get('powershell') or windows_powershell()
    script = ("$ErrorActionPreference='Stop'; $s=Get-AuthenticodeSignature -LiteralPath $env:ADDONSTORE_SIGN_FILE; "
              "[pscustomobject]@{Status=[string]$s.Status; Message=[string]$s.StatusMessage; "
              "Subject=$(if($s.SignerCertificate){$s.SignerCertificate.Subject}else{''}); "
              "Thumbprint=$(if($s.SignerCertificate){$s.SignerCertificate.Thumbprint}else{''}); "
              "Issuer=$(if($s.SignerCertificate){$s.SignerCertificate.Issuer}else{''}); "
              "Timestamp=[bool]$s.TimeStamperCertificate} | ConvertTo-Json -Compress")
    env = dict(os.environ, ADDONSTORE_SIGN_FILE=path)
    try:
        r = subprocess.run([ps, '-NoProfile', '-NonInteractive', '-Command', script], capture_output=True,
                           text=True, timeout=120, env=env)
        return json.loads(r.stdout.strip().splitlines()[-1])
    except (OSError, subprocess.TimeoutExpired, ValueError, IndexError):
        raise SignError(f'the signature of {os.path.basename(path)} could not be read')


def check(cfg, path):
    """Raises SignError unless path carries a complete signature as configured."""
    s = signature_of(cfg, path)
    name = os.path.basename(path)
    if not s.get('Subject') or s.get('Status') in ('NotSigned', 'HashMismatch', 'NotSupportedFileFormat', 'Incompatible'):
        raise SignError(f'{name}: no valid signature ({s.get("Status")}: {s.get("Message")})')
    if s.get('Status') != 'Valid':
        if not cfg.get('allowUntrustedRoot'):
            raise SignError(f'{name}: signature not valid ({s.get("Status")}: {s.get("Message")})')
        info(f'{name}: signature present, root not trusted (allowed for this test configuration)')
    if cfg.get('requireTimestamp', True) and not s.get('Timestamp'):
        raise SignError(f'{name}: signed, but without a timestamp')
    if cfg['method'] == 'certstore' and s.get('Thumbprint', '').upper() != cfg['thumbprint']:
        raise SignError(f'{name}: signed with another certificate than the configured thumbprint')
    if cfg.get('expectedSubject') and cfg['expectedSubject'] not in s.get('Subject', ''):
        raise SignError(f'{name}: signer "{s.get("Subject")}" does not contain "{cfg["expectedSubject"]}"')
    if cfg.get('expectedIssuer') and cfg['expectedIssuer'] not in s.get('Issuer', ''):
        raise SignError(f'{name}: issuer "{s.get("Issuer")}" does not contain "{cfg["expectedIssuer"]}"')
    info(f'{name}: signed by {s.get("Subject")}' + (', timestamped' if s.get('Timestamp') else ''))


def read_credential(target):
    """(user name, secret) of a generic Windows credential (Credential Manager), or None. Nothing is printed."""
    import ctypes
    from ctypes import wintypes

    class CREDENTIAL(ctypes.Structure):
        _fields_ = [('Flags', wintypes.DWORD), ('Type', wintypes.DWORD), ('TargetName', wintypes.LPWSTR),
                    ('Comment', wintypes.LPWSTR), ('LastWritten', wintypes.FILETIME), ('CredentialBlobSize', wintypes.DWORD),
                    ('CredentialBlob', ctypes.POINTER(ctypes.c_ubyte)), ('Persist', wintypes.DWORD),
                    ('AttributeCount', wintypes.DWORD), ('Attributes', ctypes.c_void_p), ('TargetAlias', wintypes.LPWSTR),
                    ('UserName', wintypes.LPWSTR)]
    advapi = ctypes.WinDLL('advapi32', use_last_error=True)
    advapi.CredReadW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD, ctypes.POINTER(ctypes.POINTER(CREDENTIAL))]
    advapi.CredReadW.restype = wintypes.BOOL
    advapi.CredFree.argtypes = [ctypes.c_void_p]
    pcred = ctypes.POINTER(CREDENTIAL)()
    if not advapi.CredReadW(target, 1, 0, ctypes.byref(pcred)):   # 1 = CRED_TYPE_GENERIC
        return None
    try:
        c = pcred.contents
        blob = ctypes.string_at(c.CredentialBlob, c.CredentialBlobSize) if c.CredentialBlobSize else b''
        try:
            secret = blob.decode('utf-16-le')   # cmdkey and the Credential Manager store UTF-16
        except UnicodeDecodeError:
            secret = blob.decode('utf-8', 'replace')
        return (c.UserName or ''), secret
    finally:
        advapi.CredFree(pcred)


GUID = re.compile(r'^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$')


def signing_env(cfg):
    """C2.0.2: environment for signtool when Trusted Signing uses a service principal: client id (user name) and
    secret come from the Windows Credential Manager entry "credentialTarget", the tenant from "tenantId".
    They exist only in signtool's environment, never in a file, the console or the log."""
    target = cfg.get('credentialTarget')
    if cfg['method'] != 'trusted-signing' or not target:
        return None
    cred = read_credential(target)
    if cred is None:
        raise SignError(f'no Windows credential "{target}" (create it once: cmdkey /generic:{target} /user:<client id> /pass)')
    client_id, secret = cred
    if not GUID.match(client_id.strip()) or not secret:
        raise SignError(f'Windows credential "{target}" needs the client id as user name and the client secret as password')
    env = dict(os.environ)
    env['AZURE_CLIENT_ID'] = client_id.strip()
    env['AZURE_TENANT_ID'] = cfg['tenantId']
    env['AZURE_CLIENT_SECRET'] = secret
    return env


def sign(cfg, path):
    timeout = int(cfg.get('timeoutSeconds', 300))
    if cfg['method'] == 'script':
        ps = cfg.get('powershell') or windows_powershell()
        rc = run([ps, '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', cfg['script'], path],
                 timeout)
        if rc != 0:
            raise SignError(f'signing script exit code {rc} for {os.path.basename(path)}')
    else:
        rc = run(signtool_args(cfg, find_signtool(cfg), path), timeout, env=signing_env(cfg))
        if rc != 0:
            raise SignError(f'signtool exit code {rc} for {os.path.basename(path)}')
    check(cfg, path)


def describe(cfg):
    m = cfg['method']
    if m == 'trusted-signing':
        meta = cfg['_meta']
        return (f'Trusted Signing, account {meta["CodeSigningAccountName"]}, profile {meta["CertificateProfileName"]}, '
                f'timestamp {cfg["timestampUrl"]}')
    if m == 'certstore':
        where = 'LocalMachine' if cfg.get('machineStore') else 'CurrentUser'
        return f'certificate {cfg["thumbprint"]} in {where}\\{cfg["store"]}, timestamp {cfg.get("timestampUrl")}'
    return f'signing script {cfg["script"]}'


def main(argv):
    mode = 'sign'
    if argv and argv[0] in ('--check', '--status'):
        mode = argv.pop(0)[2:]
    try:
        cfg = load_config()
    except ConfigError as e:
        info(f'ERROR: signing configuration: {e}')
        return 2
    if cfg is None:
        if mode != 'check':
            path, _ = config_path()
            info(f'code signing not configured ({path or ENV_CONFIG} not found), files stay unsigned')
        return 0
    if mode == 'status':
        info(f'configured in {cfg["_path"]}: {describe(cfg)}')
        return 0
    files = [os.path.abspath(f) for f in argv]
    if not files:
        info('ERROR: no files given')
        return 2
    try:
        for f in files:
            if not os.path.isfile(f):
                raise SignError(f'{f} does not exist')
        if mode == 'check':
            for f in files:
                check(cfg, f)
            return 0
        info(f'signing {len(files)} file(s): {describe(cfg)}')
        for f in files:
            sign(cfg, f)
        return 0
    except (SignError, ConfigError) as e:
        info(f'ERROR: {e}')
        return 1


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
