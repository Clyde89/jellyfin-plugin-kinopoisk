#!/usr/bin/env python3
"""Сборка установочного архива кандидата без публикации релиза."""

import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import tempfile
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path(__file__).resolve().parents[2]
PROJECT = ROOT / 'src/Jellyfin.Plugin.Kinopoisk'
properties = ET.parse(PROJECT / 'Jellyfin.Plugin.Kinopoisk.csproj').getroot().find('PropertyGroup')
version = properties.findtext('Version')
review = properties.findtext('InformationalVersion').replace('$(Version)', version)
if not re.fullmatch(r'\d+[.]\d+[.]\d+[.]\d+-review[.]\d+', review):
    raise RuntimeError('Установочный архив разрешён только для проверочного кандидата')
commit = os.environ['GITHUB_SHA']
if not re.fullmatch(r'[a-f0-9]{40}', commit):
    raise RuntimeError('Некорректный коммит сборки')
names = ('Jellyfin.Plugin.Kinopoisk.dll', 'KinopoiskUnofficialInfo.ApiClient.dll')
output = PROJECT / 'bin/Release/net10.0'
files = {name: (output / name).read_bytes() for name in names}
manifest_source = (PROJECT / 'Services/KinopoiskWebClientBundle.cs').read_text()
scripts = re.findall(r'"Jellyfin[.]Plugin[.]Kinopoisk[.]Web[.]([^"\s]+[.]js)"', manifest_source)
if len(scripts) != 12:
    raise RuntimeError('Состав клиентского bundle изменился')
bundle = '\n\n'.join((PROJECT / 'Web' / name).read_bytes().decode('utf-8-sig') for name in scripts).encode()
sha = lambda value: hashlib.sha256(value).hexdigest()
artifacts = ROOT / 'artifacts'
artifacts.mkdir(exist_ok=True)
with tempfile.TemporaryDirectory() as temporary:
    directory = Path(temporary)
    with zipfile.ZipFile(directory / 'plugin.zip', 'w', zipfile.ZIP_DEFLATED) as archive:
        for name, body in files.items():
            archive.writestr(name, body)
    source = (ROOT / 'tools/systemd/deploy.py').read_text()
    source, count_version = re.subn(r'^VERSION = "[^"]+"$', f'VERSION = "{version}"', source, flags=re.M)
    source, count_commit = re.subn(r'^COMMIT = "[^"]+"$', f'COMMIT = "{commit}"', source, flags=re.M)
    if count_version != 1 or count_commit != 1:
        raise RuntimeError('Не удалось привязать установщик к кандидату')
    (directory / 'deploy.py').write_text(source)
    manifest = {
        'version': version,
        'candidate': review,
        'guid': '33e6d249-648f-44cd-a9ce-497be06c08df',
        'target_abi': '12.1.0.0',
        'commit': commit,
        'archive_sha256': sha((directory / 'plugin.zip').read_bytes()),
        'dll_sha256': {name: sha(body) for name, body in files.items()},
        'webclient_sha256': sha(bundle),
        'functional_acceptance': 'PENDING'
    }
    (directory / 'manifest.json').write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + '\n')
    (directory / 'INSTALL.txt').write_text(
        f'Проверочный кандидат {review}.\n'
        'Выполнить внутри контейнера Jellyfin от root:\n'
        'python3 deploy.py plan\n'
        'python3 deploy.py install\n'
        'python3 deploy.py check\n'
        'Установщик создаёт резервную копию и восстанавливает её при ошибке запуска.\n'
        'После установки открыть Jellyfin заново и проверить рекомендации и запрос.\n'
    )
    spec = importlib.util.spec_from_file_location('review_deploy', directory / 'deploy.py')
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    verified, payload = module.load_bundle(directory)
    if verified != manifest or payload != files:
        raise RuntimeError('Установщик прочитал другой состав кандидата')
    checksums = ''.join(f'{sha(file.read_bytes())}  {file.name}\n' for file in sorted(directory.iterdir()) if file.is_file())
    (directory / 'SHA256SUMS').write_text(checksums)
    archive_path = artifacts / f'kinopoisk-{review}-systemd.zip'
    with zipfile.ZipFile(archive_path, 'w', zipfile.ZIP_DEFLATED) as archive:
        for file in sorted(directory.iterdir()):
            if file.is_file():
                archive.write(file, file.name)
    print(f'{sha(archive_path.read_bytes())}  {archive_path.name}')
    print(json.dumps(manifest, ensure_ascii=False))
