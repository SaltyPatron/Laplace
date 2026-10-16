-- Seed default platform administrator bindings for the installation owner accounts
-- (`aharttn@gmail.com` and `thesaltypatron@gmail.com` across Google and Microsoft OAuth)
-- so fresh or re-created databases always start with owner administrator access.
DO $$
DECLARE
    rec RECORD;
    v_user_id uuid;
    v_tenant_id text;
    v_prev_role text;
BEGIN
    LOCK TABLE app.platform_roles IN SHARE ROW EXCLUSIVE MODE;

    FOR rec IN
        SELECT * FROM (VALUES
            (
                '51c8d4fc-a658-4907-b16a-1878e73b9190'::uuid,
                '993b7b67-e2a1-4b2b-9f16-5fb3f4844101'::uuid,
                't-51c8d4fca6584907b16a1878e73b9190',
                'google',
                'https://accounts.google.com',
                '105368356205536670205',
                'Anthony Hart',
                'aharttn@gmail.com',
                '2026-10-15T18:00:50.389934Z'::timestamptz
            ),
            (
                'c3fbe492-4748-46e6-adfe-52fda500e882'::uuid,
                '993b7b67-e2a1-4b2b-9f16-5fb3f4844102'::uuid,
                't-c3fbe492474846e6adfe52fda500e882',
                'google',
                'https://accounts.google.com',
                '105862061897402259594',
                'Salty Patron',
                'thesaltypatron@gmail.com',
                '2026-10-15T18:00:57.895750Z'::timestamptz
            ),
            (
                '48cf86ec-4db0-4bf4-9264-4fcf3b09482d'::uuid,
                '993b7b67-e2a1-4b2b-9f16-5fb3f4844103'::uuid,
                't-48cf86ec4db04bf492644fcf3b09482d',
                'microsoft',
                'https://login.microsoftonline.com/9188040d-6c67-4c5b-b112-36a304b66dad/v2.0',
                'AAAAAAAAAAAAAAAAAAAAANrDmzXa_OckY6Ame0a1110',
                'Salty Patron',
                'thesaltypatron@gmail.com',
                '2026-10-15T21:07:08.745445Z'::timestamptz
            )
        ) AS t(default_user_id, default_identity_id, default_tenant_id, provider, issuer, subject, display_name, email, verified_at)
    LOOP
        SELECT user_id INTO v_user_id
        FROM app.external_identities
        WHERE provider = rec.provider
          AND issuer = rec.issuer
          AND subject = rec.subject;

        IF v_user_id IS NULL THEN
            v_user_id := rec.default_user_id;
            INSERT INTO app.users (user_id, display_name, email)
            VALUES (v_user_id, rec.display_name, rec.email)
            ON CONFLICT (user_id) DO NOTHING;

            INSERT INTO app.external_identities (identity_id, user_id, provider, issuer, subject, email, last_login_at)
            VALUES (rec.default_identity_id, v_user_id, rec.provider, rec.issuer, rec.subject, rec.email, rec.verified_at)
            ON CONFLICT (provider, issuer, subject) DO NOTHING;

            SELECT user_id INTO v_user_id
            FROM app.external_identities
            WHERE provider = rec.provider
              AND issuer = rec.issuer
              AND subject = rec.subject;
        END IF;

        IF NOT EXISTS (
            SELECT 1
            FROM app.tenant_memberships m
            JOIN app.tenants t USING (tenant_id)
            WHERE m.user_id = v_user_id
              AND t.kind = 'personal'
        ) THEN
            v_tenant_id := rec.default_tenant_id;
            INSERT INTO app.tenants (tenant_id, display_name, kind)
            VALUES (v_tenant_id, rec.display_name || '''s workspace', 'personal')
            ON CONFLICT (tenant_id) DO NOTHING;

            INSERT INTO app.tenant_memberships (tenant_id, user_id, role)
            VALUES (v_tenant_id, v_user_id, 'owner')
            ON CONFLICT (tenant_id, user_id) DO NOTHING;
        END IF;

        SELECT role INTO v_prev_role
        FROM app.platform_roles
        WHERE user_id = v_user_id;

        IF v_prev_role IS DISTINCT FROM 'administrator' THEN
            INSERT INTO app.platform_roles (user_id, role, updated_at)
            VALUES (v_user_id, 'administrator', now())
            ON CONFLICT (user_id) DO UPDATE
                SET role = EXCLUDED.role,
                    updated_at = now();

            INSERT INTO app.platform_role_events (user_id, actor, previous_role, role)
            VALUES (v_user_id, 'installation-owner', v_prev_role, 'administrator');
        END IF;
    END LOOP;
END $$;
