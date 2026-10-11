package com.mlcpu.cas.customers;

import com.mlcpu.cas.api.customers.ChangeRejection;
import com.mlcpu.cas.api.customers.CustomerChange;
import com.mlcpu.cas.api.ports.CustomerContact;
import com.mlcpu.cas.api.ports.CustomerProfile;
import com.mlcpu.cas.api.ports.MasterRecord;
import java.math.BigDecimal;
import java.util.Optional;

/** The field update policy of an UPDATE change (rules UPD). */
final class UpdatePolicy {
    private UpdatePolicy() {}

    /** The record after the change, or why the change is refused. */
    sealed interface Result permits Updated, Refused {}

    record Updated(MasterRecord record) implements Result {}

    record Refused(ChangeRejection reason) implements Result {}

    static Result apply(MasterRecord record, CustomerChange change) {
        String field = change.fieldName();
        boolean numeric = "BALANCE".equals(field) || "ORDERS".equals(field);
        Optional<BigDecimal> amount = numeric ? UpdateAmount.parse(change.value()) : Optional.empty();
        ChangeRejection rejection = numeric && amount.isEmpty() ? ChangeRejection.NOT_NUMERIC : null;
        boolean add = "ADD".equals(change.action());
        if (!add && !"REPLACE".equals(change.action())) rejection = ChangeRejection.INVALID_ACTION;
        if (rejection != null) return new Refused(rejection);

        if ("NAME".equals(field)) return new Updated(renamed(record, name(change.value())));
        if (!numeric || !(record instanceof CustomerProfile p)) return new Refused(ChangeRejection.INVALID_FIELD);
        BigDecimal v = amount.orElseThrow();
        if ("BALANCE".equals(field))
            return new Updated(withBalance(p, UnsignedField.BALANCE.store(add ? p.accountBalance().add(v) : v)));
        int orders = UnsignedField.ORDERS.store(add ? BigDecimal.valueOf(p.ordersYtd()).add(v) : v).intValueExact();
        return new Updated(withOrders(p, orders));
    }

    /** UPD-07: the first 17 characters, without trailing blanks. */
    static String name(String value) {
        String v = value == null ? "" : value;
        return v.substring(0, Math.min(17, v.length())).stripTrailing();
    }

    private static MasterRecord renamed(MasterRecord record, String name) {
        return switch (record) {
            case CustomerProfile p -> new CustomerProfile(p.customerId(), name, p.accountBalance(), p.ordersYtd(), p.address(), p.city(),
                    p.state(), p.country(), p.monthlyAmounts(), p.occupation(), p.notes(), p.data1(), p.data2());
            case CustomerContact c -> new CustomerContact(c.customerId(), c.recordType(), name, c.description(), c.contactInfo(), c.data3(),
                    c.data4(), c.data5(), c.data6());
        };
    }

    private static CustomerProfile withBalance(CustomerProfile p, BigDecimal balance) {
        return new CustomerProfile(p.customerId(), p.name(), balance, p.ordersYtd(), p.address(), p.city(), p.state(), p.country(),
                p.monthlyAmounts(), p.occupation(), p.notes(), p.data1(), p.data2());
    }

    private static CustomerProfile withOrders(CustomerProfile p, int orders) {
        return new CustomerProfile(p.customerId(), p.name(), p.accountBalance(), orders, p.address(), p.city(), p.state(), p.country(),
                p.monthlyAmounts(), p.occupation(), p.notes(), p.data1(), p.data2());
    }
}
