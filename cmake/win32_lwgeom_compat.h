#ifndef LAPLACE_WIN32_LWGEOM_COMPAT_H
#define LAPLACE_WIN32_LWGEOM_COMPAT_H

#ifdef _WIN32

#ifndef _USE_MATH_DEFINES
#define _USE_MATH_DEFINES
#endif
#include <math.h>

#ifndef YY_NO_UNISTD_H
#define YY_NO_UNISTD_H 1
#endif

#include <string.h>
#ifndef strcasecmp
#define strcasecmp  _stricmp
#endif
#ifndef strncasecmp
#define strncasecmp _strnicmp
#endif

#endif

#endif
