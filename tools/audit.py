#!/usr/bin/env python3
"""Public-source and compiled-package allowlist; never reads runtime credentials."""
import argparse
from pathlib import Path
import re
import subprocess
import zipfile

ROOT = Path(__file__).resolve().parent.parent
PIN = '2d5a0d8077bb33af080e949031da33d84b80638d'
NAME = 'BTCPayServer.Plugins.WhollyCrypto'
parser = argparse.ArgumentParser()
parser.add_argument('--package', type=Path)
parser.add_argument('--expected-dll', type=Path)
args = parser.parse_args()

def safe_content(name, content):
    blocked = [rb'github_pat_[A-Za-z0-9_]{20,}', rb'ghp_[A-Za-z0-9]{20,}',
               rb'-----BEGIN [^-]*PRIVATE KEY-----', b'/ro' + b'ot/',
               b'whollycrypto-' + b'master']
    if any(re.search(pattern, content) for pattern in blocked):
        raise SystemExit('Private material or local path detected in ' + name)

allowed = {'.gitignore', '.gitattributes', '.gitmodules', 'AGENTS.md', 'README.md',
           'LICENSE', 'SECURITY.md', 'CHANGELOG.md'}
directories = {'src', 'tests', 'docs', 'tools', 'ci'}
entries = subprocess.check_output(['git', 'ls-files', '-s', '-z'], cwd=ROOT).decode().split('\0')
count = 0
for entry in filter(None, entries):
    meta, name = entry.split('\t', 1)
    mode, sha, stage = meta.split()
    if mode == '160000':
        if name != 'submodules/btcpayserver' or sha != PIN:
            raise SystemExit('Unexpected source submodule')
        continue
    path = ROOT / name
    if (mode not in {'100644', '100755'} or stage != '0' or path.is_symlink()
        or not path.is_file() or (name not in allowed and Path(name).parts[0] not in directories)
        or set(Path(name).parts) & {'bin', 'obj', 'dist', '.build', '__pycache__'}
        or path.suffix in {'.key', '.pem', '.pfx', '.env', '.log', '.zip', '.dll', '.pdb', '.pyc'}):
        raise SystemExit('Unexpected public source file: ' + name)
    safe_content(name, path.read_bytes())
    count += 1
modules = (ROOT / '.gitmodules').read_text()
if 'https://github.com/btcpayserver/btcpayserver.git' not in modules or modules.count('url =') != 1:
    raise SystemExit('Unexpected upstream URL')
if count < 25:
    raise SystemExit('Stage the complete reviewed source before auditing')
print(f'PASS: {count} public source files and pinned public upstream submodule')

if args.package:
    with zipfile.ZipFile(args.package) as package:
        expected = {NAME + '.dll', NAME + '.deps.json', 'LICENSE'}
        if len(package.namelist()) != 3 or set(package.namelist()) != expected or package.testzip():
            raise SystemExit('Package contents differ from the connector-only allowlist')
        for item in package.infolist():
            if item.file_size > 2_000_000:
                raise SystemExit('Unexpected package entry size')
            safe_content(item.filename, package.read(item))
        if args.expected_dll and package.read(NAME + '.dll') != args.expected_dll.read_bytes():
            raise SystemExit('Package does not contain the freshly built connector')
    print('PASS: package contains only connector DLL, dependency manifest and license')
