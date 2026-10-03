#!/usr/bin/env python3
"""fetch_webview2.py - puts the pinned Microsoft WebView2 SDK into client/third_party/webview2.

The client links the static WebView2 loader (BSD-3-Clause). The 11 MB .lib is
not committed; run this once before building the client:

    python tools/fetch_webview2.py
"""
import io
import os
import urllib.request
import zipfile

VERSION = '1.0.4258.31'
URL = f'https://www.nuget.org/api/v2/package/Microsoft.Web.WebView2/{VERSION}'
HERE = os.path.dirname(os.path.abspath(__file__))
DEST = os.path.normpath(os.path.join(HERE, '..', 'client', 'third_party', 'webview2'))

FILES = {
    'build/native/include/WebView2.h': 'include/WebView2.h',
    'build/native/include/WebView2EnvironmentOptions.h': 'include/WebView2EnvironmentOptions.h',
    'build/native/x64/WebView2LoaderStatic.lib': 'x64/WebView2LoaderStatic.lib',
    'LICENSE.txt': 'LICENSE.txt',
}

def main():
    lib = os.path.join(DEST, 'x64', 'WebView2LoaderStatic.lib')
    if os.path.exists(lib):
        print('already present:', lib)
        return
    print('downloading', URL)
    data = urllib.request.urlopen(URL, timeout=300).read()
    with zipfile.ZipFile(io.BytesIO(data)) as z:
        for src, dst in FILES.items():
            target = os.path.join(DEST, dst)
            os.makedirs(os.path.dirname(target), exist_ok=True)
            with open(target, 'wb') as f:
                f.write(z.read(src))
            print('  ', dst)

if __name__ == '__main__':
    main()
