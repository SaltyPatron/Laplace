"""Preserve complete Git histories before retiring accounted-for branch tips."""
import hashlib
import json
import os
import pathlib
import subprocess

repo = os.environ['GH_REPO']
out = pathlib.Path(os.environ['RUNNER_TEMP']) / 'branch-consolidation'
out.mkdir(parents=True, exist_ok=True)
(out / 'residual').mkdir(exist_ok=True)

def command(*args, input=None, check=True):
    result = subprocess.run(args, input=input, text=True, capture_output=True, timeout=240)
    if check and result.returncode:
        raise RuntimeError(f'{args[0]} {args[1]} failed ({result.returncode}): {result.stderr[-3000:]}')
    return result

def run(*args, input=None):
    return command(*args, input=input).stdout.strip()

def pages(endpoint, key=None):
    result = json.loads(run('gh', 'api', '--paginate', '--slurp', endpoint))
    return [item for page in result for item in (page[key] if key else page)]

def api(endpoint):
    return json.loads(run('gh', 'api', endpoint))

def active_heads():
    names = {p['head']['ref'] for p in pages(f'repos/{repo}/pulls?state=open&per_page=100')
             if (p['head'].get('repo') or {}).get('full_name') == repo}
    for status in ('in_progress', 'queued', 'waiting', 'requested', 'pending'):
        for item in pages(f'repos/{repo}/actions/runs?status={status}&per_page=100', 'workflow_runs'):
            if item.get('head_branch'):
                names.add(item['head_branch'])
    return names

def ancestor(a, b):
    result = command('git', 'merge-base', '--is-ancestor', a, b, check=False)
    if result.returncode not in (0, 1):
        raise RuntimeError(f'Cannot establish ancestry of {a} and {b}')
    return result.returncode == 0

def save(name, value):
    (out / name).write_text(json.dumps(value, indent=2) + '\n', encoding='utf-8')

report = {'repository': repo, 'removed': [], 'retained': [], 'failures': [], 'archive_verified': False}
try:
    default = api(f'repos/{repo}')['default_branch']
    report['default_branch'] = default
    run('git', 'config', 'user.name', 'GitHub Actions branch preservation')
    run('git', 'config', 'user.email', '41898282+github-actions[bot]@users.noreply.github.com')
    run('git', 'fetch', '--prune', 'origin',
        '+refs/heads/*:refs/remotes/origin/*', '+refs/pull/*/head:refs/recovery/pulls/*')
    base = run('git', 'rev-parse', f'refs/remotes/origin/{default}')
    base_tree = run('git', 'rev-parse', f'{base}^{{tree}}')
    report['base_sha'] = base
    heads = pages(f'repos/{repo}/branches?per_page=100')
    report['branches_before'] = len(heads)
    active = active_heads()
    branches = []
    for branch in heads:
        name, sha = branch['name'], branch['commit']['sha']
        run('git', 'check-ref-format', f'refs/heads/{name}')
        if command('git', 'cat-file', '-e', f'{sha}^{{commit}}', check=False).returncode:
            run('git', 'fetch', 'origin', sha)
        branches.append({'branch': name, 'sha': sha,
                         'tree': run('git', 'rev-parse', f'{sha}^{{tree}}'),
                         'protected': branch['protected']})
    pulls = []
    for line in run('git', 'for-each-ref', '--format=%(refname) %(objectname)', 'refs/recovery/pulls/').splitlines():
        ref, sha = line.split(' ', 1)
        pulls.append({'ref': ref, 'sha': sha})
    report['preserved_pull_heads'] = len(pulls)
    inventory = {'repository': repo, 'base_sha': base, 'branches': branches, 'pull_heads': pulls,
                 'meaning': 'Exact recoverable source history; preservation is not implementation integration.'}
    save('inventory.json', inventory)
    digest_input = {'repository': repo, 'branches': [b for b in branches if b['branch'] != default], 'pull_heads': pulls}
    digest = hashlib.sha256(json.dumps(digest_input, sort_keys=True).encode()).hexdigest()[:20]
    tag = f'branch-preservation/{digest}'
    tagref = f'refs/tags/{tag}'
    report['archive_tag'] = tag
    existing = run('git', 'ls-remote', '--refs', 'origin', tagref)
    if existing:
        run('git', 'fetch', 'origin', f'{tagref}:refs/recovery/verified-archive')
        archive = run('git', 'rev-parse', 'refs/recovery/verified-archive^{commit}')
        archived_inventory = json.loads(run('git', 'show', f'{archive}:.branch-preservation/inventory.json'))
        expected = {(b['branch'], b['sha']) for b in branches if b['branch'] != default}
        actual = {(b['branch'], b['sha']) for b in archived_inventory['branches'] if b['branch'] != default}
        if expected != actual or archived_inventory['pull_heads'] != pulls:
            raise RuntimeError('Existing archive does not match this exact branch/PR snapshot')
    else:
        blob = run('git', 'hash-object', '-w', '--stdin', input=json.dumps(inventory, indent=2) + '\n')
        readme = ('# Preserved branch history\n\n'
                  'inventory.json maps every original branch and pull-request head to its exact commit.\n'
                  'The parents of this archive commit retain complete Git histories and file trees.\n'
                  'This is a recovery snapshot, not a claim that all changes are active on main.\n\n'
                  f'Restore a listed head with `git fetch origin refs/tags/{tag}` followed by\n'
                  '`git switch -c recovered-work <listed-commit-sha>`.\n')
        readme_blob = run('git', 'hash-object', '-w', '--stdin', input=readme)
        metadata_tree = run('git', 'mktree', input=f'100644 blob {readme_blob}\tREADME.md\n100644 blob {blob}\tinventory.json\n')
        root = command('git', 'ls-tree', '-z', base).stdout
        root = ''.join(r + '\0' for r in root.split('\0') if r and r.split('\t', 1)[1] != '.branch-preservation')
        tree = run('git', 'mktree', '-z', input=root + f'040000 tree {metadata_tree}\t.branch-preservation\0')
        tips = list(dict.fromkeys([base] + [b['sha'] for b in branches] + [p['sha'] for p in pulls]))
        parents = list(dict.fromkeys([base] + run('git', 'merge-base', '--independent', *tips).splitlines()))
        args = ['git', 'commit-tree', tree]
        for sha in parents:
            args.extend(['-p', sha])
        archive = run(*args, input='Preserve exact branch and closed-PR histories before consolidation\n\nRecovery snapshot only; not a product source merge.\n')
        run('git', 'tag', '-a', tag, archive, '-m', f'Complete pre-consolidation history for {repo}; snapshot {digest}')
        run('git', 'push', 'origin', tagref)
    remote = dict(line.split('\t', 1)[::-1] for line in run('git', 'ls-remote', 'origin', tagref, tagref + '^{}').splitlines())
    if remote.get(tagref + '^{}') != archive:
        raise RuntimeError('Remote archive readback differs from the preserved commit')
    for entry in branches + pulls:
        if entry.get('branch') != default and not ancestor(entry['sha'], archive):
            raise RuntimeError('An original head is missing from the remotely anchored archive')
    report['archive_commit'] = archive
    report['archive_verified'] = True
    run('git', 'fetch', 'origin', f'{tagref}:refs/recovery/bundle-archive')
    run('git', 'bundle', 'create', str(out / 'original-history.bundle'), 'refs/recovery/bundle-archive')
    run('git', 'bundle', 'verify', str(out / 'original-history.bundle'))
    print(f'ARCHIVE_VERIFIED {tag} {archive} branches={len(branches)} pull_heads={len(pulls)}', flush=True)
    cache = {}
    def files(sha):
        if sha not in cache:
            raw = command('git', 'ls-tree', '-rz', sha).stdout
            cache[sha] = {r.split('\t', 1)[1]: r.split('\t', 1)[0] for r in raw.split('\0') if r}
        return cache[sha]
    main_files = files(base)
    review_text = command('git', 'show', base + ':.github/branch-resolutions.json', check=False)
    reviews = json.loads(review_text.stdout) if review_text.returncode == 0 else {}
    if reviews and reviews.get('repository') != repo:
        raise RuntimeError('Branch resolutions belong to a different repository')
    def reviewed_resolution(name, sha, paths):
        review = reviews.get('branches', {}).get(name)
        if not review or review.get('sha') != sha:
            return None
        basis = reviews.get('reviewed_base')
        if not basis or not ancestor(basis, base):
            return None
        product_paths = [path for path in paths if not path.startswith('.github/workflows/')]
        if any(files(basis).get(path) != main_files.get(path) for path in product_paths):
            return None
        reason = review.get('reason')
        return reason if isinstance(reason, str) and reason else None
    planned = []
    for index, branch in enumerate(branches):
        name, sha = branch['branch'], branch['sha']
        entry = dict(branch)
        if name == default or branch['protected'] or name in active:
            entry['reason'] = 'default/protected branch' if name == default or branch['protected'] else 'active PR or workflow'
            report['retained'].append(entry)
            continue
        common = run('git', 'merge-base', base, sha)
        paths = [p for p in command('git', 'diff', '--name-only', '-z', '--no-renames', common, sha, '--').stdout.split('\0') if p]
        entry['merge_base'] = common
        entry['changed_paths'] = paths
        entry['different_paths'] = [p for p in paths if files(sha).get(p) != main_files.get(p)]
        resolution = reviewed_resolution(name, sha, paths)
        if resolution:
            entry['reason'] = 'reviewed resolution: ' + resolution
            entry['reviewed_base'] = reviews['reviewed_base']
        elif ancestor(sha, base):
            entry['reason'] = 'already in main history; original tree independently archived'
        elif not paths:
            entry['reason'] = 'no net branch delta; complete history archived'
        elif not entry['different_paths']:
            entry['reason'] = 'all branch-delta paths match current main exactly'
        elif name.startswith(('diag/', 'verify/', 'qualify/', 'ops/', 'work/')) and all(
                p.startswith('.github/workflows/') and p.endswith(('.yml', '.yaml')) for p in entry['different_paths']):
            entry['reason'] = 'only an archived one-off workflow remains different'
        else:
            merge = command('git', 'merge-tree', '--write-tree', base, sha, check=False)
            if merge.returncode not in (0, 1):
                raise RuntimeError(f'Could not compare the complete merge tree for {name}')
            entry['mergeable'] = merge.returncode == 0
            entry['candidate_tree'] = merge.stdout.splitlines()[0]
            residual_paths = (run('git', 'diff', '--name-only', base, entry['candidate_tree'], '--').splitlines()
                              if entry['mergeable'] else [])
            workflow_residual = (bool(residual_paths) and name.startswith(('diag/', 'verify/', 'qualify/', 'ops/', 'work/'))
                                 and all(p.startswith('.github/workflows/') and p.endswith(('.yml', '.yaml')) for p in residual_paths))
            if workflow_residual:
                entry['reason'] = 'three-way product integration is unchanged; only an archived one-off workflow remains'
            elif entry['mergeable'] and entry['candidate_tree'] == base_tree:
                entry['reason'] = 'three-way integration produces the exact current main tree'
            else:
                entry['reason'] = 'unique product changes retained for actual integration'
                patch = command('git', 'diff', '--binary', '--full-index', common, sha, '--').stdout
                filename = f'residual/{index:03d}-{sha[:12]}.patch'
                (out / filename).write_text(patch, encoding='utf-8')
                entry['patch'] = filename
                entry['commits'] = run('git', 'log', '--no-merges', '--format=%H %s', f'{common}..{sha}').splitlines()
                save(filename + '.json', entry)
                report['retained'].append(entry)
                continue
        planned.append(entry)
    active = active_heads()
    for entry in planned:
        name, sha = entry['branch'], entry['sha']
        if name in active:
            entry['reason'] = 'became active after snapshot; retained'
            report['retained'].append(entry)
            continue
        deletion = command('git', 'push', '--porcelain',
                           f'--force-with-lease=refs/heads/{name}:{sha}', 'origin', f':refs/heads/{name}', check=False)
        if deletion.returncode:
            entry['reason'] = 'server refused deletion or branch moved; retained'
            report['retained'].append(entry)
            report['failures'].append({'branch': name, 'detail': deletion.stderr[-2000:]})
        else:
            report['removed'].append(entry)
    remaining = pages(f'repos/{repo}/branches?per_page=100')
    report['branches_after'] = len(remaining)
    report['remote_remaining'] = [{'branch': b['name'], 'sha': b['commit']['sha']} for b in remaining]
    print(f"CONSOLIDATION_RESULT before={len(branches)} removed={len(report['removed'])} remaining={len(remaining)}", flush=True)
    for item in report['retained']:
        print('RETAINED ' + json.dumps(item), flush=True)
except Exception as error:
    report['error'] = str(error)
    raise
finally:
    save('report.json', report)
    print('REPORT ' + json.dumps(report), flush=True)
    with open(os.environ['GITHUB_STEP_SUMMARY'], 'a', encoding='utf-8') as summary:
        summary.write('## Branch preservation and consolidation\n\n')
        summary.write(f"Archive verified: {report['archive_verified']}. Removed {len(report['removed'])} branches; retained {len(report['retained'])}.\n\n")
        if report.get('archive_tag'):
            summary.write(f"Recoverable Git archive: `{report['archive_tag']}`.\n\n")
        summary.write('Preserved history is not a claim that unresolved product work is implemented.\n')
if report['failures']:
    raise SystemExit('Some deletions were refused; preserved and remaining heads are in report.json')
