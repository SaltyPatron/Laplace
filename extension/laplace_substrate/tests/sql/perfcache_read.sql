-- Mapped floor identities render from the floor.
BEGIN;
SELECT realize.render_text_batch(ARRAY[
           public.blake3_send(public.laplace_text_id('A')), public.blake3_send(public.laplace_text_id(' ')), public.blake3_send(public.laplace_text_id('狼'))])
       = ARRAY['A',' ','狼'] AS floor_without_database_closure;
ROLLBACK;
