package com.mlcpu.cas.application;

import com.mlcpu.cas.api.CasPorts;
import com.mlcpu.cas.api.CustomerAccountService;

/** The service's entry point (SPEC.md section 2). */
public final class CasApplication {
    private CasApplication() {}

    public static CustomerAccountService create(CasPorts ports) {
        return new ApplicationService(ports);
    }
}
