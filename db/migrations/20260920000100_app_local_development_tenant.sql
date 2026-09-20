-- Header-based development access resolves to local-dev. Once billing and API
-- keys are tenant-bound, that workspace must be a real durable tenant rather
-- than an identifier that exists only in application memory.
INSERT INTO app.tenants (tenant_id, display_name, kind)
VALUES ('local-dev', 'Local development workspace', 'organization')
ON CONFLICT (tenant_id) DO NOTHING;
