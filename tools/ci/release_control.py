"""Publish catalog metadata and retire explicitly approved, completed branches."""
import argparse
import base64
import copy
import datetime
import json
import os
from pathlib import Path
import re
import subprocess
import urllib.error
import urllib.parse
import urllib.request


def updated_catalog(catalog, metadata, version_entry):
    result = copy.deepcopy(catalog)
    matches = [item for item in result if item.get('guid') == metadata['guid']]
    if len(matches) != 1:
        raise ValueError('Каталог должен содержать ровно одну запись плагина')
    plugin = matches[0]
    existing = [v for v in plugin['versions'] if v['version'] == version_entry['version']]
    if existing:
        if len(existing) != 1 or any(existing[0].get(k) != v for k, v in version_entry.items() if k != 'timestamp'):
            raise ValueError('В каталоге уже опубликована другая сборка этой версии')
    else:
        plugin['versions'].insert(0, version_entry)
    plugin['overview'] = metadata['overview']
    plugin['description'] = metadata['description']
    return result


def retirement_target(entry, release_sha, default_branch):
    name = entry['name']
    if name in {'develop', 'master', 'main', 'release', default_branch}:
        raise ValueError('Основная ветка не может быть удалена')
    if not re.fullmatch(r'(?:feature|fix)/[a-zA-Z0-9._-]+', name):
        raise ValueError('Недопустимая рабочая ветка')
    sha = release_sha if entry.get('releaseBranch') else entry['sha']
    if not re.fullmatch(r'[a-f0-9]{40}', sha):
        raise ValueError('Требуется полный SHA коммита')
    return name, sha


class GitHub:
    def __init__(self):
        self.repo = os.environ['GITHUB_REPOSITORY']
        self.token = os.environ['GITHUB_TOKEN']

    def api(self, method, endpoint, payload=None):
        body = None if payload is None else json.dumps(payload, ensure_ascii=False).encode()
        request = urllib.request.Request(
            'https://api.github.com/repos/' + self.repo + endpoint, data=body, method=method,
            headers={'Authorization': 'Bearer ' + self.token, 'Accept': 'application/vnd.github+json',
                     'X-GitHub-Api-Version': '2022-11-28', 'Content-Type': 'application/json'})
        with urllib.request.urlopen(request, timeout=60) as response:
            return json.load(response)


def publish_catalog(api):
    metadata = json.loads(Path('release/metadata.json').read_text())
    version = os.environ['VERSION']
    # Publication and both expected assets must precede a visible catalog entry.
    release = api.api('GET', '/releases/tags/v' + version)
    filename = os.environ['RELEASE_FILENAME']
    if release['draft'] or release['prerelease'] or not {filename, filename + '.sha256'} <= {a['name'] for a in release['assets']}:
        raise ValueError('Релиз и оба файла должны быть опубликованы перед обновлением каталога')
    entry = {'version': version, 'checksum': os.environ['RELEASE_MD5'],
             'changelog': Path('release-notes.md').read_text().strip(),
             'targetAbi': os.environ['TARGET_ABI'], 'sourceUrl': os.environ['SOURCE_URL'],
             'timestamp': datetime.datetime.now(datetime.timezone.utc).strftime('%Y-%m-%dT%H:%M:%SZ')}
    path = '/contents/dist/manifest.json'
    current = api.api('GET', path + '?ref=release')
    catalog = json.loads(base64.b64decode(current['content']))
    updated = updated_catalog(catalog, metadata, entry)
    if updated != catalog:
        content = (json.dumps(updated, ensure_ascii=False, indent=4) + '\n').encode()
        api.api('PUT', path, {'message': 'Опубликована версия ' + version + ' в каталоге плагинов [skip ci]',
                             'branch': 'release', 'sha': current['sha'],
                             'content': base64.b64encode(content).decode()})
    # Re-read committed content before permitting branch cleanup.
    saved = api.api('GET', path + '?ref=release')
    if json.loads(base64.b64decode(saved['content'])) != updated:
        raise ValueError('Опубликованный каталог не совпал с подготовленным')
    print('Каталог обновлён: ' + version)


def git(*args, env=None):
    return subprocess.run(['git', *args], check=True, capture_output=True, text=True, env=env).stdout.strip()


def retire_branches(api):
    release_sha = os.environ['GITHUB_SHA']
    release_version = json.loads(Path('release/version.json').read_text())['version']
    # Never remove branches unless the release tag points at this exact validated commit.
    if git('rev-parse', 'v' + release_version + '^{commit}') != release_sha:
        raise ValueError('Тег релиза не совпал с проверенным коммитом')
    default = api.api('GET', '')['default_branch']
    plan = json.loads(Path('release/retired-branches.json').read_text())
    auth = base64.b64encode(('x-access-token:' + api.token).encode()).decode()
    push_env = dict(os.environ, GIT_CONFIG_COUNT='1', GIT_CONFIG_KEY_0='http.https://github.com/.extraheader',
                    GIT_CONFIG_VALUE_0='AUTHORIZATION: basic ' + auth, GIT_TERMINAL_PROMPT='0')
    for entry in plan:
        name, expected = retirement_target(entry, release_sha, default)
        try:
            current = api.api('GET', '/git/ref/heads/' + urllib.parse.quote(name, safe='/'))['object']['sha']
        except urllib.error.HTTPError as error:
            if error.code == 404:
                print('Ветка уже удалена: ' + name)
                continue
            raise
        if current != expected:
            print('Ветка сохранена: после согласования появились изменения: ' + name)
            continue
        # A squash integration must have an identical tree and be part of the release history.
        integrated = entry.get('integratedAs', expected)
        git('merge-base', '--is-ancestor', integrated, release_sha)
        git('merge-base', '--is-ancestor', entry['sha'], expected)
        if 'integratedAs' in entry:
            git('diff', '--exit-code', expected, integrated)
            archive = 'archive/' + name
            try:
                saved = api.api('GET', '/git/ref/tags/' + archive)['object']['sha']
            except urllib.error.HTTPError as error:
                if error.code != 404:
                    raise
                saved = api.api('POST', '/git/refs', {'ref': 'refs/tags/' + archive, 'sha': expected})['object']['sha']
            if saved != expected:
                raise ValueError('Архивный тег указывает на другой коммит: ' + name)
        # The lease prevents deleting a branch updated between verification and this push.
        git('push', '--force-with-lease=refs/heads/' + name + ':' + expected,
            'origin', ':refs/heads/' + name, env=push_env)
        print('Удалена завершённая ветка: ' + name)


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('operation', choices=['catalog', 'cleanup'])
    args = parser.parse_args()
    api = GitHub()
    if args.operation == 'catalog':
        publish_catalog(api)
    else:
        retire_branches(api)
