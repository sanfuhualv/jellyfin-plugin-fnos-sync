"""Build installation ZIP with license materials, checksums and optional manifest."""
from pathlib import Path
import argparse,hashlib,json,os,re,zipfile

root=Path(__file__).resolve().parents[1]
parser=argparse.ArgumentParser()
parser.add_argument('--dll',type=Path,default=root/'src/Jellyfin.Plugin.FnosSync/bin/Release/net10.0/Jellyfin.Plugin.FnosSync.dll')
parser.add_argument('--output',type=Path,default=root/'artifacts')
parser.add_argument('--repository',default=os.environ.get('GITHUB_REPOSITORY',''))
args=parser.parse_args()
version=re.search(r'ReleaseVersion = "([^"]+)"',(root/'src/Jellyfin.Plugin.FnosSync/Plugin.cs').read_text(encoding='utf-8')).group(1)
assert args.dll.is_file(),'Build the plugin before packaging'
if os.environ.get('GITHUB_REF_TYPE')=='tag':assert os.environ['GITHUB_REF_NAME']=='v'+version,'Tag and plugin version must match'
args.output.mkdir(parents=True,exist_ok=True)
meta={'guid':'918b455a-b767-45b2-8f9b-a1e44de57e91','name':'飞牛实时同步','version':version+'.0','targetAbi':'12.4.0.0','framework':'net10.0','owner':'jellyfin-plugin-fnos-sync contributors','overview':'飞牛影音与 Jellyfin 双向状态同步','status':'Active','autoUpdate':False,'imagePath':'icon.png'}
archive=args.output/f'Jellyfin.Plugin.FnosSync-{version}.zip'
with zipfile.ZipFile(archive,'w',zipfile.ZIP_DEFLATED) as z:
    entries=[('Jellyfin.Plugin.FnosSync.dll',args.dll.read_bytes()),('meta.json',json.dumps(meta,ensure_ascii=False,indent=2).encode())]
    entries.extend((name,(root/name).read_bytes()) for name in ['LICENSE','THIRD_PARTY_NOTICES.md','README.md'])
    entries.append(('icon.png',(root/'assets/icon.png').read_bytes()))
    for name,data in entries:
        entry=zipfile.ZipInfo(name,(2026,1,1,0,0,0));entry.compress_type=zipfile.ZIP_DEFLATED;entry.external_attr=0o100644<<16;z.writestr(entry,data)
archive.with_suffix('.zip.sha256').write_text(hashlib.sha256(archive.read_bytes()).hexdigest()+'  '+archive.name+'\n',encoding='utf-8')
(args.output/'icon.png').write_bytes((root/'assets/icon.png').read_bytes())
if args.repository:
    assert re.fullmatch(r'[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+',args.repository),'Use owner/repository'
    from datetime import datetime,timezone
    release={'version':version+'.0','targetAbi':'12.4.0.0','sourceUrl':f'https://github.com/{args.repository}/releases/download/v{version}/{archive.name}','checksum':hashlib.md5(archive.read_bytes()).hexdigest(),'timestamp':datetime.now(timezone.utc).isoformat()}
    manifest=[{k:v for k,v in meta.items() if k in ['guid','name','owner','overview']}]
    manifest[0].update(description='飞牛影音与 Jellyfin 收藏、已看和播放状态同步。密码加密保存，重启自动恢复连接。',category='General',imageUrl=f'https://github.com/{args.repository}/releases/download/v{version}/icon.png',versions=[release])
    (args.output/'manifest.json').write_text(json.dumps(manifest,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
print('Packaged',archive.name)
