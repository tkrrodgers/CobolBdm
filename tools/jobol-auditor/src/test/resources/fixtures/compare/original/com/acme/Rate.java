package com.acme;

import java.math.BigDecimal;

public interface Rate {
    BigDecimal apply(BigDecimal amount);
}
