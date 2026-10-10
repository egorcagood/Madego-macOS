#!/usr/bin/env python3
"""Materialize only the source closure required by the Mac projects. Frozen baseline is read-only."""
from pathlib import Path
import xml.etree.ElementTree as ET
import glob,hashlib,json,shutil
root=Path(__file__).resolve().parents[1]
baseline=root/'windows-baseline'
if not baseline.is_dir():
    raise SystemExit('Local baseline missing. Public checkout already contains shared-source; do not regenerate from another release.')
selected=set()
for project in [*root.glob('src/*/*.csproj'),*root.glob('tests/Baseline/*/*.csproj')]:
    tree=ET.fromstring(project.read_text())
    for item in tree.iter():
        if item.tag not in ('Compile','EmbeddedResource'):continue
        pattern=item.get('Include','').replace('\\','/')
        for filename in glob.glob(str(project.parent/pattern)):
            path=Path(filename).resolve()
            if path.is_file() and path.is_relative_to(baseline):selected.add(path.relative_to(baseline))
            elif path.is_file() and path.is_relative_to(root/'shared-source'):selected.add(path.relative_to(root/'shared-source'))
for p in (baseline/'src/EgorVpn/Routing/data').iterdir():
    if p.is_file():selected.add(p.relative_to(baseline))
for p in (baseline/'src/Madyar.Link').rglob('*'):
    if p.is_file() and (p.suffix=='.go' or p.name in ('go.mod','go.sum','NOTICE.md') or 'LICENSES' in p.parts):selected.add(p.relative_to(baseline))
for name in ['BouncyCastle-LICENSE.txt','Microsoft-Data-Sqlite-LICENSE.txt','SQLitePCLRaw-LICENSE.txt']:
    selected.add(Path('src/EgorVpn/assets/licenses')/name)
selected.add(Path('src/EgorVpn/Services/SubscriptionService.cs'))  # unchanged source of extracted portable parsing methods
manifest={}
for rel in sorted(selected):
    src=baseline/rel;dst=root/'shared-source'/rel
    if not src.is_file():raise SystemExit('Required source/notices missing: '+str(rel))
    dst.parent.mkdir(parents=True,exist_ok=True);shutil.copy2(src,dst)
    manifest[str(rel)]=hashlib.sha256(src.read_bytes()).hexdigest()
(root/'build-metadata/shared-source.json').write_text(json.dumps({'baseline_version':'0.19.2','files':manifest,'baseline_modified':False},indent=2)+'\n')
for project in [*root.glob('src/*/*.csproj'),*root.glob('tests/Baseline/*/*.csproj')]:
    project.write_text(project.read_text().replace('windows-baseline/','shared-source/'))
print('SHARED_SOURCE_FILES='+str(len(manifest)))
