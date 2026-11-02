"""Check release contents and manifest before distributing to Jellyfin."""
from pathlib import Path
import argparse
import hashlib
import json
import re
import xml.etree.ElementTree as ET
import zipfile

root = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser()
parser.add_argument('--output', type=Path, default=root / 'artifacts')
args = parser.parse_args()
source = root / 'src/Jellyfin.Plugin.FnosSync'
plugin = (source / 'Plugin.cs').read_text(encoding='utf-8')
version = re.search(r'ReleaseVersion = "([^"]+)"', plugin).group(1)
assert f'AssemblyVersion("{version}.0")' in plugin, 'Assembly version mismatch'
assert f'AssemblyInformationalVersion("{version}")' in plugin, 'Informational version mismatch'
assert ET.parse(source / 'Jellyfin.Plugin.FnosSync.csproj').findtext('./PropertyGroup/Version') == version
archive = args.output / f'Jellyfin.Plugin.FnosSync-{version}.zip'
documents = ['LICENSE', 'THIRD_PARTY_NOTICES.md', 'README.md']
with zipfile.ZipFile(archive) as z:
    expected = {'Jellyfin.Plugin.FnosSync.dll', 'meta.json', 'icon.png', *documents}
    assert set(z.namelist()) == expected and len(z.namelist()) == len(expected), 'Unexpected or nested package contents'
    assert z.testzip() is None, 'ZIP corruption'
    dll = z.read('Jellyfin.Plugin.FnosSync.dll')
    assert dll[:2] == b'MZ', 'Invalid DLL header'
    for name in documents:
        assert z.read(name) == (root / name).read_bytes(), f'Missing or stale {name}'
        assert ('Jellyfin.Plugin.FnosSync.' + name).encode() in dll, f'Document not embedded: {name}'
    meta = json.loads(z.read('meta.json'))
    assert meta['imagePath'] == 'icon.png'
    assert z.read('icon.png') == (root / 'assets/icon.png').read_bytes()
    assert z.read('icon.png').startswith(b'\x89PNG\r\n\x1a\n')
    assert b'Jellyfin.Plugin.FnosSync.icon.png' in dll
    assert meta['version'] == version + '.0' and meta['targetAbi'] == '12.4.0.0'
raw = archive.read_bytes()
assert archive.with_suffix('.zip.sha256').read_text().split()[0] == hashlib.sha256(raw).hexdigest()
manifest = args.output / 'manifest.json'
if manifest.exists():
    package = json.loads(manifest.read_text(encoding='utf-8'))[0]
    release = package['versions'][0]
    assert package['guid'] == meta['guid']
    assert (args.output / 'icon.png').read_bytes() == (root / 'assets/icon.png').read_bytes()
    assert package['imageUrl'] == release['sourceUrl'].rsplit('/', 1)[0] + '/icon.png'
    assert release['version'] == meta['version'] and release['targetAbi'] == meta['targetAbi']
    assert release['checksum'] == hashlib.md5(raw).hexdigest(), 'Manifest checksum mismatch'
    assert re.fullmatch(r'https://github\.com/[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+/releases/download/v' + re.escape(version) + '/' + re.escape(archive.name), release['sourceUrl']), 'Manifest must link directly to the matching release ZIP'
print(f'Verified {archive.name}: flat ZIP, DLL, license documents, versions and checksums')
