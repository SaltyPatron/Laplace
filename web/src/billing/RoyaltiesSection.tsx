import { type FormEvent, useEffect, useState } from 'react';
import { Button, ErrorText, Field, Input, Muted, ReadStatus, Select, Table, TableScroll, Td, Th, useReadResource } from '@ui';
import { apiGet, apiPost } from '../api/client';
import type { components } from '../api/types.gen';
import { formatCents } from './amounts';
import { humanizeIdentifier } from './labels';
import styles from './BillingView.module.css';

type RoyaltiesResponse = components['schemas']['RoyaltiesResponse'];

type WitnessPayee = {
  witness_id: string;
  owner_tenant?: string | null;
  license_kind: string;
  display_name: string;
  updated_at: string;
};

type WitnessPayeesResponse = {
  tenant: string;
  payees: WitnessPayee[];
};

export function RoyaltiesSection({
  tenant,
  authUserId,
  signedIn,
  refreshSignal,
  serviceName,
}: {
  tenant: string;
  authUserId?: string;
  signedIn: boolean;
  refreshSignal: number;
  serviceName: (id: string) => string;
}) {
  const [showRoyalties, setShowRoyalties] = useState(false);
  const [payeeWitnessId, setPayeeWitnessId] = useState('');
  const [payeeDisplayName, setPayeeDisplayName] = useState('');
  const [payeeLicenseKind, setPayeeLicenseKind] = useState<'royalty_bearing' | 'open_license'>('royalty_bearing');
  const [payeePending, setPayeePending] = useState(false);
  const [payeeError, setPayeeError] = useState('');

  const royaltiesRead = useReadResource({
    key: JSON.stringify(['billing-royalties', tenant, authUserId]),
    enabled: signedIn && showRoyalties,
    read: (signal) => apiGet<RoyaltiesResponse>('/v1/billing/royalties', { tenant, signal }),
  });
  const payeesRead = useReadResource({
    key: JSON.stringify(['billing-payees', tenant, authUserId]),
    enabled: signedIn && showRoyalties,
    read: (signal) => apiGet<WitnessPayeesResponse>('/v1/billing/payees', { tenant, signal }),
  });

  useEffect(() => {
    if (refreshSignal > 0 && signedIn && showRoyalties) {
      setPayeeError('');
      void royaltiesRead.refresh();
      void payeesRead.refresh();
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [refreshSignal]);

  async function registerPayee(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (payeePending || !signedIn) return;
    setPayeePending(true);
    setPayeeError('');
    try {
      await apiPost('/v1/billing/payees', {
        witness_id: payeeWitnessId.trim(),
        display_name: payeeDisplayName.trim() || undefined,
        license_kind: payeeLicenseKind,
        owner_tenant: payeeLicenseKind === 'royalty_bearing' ? tenant : undefined,
      }, { tenant });
      setPayeeWitnessId('');
      setPayeeDisplayName('');
      void payeesRead.refresh();
      void royaltiesRead.refresh();
    } catch (failure) {
      setPayeeError(failure instanceof Error ? failure.message : 'Failed to register witness payee.');
    } finally {
      setPayeePending(false);
    }
  }

  return (
    <>
      <h2>Creator source royalties (<code>share_bp</code> / 10000)</h2>
      {!signedIn ? <Muted>Sign in to view source royalties attributed to this workspace.</Muted> : (
        <>
          <div className={styles.sectionHead}>
            <Muted>Integer-conserved basis points attributed to attested sources across executed quotes (Σ share_bp = 10000).</Muted>
            <Button variant="ghost" onClick={() => setShowRoyalties((v) => !v)}>
              {showRoyalties ? 'Hide royalties ledger' : 'Load royalties ledger'}
            </Button>
          </div>
          {showRoyalties && (
            <>
              <ReadStatus label="Source royalties" resource={royaltiesRead} />
              {royaltiesRead.data && (
                royaltiesRead.data.entries.length === 0 ? (
                  <Muted>No royalty entries recorded for this workspace.</Muted>
                ) : (
                  <>
                    <p>
                      Total attributed: {formatCents(royaltiesRead.data.total_royalty_cents)} · Σ share_bp:{' '}
                      {royaltiesRead.data.entries.reduce((acc, entry) => acc + Number(entry.share_bp), 0).toLocaleString()} bp
                    </p>
                    <TableScroll className={styles.catalogScroll}>
                      <Table className={styles.catalog}>
                        <thead>
                          <tr>
                            <Th>Source</Th>
                            <Th>Owner</Th>
                            <Th>Service</Th>
                            <Th>Payer</Th>
                            <Th>share_bp</Th>
                            <Th>Hits</Th>
                            <Th>Royalty</Th>
                            <Th>Executed</Th>
                          </tr>
                        </thead>
                        <tbody>
                          {royaltiesRead.data.entries.map((entry, idx) => (
                            <tr key={`${entry.quote_id}:${entry.source_id}:${idx}`}>
                              <Td><code>{entry.source_id}</code></Td>
                              <Td>{entry.source_owner_tenant ?? 'open_license (0¢)'}</Td>
                              <Td>{serviceName(entry.service_id)}</Td>
                              <Td>{entry.payer_tenant}</Td>
                              <Td>{entry.share_bp} / 10000</Td>
                              <Td>{Number(entry.hit_count).toLocaleString()}</Td>
                              <Td>{formatCents(entry.royalty_cents)}</Td>
                              <Td>{entry.executed_at ? new Date(entry.executed_at).toLocaleString() : '—'}</Td>
                            </tr>
                          ))}
                        </tbody>
                      </Table>
                    </TableScroll>
                  </>
                )
              )}
              <div className={styles.current}>
                <h3>Witness payees</h3>
                <Muted>Register 32-hex witness identities as royalty-bearing or open-license for two-sided attribution.</Muted>
                <ReadStatus label="Witness payees" resource={payeesRead} />
                {payeeError && <ErrorText role="alert">{payeeError}</ErrorText>}
                {payeesRead.data && (
                  payeesRead.data.payees.length === 0 ? (
                    <Muted>No witness payees registered for this workspace yet.</Muted>
                  ) : (
                    <TableScroll className={styles.catalogScroll}>
                      <Table className={styles.catalog}>
                        <thead>
                          <tr>
                            <Th>Witness ID</Th>
                            <Th>Display name</Th>
                            <Th>License</Th>
                            <Th>Owner</Th>
                            <Th>Updated</Th>
                          </tr>
                        </thead>
                        <tbody>
                          {payeesRead.data.payees.map((payee) => (
                            <tr key={payee.witness_id}>
                              <Td><code>{payee.witness_id}</code></Td>
                              <Td>{payee.display_name}</Td>
                              <Td>{humanizeIdentifier(payee.license_kind)}</Td>
                              <Td>{payee.owner_tenant ?? 'open_license'}</Td>
                              <Td>{payee.updated_at ? new Date(payee.updated_at).toLocaleString() : '—'}</Td>
                            </tr>
                          ))}
                        </tbody>
                      </Table>
                    </TableScroll>
                  )
                )}
                <form className={styles.entitlement} onSubmit={(e) => void registerPayee(e)}>
                  <Field label="Witness ID (32-hex)">
                    <Input
                      value={payeeWitnessId}
                      onChange={(e) => setPayeeWitnessId(e.target.value)}
                      placeholder="0123456789abcdef0123456789abcdef"
                      disabled={payeePending}
                    />
                  </Field>
                  <Field label="Display name">
                    <Input
                      value={payeeDisplayName}
                      onChange={(e) => setPayeeDisplayName(e.target.value)}
                      placeholder="Corpus witness name"
                      disabled={payeePending}
                    />
                  </Field>
                  <Field label="License kind">
                    <Select
                      value={payeeLicenseKind}
                      onChange={(e) => setPayeeLicenseKind(e.target.value as 'royalty_bearing' | 'open_license')}
                      disabled={payeePending}
                    >
                      <option value="royalty_bearing">royalty_bearing</option>
                      <option value="open_license">open_license</option>
                    </Select>
                  </Field>
                  <div>
                    <Button type="submit" loading={payeePending} disabled={payeePending || !payeeWitnessId.trim()}>
                      Register witness payee
                    </Button>
                  </div>
                </form>
              </div>
            </>
          )}
        </>
      )}
    </>
  );
}
