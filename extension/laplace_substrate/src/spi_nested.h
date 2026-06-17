#ifndef LAPLACE_SPI_NESTED_H
#define LAPLACE_SPI_NESTED_H

#include "executor/spi.h"

static inline int
laplace_spi_connect(bool *need_finish)
{
    int rc = SPI_connect();

    if (need_finish)
        *need_finish = false;
    if (rc == SPI_OK_CONNECT)
    {
        if (need_finish)
            *need_finish = true;
        return SPI_OK_CONNECT;
    }
    if (rc == SPI_ERROR_CONNECT)
        return SPI_OK_CONNECT;
    return rc;
}

static inline void
laplace_spi_finish(bool need_finish)
{
    if (need_finish)
        SPI_finish();
}

#endif 
