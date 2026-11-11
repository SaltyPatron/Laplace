/** Self-play training state for the play view: status polling, knobs and start/stop requests. */
import { useCallback, useEffect, useRef, useState } from 'react';

import { useVisiblePolling } from '@ui';

import { apiGet, apiPost } from '../../api/client';

import type { TrainKnobs, TrainStatus } from './EnginePanel';
import { msg } from './playTypes';

export function useTraining() {
  const [train, setTrain] = useState<TrainStatus | null>(null);
  const [trainStatusError, setTrainStatusError] = useState<string | null>(null);
  const [trainActionError, setTrainActionError] = useState<string | null>(null);
  const [trainBusy, setTrainBusy] = useState(false);
  const trainActionPending = useRef(false);
  const trainReadSequence = useRef(0);
  const trainReadController = useRef<AbortController | null>(null);

  const [knobs, setKnobs] = useState<TrainKnobs>({ games: 200, temp: 120, maxPlies: 200, weight: 0.5 });

  const fetchTrain = useCallback(async () => {
    const sequence = ++trainReadSequence.current;
    trainReadController.current?.abort();
    const controller = new AbortController();
    trainReadController.current = controller;
    try {
      const current = await apiGet<TrainStatus>('/chess/train/status', { signal: controller.signal });
      if (sequence !== trainReadSequence.current) return;
      setTrain(current);
      setTrainStatusError(null);
    } catch (failure) {
      if (sequence !== trainReadSequence.current) return;
      setTrainStatusError(`Cannot read training status: ${msg(failure)}`);
    }
  }, []);

  useEffect(() => {
    void fetchTrain();
    return () => {
      ++trainReadSequence.current;
      trainReadController.current?.abort();
    };
  }, [fetchTrain]);

  useVisiblePolling(fetchTrain, {
    intervalMs: 1500,
    enabled: !!train?.running && !trainBusy,
    immediate: false,
  });

  const changeTraining = async (action: 'start' | 'stop') => {
    if (trainActionPending.current) return;
    trainActionPending.current = true;
    ++trainReadSequence.current;
    trainReadController.current?.abort();
    setTrainBusy(true);
    setTrainActionError(null);

    const q = new URLSearchParams({
      games: String(knobs.games), temperature: String(knobs.temp),
      maxPlies: String(knobs.maxPlies), weight: String(knobs.weight),
    }).toString();

    try {
      await apiPost(`/chess/train/${action}${action === 'start' ? `?${q}` : ''}`, {});
      await fetchTrain();
    } catch (failure) {
      setTrainActionError(`${action === 'start' ? 'Start' : 'Stop'} request failed: ${msg(failure)}. Refresh status before trying again.`);
      setTrain(null);
    } finally {
      trainActionPending.current = false;
      setTrainBusy(false);
    }
  };

  return { train, trainBusy, trainStatusError, trainActionError, fetchTrain, knobs, setKnobs, changeTraining };
}
