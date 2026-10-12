// Browser-visible raw transport proof; no request reaches an installed service.
const original = new TextEncoder().encode('\ufeff King != king\r\ne\u0301\n');
const receipt = { file_id: 'a'.repeat(32), document_id: 'b'.repeat(32), content_id: 'c'.repeat(32), metadata_id: 'd'.repeat(32), source_id: 'e'.repeat(32), source: 'fixture', bytes: original.length, modality: null };
let complete: (() => void) | undefined;
let calls = 0;
document.getElementById('complete')!.onclick = () => complete?.();
window.fetch = async (input, init) => {
  const url = new URL(String(input), location.origin);
  if (url.pathname === '/v1/content/text/raw') {
    const bytes = new Uint8Array(await new Response(init?.body).arrayBuffer());
    document.getElementById('calls')!.textContent = String(++calls);
    document.getElementById('bytes')!.textContent = bytes.length === original.length && bytes.every((byte, index) => byte === original[index])
      && url.searchParams.get('path') === 'exact.txt' && url.searchParams.get('name') === 'exact.txt' ? 'exact' : 'mismatch';
    await new Promise<void>((resolve) => { complete = resolve; });
    return Response.json(receipt);
  }
  if (url.pathname === `/v1/content/${receipt.file_id}`) return Response.json({ ...receipt, kind: 'file', requested_id: receipt.file_id,
    name: 'exact.txt', path: 'exact.txt', text: '\ufeff King != king\r\ne\u0301\n', content_base64: null, contexts: [], modified_at: null });
  if (url.pathname === `/v1/content/${receipt.file_id}/raw`) return new Response(original, { headers: { 'Content-Type': 'application/octet-stream' } });
  return Response.json({ error: { message: 'Unprovided fixture route' } }, { status: 501 });
};
await import('./data-fixture');
export {};
