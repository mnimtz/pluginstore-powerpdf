#!/usr/bin/env python3
"""signing_tests.py - tests of the optional code signing step (client/signing/sign.py).

Run by client\\tests\\run_tests.cmd after the C++ tests (it signs nothing and needs no
certificate): configuration off, refused configurations, the signtool command lines, and
that a script which "succeeds" without signing still fails the check.
Usage: python client\\tests\\signing_tests.py [unsigned PE file]
"""
import json
import os
import shutil
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
SIGN = os.path.normpath(os.path.join(HERE, '..', 'signing', 'sign.py'))
sys.path.insert(0, os.path.dirname(SIGN))
import sign as signmod   # noqa: E402

failed = 0
checks = 0


def check(cond, what):
    global failed, checks
    checks += 1
    if not cond:
        failed += 1
        print('FAIL', what)


def run(args, env_extra, cwd=None):
    env = {k: v for k, v in os.environ.items() if k != signmod.ENV_CONFIG}
    env.update(env_extra)
    r = subprocess.run([sys.executable, SIGN] + args, capture_output=True, text=True, env=env, cwd=cwd)
    return r.returncode, r.stdout + r.stderr


def write(path, obj):
    with open(path, 'w', encoding='utf-8') as f:
        json.dump(obj, f) if not isinstance(obj, str) else f.write(obj)
    return path


def main():
    tmp = tempfile.mkdtemp(prefix='signtest-')
    try:
        empty = os.path.join(tmp, 'empty')
        os.makedirs(empty)
        target = os.path.join(tmp, 'file.bin')
        write(target, 'x')

        # off: no configuration -> one info line, exit 0, nothing else
        rc, out = run([target], {'LOCALAPPDATA': empty})
        check(rc == 0 and 'not configured' in out and out.count('\n') == 1, 'unconfigured build: one info line, rc 0')
        rc, out = run(['--check', target], {'LOCALAPPDATA': empty})
        check(rc == 0 and out == '', 'unconfigured --check is silent')

        # named by the environment but missing: configured and broken -> error
        rc, out = run([target], {signmod.ENV_CONFIG: os.path.join(tmp, 'nope.json')})
        check(rc == 2 and 'does not exist' in out, 'missing configured file fails')

        # never inside the repository (it would end up in Git); client/tests/out is ignored by Git
        outdir = os.path.join(HERE, 'out')
        os.makedirs(outdir, exist_ok=True)
        inrepo = write(os.path.join(outdir, 'signing.json'), {'method': 'script', 'script': SIGN})
        rc, out = run([target], {signmod.ENV_CONFIG: inrepo})
        check(rc == 2 and 'inside the repository' in out, 'configuration inside the repo refused')
        os.remove(inrepo)

        def cfg(obj):
            return {signmod.ENV_CONFIG: write(os.path.join(tmp, 'signing.json'), obj)}

        # secrets and key files are refused
        rc, out = run([target], cfg({'method': 'certstore', 'thumbprint': 'A' * 40, 'password': 'x'}))
        check(rc == 2 and 'never belong' in out and 'x' not in out.split('never belong')[1][:40], 'password key refused, value not shown')
        rc, out = run([target], cfg({'method': 'certstore', 'thumbprint': 'A' * 40, 'signtool': 'C:\\k\\cert.pfx'}))
        check(rc == 2 and 'not supported' in out, 'pfx path refused')
        rc, out = run([target], cfg({'method': 'magic'}))
        check(rc == 2, 'unknown method refused')
        rc, out = run([target], cfg({'method': 'certstore', 'thumbprint': '12AB', 'timestampUrl': 'http://ts.example'}))
        check(rc == 2 and 'thumbprint' in out, 'short thumbprint refused')
        rc, out = run([target], cfg({'method': 'certstore', 'thumbprint': 'A' * 40}))
        check(rc == 2 and 'timestampUrl' in out, 'certstore without timestamp refused')

        # Trusted Signing: dlib and metadata must exist; metadata needs its three fields
        dlib = write(os.path.join(tmp, 'Azure.CodeSigning.Dlib.dll'), 'not a dll')
        meta = os.path.join(tmp, 'metadata.json')
        # signtool silently ignores a dlib it cannot load and signs with a store certificate instead
        rc, out = run([target], cfg({'method': 'trusted-signing', 'dlib': dlib, 'metadata': dlib}))
        check(rc == 2 and 'is not a DLL' in out, 'trusted-signing with a non-DLL dlib refused')
        write(dlib, 'MZ fake PE')
        rc, out = run([target], cfg({'method': 'trusted-signing', 'dlib': dlib, 'expectedSubject': 'Tungsten'}))
        check(rc == 2 and '"metadata" is missing' in out, 'trusted-signing without metadata refused')
        write(meta, {'Endpoint': 'https://weu.codesigning.azure.net', 'CodeSigningAccountName': 'acct',
                     'CertificateProfileName': 'prof'})
        rc, out = run([target], cfg({'method': 'trusted-signing', 'dlib': dlib, 'metadata': meta}))
        check(rc == 2 and 'expectedSubject' in out, 'trusted-signing without expectedSubject refused')
        ts = {'method': 'trusted-signing', 'dlib': dlib, 'metadata': meta, 'expectedSubject': 'Tungsten Automation'}
        write(meta, {'Endpoint': 'https://weu.codesigning.azure.net', 'CodeSigningAccountName': 'acct'})
        rc, out = run([target], cfg(ts))
        check(rc == 2 and 'CertificateProfileName' in out, 'metadata without profile refused')
        write(meta, {'Endpoint': 'https://weu.codesigning.azure.net', 'CodeSigningAccountName': 'acct',
                     'CertificateProfileName': 'prof', 'ClientSecret': 's3cr3t'})
        rc, out = run([target], cfg(ts))
        check(rc == 2 and 's3cr3t' not in out, 'metadata with a secret refused, secret not shown')
        write(meta, {'Endpoint': 'https://weu.codesigning.azure.net', 'CodeSigningAccountName': 'acct',
                     'CertificateProfileName': 'prof'})
        good = dict(ts, description='Add-on Store')
        rc, out = run(['--status'], cfg(good))
        check(rc == 0 and 'acct' in out and 'prof' in out and 'timestamp.acs.microsoft.com' in out, 'trusted-signing status')

        # C2.0.2: service principal from the Windows Credential Manager (credentialTarget + tenantId)
        tenant = '11111111-2222-3333-4444-555555555555'
        rc, out = run(['--status'], cfg(dict(good, credentialTarget='AddonStoreBuildTest/x')))
        check(rc == 2 and 'go together' in out, 'credentialTarget without tenantId refused')
        rc, out = run(['--status'], cfg(dict(good, tenantId='contoso.com', credentialTarget='AddonStoreBuildTest/x')))
        check(rc == 2 and 'GUID' in out, 'tenantId must be a GUID')
        rc, out = run(['--status'], cfg(dict(good, tenantId=tenant, credentialTarget='bad target with spaces')))
        check(rc == 2 and 'credentialTarget' in out, 'credentialTarget name checked')
        rc, out = run(['--status'], cfg(dict(good, tenantId=tenant, credentialTarget='AddonStoreBuildTest/x')))
        check(rc == 0, 'service principal configuration accepted')
        target_name = 'AddonStoreBuildTest/' + os.urandom(6).hex()
        c2 = dict(good, tenantId=tenant, credentialTarget=target_name)
        signmod.validate(c2)
        try:
            signmod.signing_env(c2)
            check(False, 'missing Windows credential must fail')
        except signmod.SignError as e:
            check('cmdkey' in str(e), 'missing Windows credential: clear message with the cmdkey hint')
        client_id = '99999999-8888-7777-6666-555555555555'
        fake_secret = 'TEST-ONLY-' + os.urandom(8).hex()
        made = subprocess.run(['cmdkey', '/generic:' + target_name, '/user:' + client_id, '/pass:' + fake_secret],
                              capture_output=True, text=True).returncode == 0
        try:
            if made:
                env = signmod.signing_env(c2)
                check(env['AZURE_CLIENT_ID'] == client_id and env['AZURE_TENANT_ID'] == tenant and env['AZURE_CLIENT_SECRET'] == fake_secret,
                      'service principal read from the Credential Manager into signtool\'s environment only')
                check('AZURE_CLIENT_SECRET' not in os.environ, 'the secret does not leak into this process environment')
            else:
                print('note: cmdkey not available, Credential Manager read skipped')
        finally:
            subprocess.run(['cmdkey', '/delete:' + target_name], capture_output=True)

        # the signtool command lines
        c = dict(good)
        signmod.validate(c)
        a = signmod.signtool_args(c, 'signtool.exe', 'F.zxt')
        check(a[:4] == ['signtool.exe', 'sign', '/fd', 'SHA256'] and a[-1] == 'F.zxt', 'sign /fd SHA256 first, file last')
        check(a[a.index('/dlib') + 1] == dlib and a[a.index('/dmdf') + 1] == meta, 'dlib and metadata passed')
        check(a[a.index('/tr') + 1] == 'http://timestamp.acs.microsoft.com' and a[a.index('/td') + 1] == 'SHA256',
              'RFC 3161 timestamp with SHA-256')
        check(a[a.index('/d') + 1] == 'Add-on Store', 'description passed')
        c = {'method': 'certstore', 'thumbprint': 'ab' * 20, 'timestampUrl': 'http://timestamp.digicert.com'}
        signmod.validate(c)
        a = signmod.signtool_args(c, 'signtool.exe', 'F.exe')
        check(a[a.index('/sha1') + 1] == 'AB' * 20 and a[a.index('/s') + 1] == 'My' and '/sm' not in a, 'certstore: user store My')
        c['machineStore'] = True
        check('/sm' in signmod.signtool_args(c, 'signtool.exe', 'F.exe'), 'certstore: machine store')

        # script method: a failing script fails the build; a "successful" one that signs nothing fails the check
        pe = sys.argv[1] if len(sys.argv) > 1 else ''
        bad = write(os.path.join(tmp, 'bad.ps1'), 'exit 3\r\n')
        lazy = write(os.path.join(tmp, 'lazy.ps1'), 'param($file)\r\nif (-not (Test-Path -LiteralPath $file)) { exit 5 }\r\nexit 0\r\n')
        rc, out = run([target], cfg({'method': 'script', 'script': bad}))
        check(rc == 1 and 'exit code 3' in out, 'failing script fails the build')
        if pe and os.path.isfile(pe):
            copy = os.path.join(tmp, 'unsigned.exe')
            shutil.copyfile(pe, copy)
            rc, out = run([copy], cfg({'method': 'script', 'script': lazy}))
            check(rc == 1 and 'no valid signature' in out, 'script without a signature fails the check')
            rc, out = run(['--check', copy], cfg({'method': 'script', 'script': lazy}))
            check(rc == 1, '--check of an unsigned file fails when signing is configured')
        else:
            print('note: no unsigned PE given, script check skipped')
    finally:
        shutil.rmtree(tmp, ignore_errors=True)
    print(f'{checks} checks, {failed} failed')
    return 1 if failed else 0


if __name__ == '__main__':
    sys.exit(main())
