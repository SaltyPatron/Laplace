# Received-row workspace

`ResultWorkspace<T>` receives a `RowSnapshot<T>`, an explicit principal/tenant/collection `scopeKey`, optional display columns, and optional row labels. Use `captureRows` once **after the actual read resolves**, with its submitted inputs and truthful response boundary. Do not capture the currently edited form as the provenance of an earlier response. Never put credentials in snapshot context.

Columns are presentation adapters. They may render an authoritative record reference as a normal link; they must not infer that every ID-looking value names an entity. Extra returned fields remain available in the column chooser and row inspector. No data provider, remote URL, ranking, traversal or authorization lives in the component.

Selection uses response identity plus original response ordinal, preserving repeated equal records as distinct received occurrences. Presentation filtering and pagination do not change those ordinals or select unseen remote rows. Refresh retains selected rows with their old boundaries instead of silently replacing them. A scope change disposes the working selection. Field comparisons page the selected records without a semantic score or all-pairs computation.

JSON export includes selected ordinals, received values, request context, time and coverage. It does **not** claim original-byte fidelity, a remote snapshot transaction, all-matching coverage or exact precision that the transport did not preserve. Source-byte export is a separate content-readback operation. Current consumers include Query, installed operations, source assertions, run/file receipts and artifact selection.

This is not the full #277 collection/query engine: server cursors, global filters/top-N, saved native query recipes, grant-aware related queries and durable large-export jobs still need their existing providers.
