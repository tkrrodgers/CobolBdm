package com.mlcpu.loans.api;

import java.math.RoundingMode;

/** The calculation context of a loan: significant digits and rounding mode of every intermediate result (UNNECESSARY is rejected). */
public record Calculation(int precision, RoundingMode roundingMode) {
}
