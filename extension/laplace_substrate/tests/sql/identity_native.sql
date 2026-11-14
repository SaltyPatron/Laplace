CREATE EXTENSION IF NOT EXISTS postgis;
CREATE EXTENSION IF NOT EXISTS laplace_geom;
CREATE EXTENSION IF NOT EXISTS laplace_substrate CASCADE;

-- A property's value is the Native compose of its parts' Native text IDs.
SELECT laplace.property_value_id('language', 'eng')
         = public.blake3_send(public.laplace_compose(ARRAY[public.laplace_text_id('language'), public.laplace_text_id('eng')])) AS property_value_composes;

-- Relation and entity-type labels are the Native text IDs of the labels.
SELECT bool_and(r.type_id = public.blake3_send(public.laplace_text_id(r.canonical))) AS relation_ids_are_native_text
FROM laplace.relation_registry() r;
SELECT bool_and(e.type_id = public.blake3_send(public.laplace_text_id(e.canonical))) AS entity_type_ids_are_native_text
FROM laplace.entity_type_registry() e;
