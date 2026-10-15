package org.apache.ofbiz.loans;

import com.mlcpu.loans.api.ports.AccountTransaction;
import com.mlcpu.loans.api.ports.FinancialAccounts;
import com.mlcpu.loans.api.ports.LoanAccount;
import java.sql.Timestamp;
import java.util.HashMap;
import java.util.Map;

import org.apache.ofbiz.service.GenericServiceException;
import org.apache.ofbiz.service.ServiceUtil;

/**
 * The SME's reference for the FinancialAccounts hole: a loan's account is an OFBiz FinAccount of type LOAN_ACCOUNT (OFB-02);
 * its transactions are FinAccountTrans rows, created with createFinAccountTrans and without a GL account, so OFBiz does not post
 * them (OFB-04); a cancelled one is set to FINACT_TRNS_CANCELED (OFB-07).
 */
public final class OfbizFinancialAccounts implements FinancialAccounts {

    private final LoansPortContext context;

    public OfbizFinancialAccounts(LoansPortContext context) {
        this.context = context;
    }

    @Override
    public String open(LoanAccount account) {
        Map<String, Object> in = new HashMap<>();
        in.put("finAccountTypeId", "LOAN_ACCOUNT");
        in.put("finAccountName", "Loan " + account.loanId());
        in.put("ownerPartyId", account.ownerPartyId());
        in.put("organizationPartyId", account.organizationPartyId());
        in.put("currencyUomId", account.currencyCode());
        in.put("statusId", "FNACT_ACTIVE");
        return (String) call("createFinAccount", in).get("finAccountId");
    }

    @Override
    public String post(AccountTransaction transaction) {
        Map<String, Object> in = new HashMap<>();
        in.put("finAccountId", transaction.accountId());
        in.put("finAccountTransTypeId", transaction.type().name());
        in.put("amount", transaction.amount());
        in.put("transactionDate", Timestamp.valueOf(transaction.date().atStartOfDay()));
        in.put("statusId", "FINACT_TRNS_APPROVED");
        if (transaction.paymentId() != null) {
            in.put("paymentId", transaction.paymentId());
        }
        if (transaction.invoiceId() != null) {
            in.put("comments", "invoice " + transaction.invoiceId());
        }
        return (String) call("createFinAccountTrans", in).get("finAccountTransId");
    }

    @Override
    public void cancel(String transactionId) {
        call("setFinAccountTransStatus", Map.of("finAccountTransId", transactionId, "statusId", "FINACT_TRNS_CANCELED"));
    }

    private Map<String, Object> call(String service, Map<String, Object> in) {
        Map<String, Object> request = new HashMap<>(in);
        request.put("userLogin", context.userLogin());
        try {
            Map<String, Object> result = context.dispatcher().runSync(service, request);
            if (ServiceUtil.isError(result) || ServiceUtil.isFailure(result)) {
                throw new IllegalStateException(service + ": " + ServiceUtil.getErrorMessage(result));
            }
            return result;
        } catch (GenericServiceException e) {
            throw new IllegalStateException(service + ": " + e.getMessage(), e);
        }
    }
}
