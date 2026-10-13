#!/usr/bin/env python3
"""Exercise installed chess workers through the authenticated match API."""
import argparse
import json
import os
from pathlib import Path
import time
import urllib.error
import urllib.parse
import urllib.request


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--base', required=True)
    parser.add_argument('--profile', action='append', required=True)
    parser.add_argument('--conductor', action='append')
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    key = os.environ['LAPLACE_API_KEY']
    base = args.base.rstrip('/')
    conductors = args.conductor or ['fastchess', 'cutechess']
    args.output.mkdir(parents=True, exist_ok=True)
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))

    def request(path, body=None, raw=False):
        req = urllib.request.Request(base + path, data=None if body is None else json.dumps(body).encode(),
            headers={'Authorization': 'Bearer ' + key, 'Content-Type': 'application/json'})
        with opener.open(req, timeout=30) as response:
            return response.read().decode() if raw else json.load(response)

    def save(name, value):
        (args.output / name).write_text(json.dumps(value, indent=2) + '\n')

    catalogue = request('/chess/lab/catalog')
    for profile in args.profile:
        if not any(p['id'] == profile and p['implementation'] == 'laplace-mono' for p in catalogue['connections']):
            raise RuntimeError('Installed API has no declared monorepo worker ' + profile)
    results = []
    job_id = None
    try:
        for profile in args.profile:
            for conductor in conductors:
                config = dict(conductor=conductor, laplaceProfile=profile, rounds='2', depth='1', concurrency='1',
                    ingest='true', stockfishThreads='1', stockfishHashMb='16', limitStrength='false')
                if not request('/chess/lab/cutechess/preview?' + urllib.parse.urlencode(config))['ready']:
                    raise RuntimeError('Installed conductor is not ready: ' + conductor)
                job_id = request('/chess/lab/start', {'kind': 'cutechess', 'config': config})['jobId']
                print(f'{conductor} running {job_id} on {profile}', flush=True)
                deadline = time.monotonic() + 480
                while True:
                    job = request('/chess/lab/jobs/' + job_id)
                    if job['state'] in ['Completed', 'Failed', 'Cancelled']:
                        break
                    if time.monotonic() >= deadline:
                        raise RuntimeError('Match exceeded its verification observation window.')
                    time.sleep(2)
                transcript = request('/chess/lab/jobs/' + job_id + '/terminal.txt', raw=True)
                (args.output / (job_id + '.log')).write_text(transcript)
                if job['state'] != 'Completed':
                    raise RuntimeError(f'{conductor}/{profile}: ' + str(job['summary']))
                recording = request('/chess/lab/jobs/' + job_id + '/artifact/recording.json')
                experiment = request('/chess/lab/jobs/' + job_id + '/artifact/experiment.json')
                if recording['status'] != 'completed' or recording['readbackGames'] != 2 or recording['readbackPlies'] <= 0:
                    raise RuntimeError('Recorded games did not read back.')
                if not job.get('requester') or experiment['requester'] != job['requester']:
                    raise RuntimeError('Recorded requester differs from the authenticated job.')
                if experiment['requestedOptions']['laplaceConnection']['id'] != profile:
                    raise RuntimeError('Recorded connection differs from selected worker.')
                if not any('info string laplace knowledge ' in line for line in experiment['uciConfiguration'].get('Laplace', [])):
                    raise RuntimeError('Remote Laplace did not report its actual knowledge identity.')
                result = dict(profile=profile, conductor=conductor, job=job, recording=recording, experiment=experiment)
                save(job_id + '.json', result)
                results.append(result)
                print(f'{conductor}/{profile}: {recording["readbackGames"]} recorded games / {recording["readbackPlies"]} plies', flush=True)
                job_id = None
        config = dict(conductor=conductors[0], laplaceProfile=args.profile[0], rounds='100', depth='20', concurrency='1',
            ingest='true', stockfishThreads='1', stockfishHashMb='16', limitStrength='false')
        job_id = request('/chess/lab/start', {'kind': 'cutechess', 'config': config})['jobId']
        deadline = time.monotonic() + 40
        while 'info string laplace knowledge ' not in request('/chess/lab/jobs/' + job_id + '/terminal.txt', raw=True):
            if time.monotonic() >= deadline:
                raise RuntimeError('No remote engine session appeared before cancellation.')
            time.sleep(1)
        request('/chess/lab/stop/' + job_id, {})
        deadline = time.monotonic() + 30
        while True:
            cancelled = request('/chess/lab/jobs/' + job_id)
            if cancelled['state'] == 'Cancelled':
                break
            if time.monotonic() >= deadline:
                raise RuntimeError('Cancellation did not complete.')
            time.sleep(1)
        job_id = None
        try:
            request('/chess/lab/start', {'kind': 'cutechess', 'config': {'laplaceProfile': 'unregistered-worker'}})
        except urllib.error.HTTPError as error:
            if error.code != 400:
                raise
        else:
            raise RuntimeError('An undeclared worker was not rejected.')
        save('receipt.json', dict(verified_utc=time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime()), api=base,
            matches=results, cancelled=cancelled, undeclared_worker_status=400))
        print('Cross-host matches, recording readback and cancellation passed: ' + str(args.output), flush=True)
    finally:
        if job_id:
            request('/chess/lab/stop/' + job_id, {})


if __name__ == '__main__':
    main()
