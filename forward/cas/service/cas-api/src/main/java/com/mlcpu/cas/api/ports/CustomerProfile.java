package com.mlcpu.cas.api.ports;

import java.math.BigDecimal;
import java.util.List;

/**
 * A customer's profile (record type "A"). {@code accountBalance} and the 12 {@code monthlyAmounts} have 2 decimals and at most
 * 7 integer digits and are never negative; {@code ordersYtd} has at most 5 digits.
 */
public record CustomerProfile(String customerId, String name, BigDecimal accountBalance, int ordersYtd, String address, String city,
                              String state, String country, List<BigDecimal> monthlyAmounts, String occupation, String notes,
                              String data1, String data2) implements MasterRecord {
    public static final String RECORD_TYPE = "A";

    public CustomerProfile {
        monthlyAmounts = List.copyOf(monthlyAmounts);
        if (monthlyAmounts.size() != 12) throw new IllegalArgumentException("a profile has 12 monthly amounts");
    }

    @Override
    public String recordType() {
        return RECORD_TYPE;
    }
}
