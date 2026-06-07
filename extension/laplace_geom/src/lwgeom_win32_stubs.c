#include "liblwgeom_internal.h"
#include "lwgeodetic.h"

LWGEOM *lwgeom_intersection_prec(const LWGEOM *geom1, const LWGEOM *geom2, double gridSize)
{
    (void)geom1; (void)geom2; (void)gridSize;
    lwerror("lwgeom_intersection_prec: GEOS support not built into this module");
    return NULL;
}

LWGEOM *lwgeom_offsetcurve(const LWGEOM *geom, double size, int quadsegs, int joinStyle, double mitreLimit)
{
    (void)geom; (void)size; (void)quadsegs; (void)joinStyle; (void)mitreLimit;
    lwerror("lwgeom_offsetcurve: GEOS support not built into this module");
    return NULL;
}

void spheroid_init(SPHEROID *s, double a, double b)
{
    (void)s; (void)a; (void)b;
    lwerror("spheroid_init: PROJ support not built into this module");
}

double spheroid_distance(const GEOGRAPHIC_POINT *a, const GEOGRAPHIC_POINT *b, const SPHEROID *spheroid)
{
    (void)a; (void)b; (void)spheroid;
    lwerror("spheroid_distance: PROJ support not built into this module");
    return 0.0;
}

double spheroid_direction(const GEOGRAPHIC_POINT *r, const GEOGRAPHIC_POINT *s, const SPHEROID *spheroid)
{
    (void)r; (void)s; (void)spheroid;
    lwerror("spheroid_direction: PROJ support not built into this module");
    return 0.0;
}

int spheroid_project(const GEOGRAPHIC_POINT *r, const SPHEROID *spheroid, double distance, double azimuth, GEOGRAPHIC_POINT *g)
{
    (void)r; (void)spheroid; (void)distance; (void)azimuth; (void)g;
    lwerror("spheroid_project: PROJ support not built into this module");
    return LW_FAILURE;
}

double lwgeom_area_spheroid(const LWGEOM *lwgeom, const SPHEROID *spheroid)
{
    (void)lwgeom; (void)spheroid;
    lwerror("lwgeom_area_spheroid: PROJ support not built into this module");
    return 0.0;
}
