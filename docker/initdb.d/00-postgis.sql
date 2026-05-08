-- PostGIS is enabled additively for naturally low-dim modalities (audio
-- waveforms 2D, spectra 2D, spectrograms 3D, stereo 3D). The substrate's
-- own GEOMETRY4D type family (POINT4D / LINESTRING4D / POLYGON4D / BOX4D)
-- is registered separately by the laplace_pg extension and is independent
-- of PostGIS — substrate invariant 5 (CLAUDE.md): GEOMETRY4D is a parallel
-- type family, NOT PostGIS GEOMETRYZM with M repurposed.
CREATE EXTENSION IF NOT EXISTS postgis;
