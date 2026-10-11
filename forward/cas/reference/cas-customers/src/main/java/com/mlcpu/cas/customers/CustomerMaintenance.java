package com.mlcpu.cas.customers;

import com.mlcpu.cas.api.customers.ChangeRejection;
import com.mlcpu.cas.api.customers.ChangeTotals;
import com.mlcpu.cas.api.customers.CustomerChange;
import com.mlcpu.cas.api.customers.CustomerChangeError;
import com.mlcpu.cas.api.customers.CustomerChangeSummary;
import com.mlcpu.cas.api.ports.CustomerContact;
import com.mlcpu.cas.api.ports.CustomerMaster;
import com.mlcpu.cas.api.ports.CustomerProfile;
import com.mlcpu.cas.api.ports.MasterRecord;
import java.math.BigDecimal;
import java.util.ArrayList;
import java.util.Collections;
import java.util.List;
import java.util.Optional;

/** Customer master maintenance (rules CUST): a batch of changes applied in order. */
public final class CustomerMaintenance {
    private static final List<String> TYPES = List.of("ADD", "DELETE", "UPDATE");

    private final CustomerMaster master;

    public CustomerMaintenance(CustomerMaster master) {
        this.master = master;
    }

    public CustomerChangeSummary apply(List<CustomerChange> changes) {
        int[] requested = new int[3];
        int[] processed = new int[3];
        List<CustomerChangeError> errors = new ArrayList<>();
        String previousKey = null;
        int read = 0;
        for (int i = 0; i < changes.size(); i++) {
            CustomerChange change = changes.get(i);
            if (change.code() != null && change.code().startsWith("*")) continue;
            read++;
            String key = change.customerKey();
            boolean outOfSequence = previousKey != null && key.compareTo(previousKey) < 0;
            previousKey = key;
            if (outOfSequence) {
                errors.add(new CustomerChangeError(i, key, ChangeRejection.OUT_OF_SEQUENCE));
                continue;
            }
            int type = TYPES.indexOf(change.code());
            if (type < 0) {
                errors.add(new CustomerChangeError(i, key, ChangeRejection.INVALID_CODE));
                continue;
            }
            requested[type]++;
            Optional<ChangeRejection> rejection = switch (TYPES.get(type)) {
                case "ADD" -> add(key);
                case "DELETE" -> delete(key);
                default -> update(key, change);
            };
            if (rejection.isPresent()) errors.add(new CustomerChangeError(i, key, rejection.get()));
            else processed[type]++;
        }
        List<ChangeTotals> totals = new ArrayList<>();
        for (int t = 0; t < 3; t++) totals.add(new ChangeTotals(TYPES.get(t), requested[t], processed[t], requested[t] - processed[t]));
        return new CustomerChangeSummary(totals, errors, read);
    }

    /** CUST-05. */
    private Optional<ChangeRejection> add(String key) {
        if (master.find(key).isPresent()) return Optional.of(ChangeRejection.DUPLICATE_KEY);
        String id = key.substring(0, 5);
        String type = key.substring(5);
        BigDecimal zero = BigDecimal.ZERO.setScale(2);
        master.save(CustomerProfile.RECORD_TYPE.equals(type)
                ? new CustomerProfile(id, "", zero, 0, "", "", "", "", Collections.nCopies(12, zero), "", "", "", "")
                : new CustomerContact(id, type, "", "", "", "", "", "", ""));
        return Optional.empty();
    }

    /** CUST-06. */
    private Optional<ChangeRejection> delete(String key) {
        if (master.find(key).isEmpty()) return Optional.of(ChangeRejection.NO_MATCHING_KEY);
        master.delete(key);
        return Optional.empty();
    }

    /** CUST-07. */
    private Optional<ChangeRejection> update(String key, CustomerChange change) {
        Optional<MasterRecord> record = master.find(key);
        if (record.isEmpty()) return Optional.of(ChangeRejection.NO_MATCHING_KEY);
        return switch (UpdatePolicy.apply(record.get(), change)) {
            case UpdatePolicy.Updated u -> {
                master.save(u.record());
                yield Optional.empty();
            }
            case UpdatePolicy.Refused r -> Optional.of(r.reason());
        };
    }
}
