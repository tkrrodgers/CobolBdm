package com.mlcpu.cas.api.ports;

/** A card: its number, the CardDemo customer and the account it belongs to (the card cross-reference). */
public record CardRecord(String cardNumber, long customerId, long accountId) {}
