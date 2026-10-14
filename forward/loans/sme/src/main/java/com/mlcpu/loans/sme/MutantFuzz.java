package com.mlcpu.loans.sme;

import com.mlcpu.loans.api.Calculation;
import com.mlcpu.loans.api.CurrencyTerms;
import com.mlcpu.loans.api.Frequency;
import com.mlcpu.loans.api.InterestMethod;
import com.mlcpu.loans.api.MonthDays;
import com.mlcpu.loans.api.ScheduleRequest;
import com.mlcpu.loans.api.ScheduleService;
import com.mlcpu.loans.api.YearDays;
import com.mlcpu.loans.application.LoansApplication;
import com.mlcpu.loans.schedule.AmortizationMutants.Mutant;
import com.mlcpu.loans.terms.TermsReview;
import java.math.BigDecimal;
import java.math.RoundingMode;
import java.time.LocalDate;
import java.util.SplittableRandom;
import java.util.concurrent.atomic.AtomicInteger;
import java.util.stream.IntStream;

/**
 * Searches random loan requests (inside the legacy envelope) for one where a schedule mutant's outcome differs from the
 * reference's: a rule the corpus never exercises is either reachable (the request found becomes a probe, its expected values from
 * the faithful program) or untestable (FE 7.2: not carried forward). Seeded, so a search can be repeated.
 *
 * <p>Usage: MutantFuzz &lt;mutant, e.g. AMZ_13&gt; &lt;requests&gt; [seed 42]
 */
public final class MutantFuzz {

    public static void main(String[] args) {
        Mutant m = Mutant.valueOf(args[0]);
        int n = Integer.parseInt(args[1]);
        long seed = args.length > 2 ? Long.parseLong(args[2]) : 42;
        ScheduleService reference = LoansApplication.quotes();
        TermsReview terms = new TermsReview();
        AtomicInteger found = new AtomicInteger();
        AtomicInteger scheduled = new AtomicInteger();
        IntStream.range(0, n).parallel().forEach(i -> {
            if (found.get() >= 5) return;
            ScheduleRequest q = random(new SplittableRandom(seed * 1_000_003L + i), i);
            var expected = PoolRunner.toJson(reference.schedule(q));
            if ("Scheduled".equals(expected.path("@type").asText())) scheduled.incrementAndGet();
            var got = PoolRunner.toJson(SchedulePool.mutated(q, terms, m));
            if (!got.equals(expected) && found.incrementAndGet() <= 5) {
                System.out.println("DIFFERS: " + q);
            }
        });
        System.out.printf("%s: %d random requests (%d scheduled), %d differ%n", m.rule(), n, scheduled.get(), found.get());
    }

    static ScheduleRequest random(SplittableRandom r, int i) {
        Frequency f = Frequency.values()[r.nextInt(4)];
        int installments = 1 + r.nextInt(r.nextBoolean() ? 6 : 60);
        int every = 1 + r.nextInt(f == Frequency.DAYS ? 60 : f == Frequency.WEEKS ? 4 : f == Frequency.MONTHS ? 3 : 2);
        LocalDate start = LocalDate.of(2000 + r.nextInt(40), 1 + r.nextInt(12), 1 + r.nextInt(28)).plusDays(r.nextInt(4));
        LocalDate disbursed = r.nextInt(3) == 0 ? start : start.plusDays(r.nextInt(40));
        int decimals = r.nextInt(5);
        BigDecimal principal = BigDecimal.valueOf(1 + r.nextInt(r.nextBoolean() ? 50 : 1_000_000)).movePointLeft(r.nextInt(decimals + 1));
        BigDecimal rate = BigDecimal.valueOf(r.nextInt(100_000_000)).movePointLeft(6 + r.nextInt(3));
        boolean dp = r.nextInt(5) == 0;
        BigDecimal dpPercent = dp ? BigDecimal.valueOf(r.nextInt(9500)).movePointLeft(2) : null;
        Integer fixed = r.nextInt(6) == 0 ? (installments - 1) * every + 1 + r.nextInt(2 * every) : null;
        RoundingMode mode = RoundingMode.values()[r.nextInt(7)];
        return new ScheduleRequest(String.format("%016d", i), start, new CurrencyTerms("CUR", decimals, null), principal, disbursed, installments, every, f,
                rate, dpPercent, dp, MonthDays.values()[r.nextInt(2)], YearDays.values()[r.nextInt(4)], fixed, InterestMethod.DECLINING_BALANCE, false,
                new Calculation(1 + r.nextInt(12), mode));
    }
}
