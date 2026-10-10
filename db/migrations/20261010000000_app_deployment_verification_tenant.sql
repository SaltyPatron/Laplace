-- Deployment checks use an isolated workspace and revocable workload credentials.
-- It has no human members and does not inherit any customer's billing state.
INSERT INTO app.tenants (tenant_id, display_name, kind)
VALUES ('deployment-verification', 'Installation verification', 'organization')
ON CONFLICT (tenant_id) DO NOTHING;
