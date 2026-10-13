#!/usr/bin/env python3
"""Interrupt final recording on the installed API, then recover the retained game.

Run under the deployment host reservation. The declared PostgreSQL connection must
own the existing substrate. A bounded journal-table lock holds final recording while
the setup-owned restart trigger replaces the API process. No database is created.
"""
import argparse
import concurrent.futures
import json
import os
from pathlib import Path
import select
import subprocess
import time
import urllib.error
import urllib.request


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--api-base', required=True)
    parser.add_argument('--psql', required=True)
    parser.add_argument('--service', required=True)
    parser.add_argument('--restart-file', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    key = os.environ['LAPLACE_API_KEY']
    receipt = {'status': 'failed', 'origin': args.api_base}

    def request(path, body=None, authenticated=True):
        headers = {'Content-Type': 'application/json'}
        if authenticated:
            headers['Authorization'] = 'Bearer ' + key
        req = urllib.request.Request(args.api_base.rstrip('/') + path,
                                     data=None if body is None else json.dumps(body).encode(), headers=headers)
        with urllib.request.urlopen(req, timeout=70) as response:
            return json.load(response)

    def wait_for(probe, seconds=60):
        deadline = time.monotonic() + seconds
        while time.monotonic() < deadline:
            try:
                value = probe()
                if value:
                    return value
            except (OSError, urllib.error.URLError):
                pass
            time.sleep(.2)
        raise TimeoutError('Installed operation did not reach the required state')

    def process_id():
        return int(subprocess.check_output(
            ['systemctl', 'show', args.service, '--property=MainPID', '--value'], text=True).strip())

    try:
        started = request('/chess/play/start', {'record': True})
        session = started['sessionId']
        path = '/chess/play/' + session
        fen = started['fen']
        moves = ['e2e4', 'c7c5', 'g1f3', 'b8c6', 'f1b5', 'g8f6', 'e1g1', 'd7d6']
        for move in moves:
            played = request('/chess/play/move', {'sessionId': session, 'fen': fen, 'uci': move})
            assert played['legal'] and not played['terminal'], played
            fen = played['fen']
        before = request(path)
        receipt.update(session=session, before=before)
        old_pid = process_id()
        assert old_pid > 0
        lock = subprocess.Popen([args.psql, '-X', '-qAt', '-v', 'ON_ERROR_STOP=1'],
                                stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                text=True, bufsize=1)
        try:
            lock.stdin.write("BEGIN; SET LOCAL lock_timeout = '5s'; "
                             "SET LOCAL idle_in_transaction_session_timeout = '75s'; "
                             "LOCK TABLE laplace.ingest_flush_journal IN EXCLUSIVE MODE;\n"
                             "\\echo recording-held\n")
            lock.stdin.flush()
            ready, _, _ = select.select([lock.stdout], [], [], 10)
            if not ready or lock.stdout.readline().strip() != 'recording-held':
                raise RuntimeError('Could not reserve the existing recording journal')
            with concurrent.futures.ThreadPoolExecutor(max_workers=1) as executor:
                finishing = executor.submit(request, '/chess/play/finish',
                                             {'sessionId': session, 'status': 'draw', 'adjudicated': True})
                try:
                    pending = wait_for(lambda: (op if (op := request(path))['state'] == 'recording' else None), 15)
                    assert not finishing.done(), 'Recording was not held across the restart'
                    receipt['interrupted'] = pending
                    args.restart_file.touch()
                    new_pid = wait_for(lambda: (pid if (pid := process_id()) > 0 and pid != old_pid else None), 55)
                    receipt['processes'] = [old_pid, new_pid]
                finally:
                    lock.stdin.write('ROLLBACK;\n\\q\n')
                    lock.stdin.flush()
                    lock.wait(timeout=5)
                try:
                    finishing.result(timeout=10)
                except (OSError, urllib.error.URLError):
                    pass
        finally:
            if lock.poll() is None:
                lock.terminate()
                lock.wait(timeout=5)
            for stream in (lock.stdin, lock.stdout, lock.stderr):
                stream.close()
        retained = wait_for(lambda: request(path))
        assert retained['canRetryRecording'], retained
        assert retained['requester'] == before['requester']
        assert retained['moves'] == moves and retained['fen'] == fen
        assert retained['result'] == '1/2-1/2' and retained['adjudicated'] is True
        receipt['retained_after_restart'] = retained
        try:
            request(path + '/recording/retry', {}, authenticated=False)
            raise AssertionError('Unauthenticated recording retry was accepted')
        except urllib.error.HTTPError as error:
            assert error.code in (401, 403), error.code
            receipt['unauthenticated_retry'] = error.code
        request(path + '/recording/retry', {})
        completed = request(path)
        assert completed['state'] == 'completed' and completed['recording'] == 'recorded'
        assert completed['requester'] == before['requester'] and completed['analysis'] == before['analysis']
        assert completed['moves'] == moves and completed['fen'] == fen
        assert completed['adjudicated'] is True and completed['result'] == '1/2-1/2'
        game = request('/v1/chess/games/' + completed['recordedPlayingId'])
        assert game['movetext'] == '1. e4 c5 2. Nf3 Nc6 3. Bb5 Nf6 4. O-O d6 1/2-1/2', game
        assert game['result'] == '1/2-1/2' and game['termination'] == 'adjudicated'
        request(path + '/recording/retry', {})
        assert request(path) == completed, 'Completed retry changed the retained game'
        assert request('/v1/chess/games/' + completed['recordedPlayingId']) == game
        receipt.update(status='passed', completed=completed, game=game, completed_retry='unchanged')
    finally:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps(receipt, indent=2) + '\n')
    print('Installed recording recovered after process replacement; exact game and repeated retry verified.')


if __name__ == '__main__':
    main()
