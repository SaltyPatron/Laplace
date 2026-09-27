-- Header-based development access resolves to local-dev. Billing and API keys are
-- tenant-bound, so that workspace is a durable row in app.tenants.
INSERT INTO app.tenants (tenant_id, display_name, kind)
VALUES ('local-dev', 'Local development workspace', 'organization')
ON CONFLICT (tenant_id) DO NOTHING;
