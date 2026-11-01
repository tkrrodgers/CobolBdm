package com.acme.util;

import java.time.LocalDate;

public final class Calendar {
    private Calendar() {
    }

    public static LocalDate nextBusinessDay(LocalDate day) {
        return day.plusDays(1);
    }
}
