#!/usr/bin/env python3
"""Allowlist + credential-pattern review aid. Does not claim exhaustive security proof."""
from pathlib import Path
import hashlib,json,re,subprocess,argparse
root=Path(__file__).resolve().parents[1]
parser=argparse.ArgumentParser();parser.add_argument('--staged',action='store_true');args=parser.parse_args()
roots=['.github','src','tests','shared-source','scripts','packaging','build-metadata']
files=set()
for folder in roots:
    for path in (root/folder).rglob('*'):
        if path.is_file() and not any(p in {'bin','obj','__pycache__'} for p in path.relative_to(root).parts) and path.name!='windows-baseline.json':files.add(path)
for name in ['README.md','LICENSE','THIRD_PARTY_NOTICES.md','VERSION','global.json','Directory.Build.props','Package.swift','.gitignore']:files.add(root/name)
for name in ['BUILD.md','ENGINE-FEASIBILITY.md','ACCEPTANCE.md']:files.add(root/'docs'/name)
allowed={str(path.relative_to(root)):path for path in files}
issues=[]
if args.staged:
    staged=subprocess.check_output(['git','diff','--cached','--name-only','-z'],cwd=root).decode().split('\0')
    for name in filter(None,staged):
        if name not in allowed:issues.append({'path':name,'reason':'outside public allowlist'})
patterns=[r'gh[pousr]_[A-Za-z0-9]{24,}',r'github_pat_[A-Za-z0-9_]{30,}',r'AKIA[A-Z0-9]{16}',r'-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----']
for name,path in allowed.items():
    if path.is_symlink() or path.suffix.lower() in {'.db','.sqlite','.pem','.key','.p12','.exe','.dmg','.log'} or path.name.startswith('.env'):issues.append({'path':name,'reason':'forbidden runtime/private/binary file'})
    try:contents=path.read_text()
    except UnicodeDecodeError:issues.append({'path':name,'reason':'unexpected binary'});continue
    # This scanner's own literal rule names a forbidden context, but contains no private context data/path.
    if 'AI-CONTEXT' in contents and name != 'scripts/audit-public.py':issues.append({'path':name,'reason':'internal context reference'})
    for pattern in patterns:
        if re.search(pattern,contents):issues.append({'path':name,'reason':'credential/key pattern; requires review'})
out=root/'work/public-audit';out.mkdir(parents=True,exist_ok=True)
report={'source_file_count':len(allowed),'files':{name:hashlib.sha256(path.read_bytes()).hexdigest() for name,path in sorted(allowed.items())},'findings':issues,'scope':'explicit source allowlist and selected credential patterns; runtime, full Windows archive and private context excluded; not exhaustive security proof'}
(out/'manifest.json').write_text(json.dumps(report,indent=2)+'\n');(out/'files.txt').write_text('\n'.join(sorted(allowed))+'\n')
print('PUBLIC_FILES='+str(len(allowed)));print('FINDINGS='+str(len(issues)))
for issue in issues:print(issue['path']+': '+issue['reason'])
raise SystemExit(bool(issues))
