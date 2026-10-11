package com.mlcpu.cas.api.ports;

import java.util.Optional;

/** Disclosure group rates by group, type and category. */
public interface DisclosureRates {
    Optional<DisclosureRate> find(String groupId, String typeCode, int categoryCode);
}
