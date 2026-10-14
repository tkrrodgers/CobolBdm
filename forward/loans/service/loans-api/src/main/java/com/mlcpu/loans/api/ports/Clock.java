package com.mlcpu.loans.api.ports;

import java.time.LocalDate;

/** The business date. */
public interface Clock {

    LocalDate today();
}
