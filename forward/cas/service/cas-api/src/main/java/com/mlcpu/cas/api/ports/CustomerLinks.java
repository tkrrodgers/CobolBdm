package com.mlcpu.cas.api.ports;

import java.util.Optional;

/** Customer links by SAM customer id. */
public interface CustomerLinks {
    Optional<CustomerLink> find(String customerId);
}
