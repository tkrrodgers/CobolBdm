package com.mlcpu.forwardexport;

import static com.mlcpu.forwardexport.Jw.obj;
import static com.mlcpu.forwardexport.Jw.text;

import com.mlcpu.carddemo.data.RecordLayouts;
import com.mlcpu.carddemo.domain.Account;
import com.mlcpu.carddemo.domain.CategoryBalance;
import com.mlcpu.carddemo.domain.Reference;
import com.mlcpu.carddemo.domain.Transaction;
import com.mlcpu.rt.StepEquivalence;
import java.util.ArrayList;
import java.util.List;
import java.util.Map;
import java.util.function.Function;

/** CardDemo datasets decoded into the Customer Account Service's JSON (cas-api port records and commands). */
public final class CardDemoJson {
    private CardDemoJson() {}

    public static <T> List<Object> each(byte[] file, int lrecl, Function<byte[], T> decode, Function<T, Object> json) {
        List<Object> out = new ArrayList<>();
        if (file == null) return out;
        for (byte[] r : StepEquivalence.records(file, lrecl)) out.add(json.apply(decode.apply(r)));
        return out;
    }

    public static Map<String, Object> account(Account a) {
        return obj("accountId", a.id(), "activeStatus", text(a.activeStatus()), "currentBalance", a.currentBalance(), "creditLimit", a.creditLimit(),
                "cashCreditLimit", a.cashCreditLimit(), "openDate", Jw.date(a.openDate()), "expirationDate", Jw.date(a.expirationDate()),
                "reissueDate", Jw.date(a.reissueDate()), "currentCycleCredit", a.currentCycleCredit(), "currentCycleDebit", a.currentCycleDebit(),
                "addressZip", text(a.addressZip()), "groupId", text(a.groupId()), "lastClosedCycle", null);
    }

    public static List<Object> accounts(byte[] f) {
        return each(f, RecordLayouts.ACCOUNT, RecordLayouts::account, CardDemoJson::account);
    }

    public static Map<String, Object> card(Reference.CardCrossReference x) {
        return obj("cardNumber", text(x.cardNumber()), "customerId", x.customerId(), "accountId", x.accountId());
    }

    public static List<Object> cards(byte[] f) {
        return each(f, RecordLayouts.CROSS_REFERENCE, RecordLayouts::crossReference, CardDemoJson::card);
    }

    public static Map<String, Object> categoryBalance(CategoryBalance b) {
        return obj("accountId", b.key().accountId(), "typeCode", text(b.key().typeCode()), "categoryCode", b.key().categoryCode(), "balance", b.balance());
    }

    public static List<Object> categoryBalances(byte[] f) {
        return each(f, RecordLayouts.CATEGORY_BALANCE, RecordLayouts::categoryBalance, CardDemoJson::categoryBalance);
    }

    public static List<Object> disclosureRates(byte[] f) {
        return each(f, RecordLayouts.DISCLOSURE_GROUP, RecordLayouts::disclosureGroup, g -> obj("groupId", text(g.groupId()), "typeCode", text(g.typeCode()),
                "categoryCode", g.categoryCode(), "annualRatePercent", g.interestRate()));
    }

    public static List<Object> transactionTypes(byte[] f) {
        return each(f, RecordLayouts.TRANSACTION_TYPE, RecordLayouts::transactionType, t -> obj("typeCode", text(t.code()), "description", text(t.description())));
    }

    public static List<Object> transactionCategories(byte[] f) {
        return each(f, RecordLayouts.TRANSACTION_CATEGORY, RecordLayouts::transactionCategory,
                t -> obj("typeCode", text(t.typeCode()), "categoryCode", t.categoryCode(), "description", text(t.description())));
    }

    public static Map<String, Object> input(Transaction t) {
        return obj("transactionId", text(t.id()), "typeCode", text(t.typeCode()), "categoryCode", t.categoryCode(), "source", text(t.source()),
                "description", text(t.description()), "amount", t.amount(), "merchantId", t.merchantId(), "merchantName", text(t.merchantName()),
                "merchantCity", text(t.merchantCity()), "merchantZip", text(t.merchantZip()), "cardNumber", text(t.cardNumber()),
                "originTimestamp", Jw.timestamp(t.originTimestamp()));
    }

    public static List<Object> inputs(byte[] f) {
        return each(f, RecordLayouts.TRANSACTION, RecordLayouts::transaction, CardDemoJson::input);
    }

    /** A ledger entry: the transaction's fields with its account (from {@code accountOf}) and its processed timestamp. */
    public static Map<String, Object> ledgerEntry(Transaction t, long accountId, String interestCycle) {
        Map<String, Object> m = obj("transactionId", text(t.id()), "accountId", accountId, "cardNumber", text(t.cardNumber()), "typeCode", text(t.typeCode()),
                "categoryCode", t.categoryCode(), "source", text(t.source()), "description", text(t.description()), "amount", t.amount(),
                "merchantId", t.merchantId(), "merchantName", text(t.merchantName()), "merchantCity", text(t.merchantCity()),
                "merchantZip", text(t.merchantZip()), "originTimestamp", Jw.timestamp(t.originTimestamp()),
                "processedTimestamp", Jw.timestamp(t.processedTimestamp()), "interestCycle", interestCycle);
        return m;
    }

    public static List<Object> ledger(byte[] f, Map<String, Long> accountOfCard) {
        return each(f, RecordLayouts.TRANSACTION, RecordLayouts::transaction,
                t -> ledgerEntry(t, accountOfCard.getOrDefault(text(t.cardNumber()), 0L), null));
    }

    public static Map<String, Long> accountOfCard(byte[] xref) {
        Map<String, Long> m = new java.util.HashMap<>();
        if (xref != null)
            for (byte[] r : StepEquivalence.records(xref, RecordLayouts.CROSS_REFERENCE)) {
                var x = RecordLayouts.crossReference(r);
                m.put(text(x.cardNumber()), x.accountId());
            }
        return m;
    }

    public static String sysout(StepEquivalence.Outcome o) {
        byte[] s = o.files().get("sysout/SYSOUT");
        return s == null ? "" : new String(s, java.nio.charset.StandardCharsets.ISO_8859_1);
    }
}
