package demo.posting;

import java.math.BigDecimal;
import java.math.RoundingMode;
import java.time.LocalDate;
import java.util.List;
import java.util.Map;
import java.util.Optional;
import java.util.function.Function;
import java.util.stream.Collectors;

/** A modern sample: the same capability as PostTranStep, written as idiomatic Java. */
public final class TransactionPosting {
    public enum Outcome { POSTED, REJECTED }

    public record Transaction(String accountId, BigDecimal amount, LocalDate postedOn) {
    }

    public record Account(String id, BigDecimal balance) {
        Account credit(BigDecimal amount) {
            return new Account(id, balance.add(amount).setScale(2, RoundingMode.HALF_EVEN));
        }
    }

    public record Result(Map<String, Account> accounts, long posted, long rejected) {
    }

    public interface AccountRepository {
        Optional<Account> find(String id);
    }

    private final AccountRepository accounts;

    public TransactionPosting(AccountRepository accounts) {
        this.accounts = accounts;
    }

    public Result post(List<Transaction> transactions) {
        Map<Boolean, List<Transaction>> split = transactions.stream()
            .collect(Collectors.partitioningBy(t -> accounts.find(t.accountId()).isPresent()));
        Map<String, Account> updated = split.get(true).stream()
            .collect(Collectors.groupingBy(Transaction::accountId)).entrySet().stream()
            .map(e -> applyAll(accounts.find(e.getKey()).orElseThrow(), e.getValue()))
            .collect(Collectors.toMap(Account::id, Function.identity()));
        return new Result(updated, split.get(true).size(), split.get(false).size());
    }

    public Outcome classify(Transaction t) {
        return accounts.find(t.accountId()).isPresent() ? Outcome.POSTED : Outcome.REJECTED;
    }

    private static Account applyAll(Account account, List<Transaction> transactions) {
        Account current = account;
        for (Transaction t : transactions) {
            current = current.credit(t.amount());
        }
        return current;
    }
}
