package com.mlcpu.cas.api.ports;

/** A contact of a customer (any record type but "A"). */
public record CustomerContact(String customerId, String recordType, String name, String description, String contactInfo,
                              String data3, String data4, String data5, String data6) implements MasterRecord {}
