package org.apache.ofbiz.loans;

import com.mlcpu.loans.api.ports.DisbursementPayment;
import com.mlcpu.loans.api.ports.Ledger;
import java.sql.Timestamp;
import java.util.HashMap;
import java.util.Map;

import org.apache.ofbiz.entity.GenericEntityException;
import org.apache.ofbiz.entity.GenericValue;
import org.apache.ofbiz.entity.util.EntityQuery;
import org.apache.ofbiz.service.GenericServiceException;
import org.apache.ofbiz.service.ServiceUtil;

/**
 * The SME's reference for the Ledger hole. A currency is postable for an organization when it is the organization's base
 * currency or has a conversion to it valid on the business date (OFBiz converts every GL entry to the base currency; D-11). The disbursement is an
 * OFBiz payment of type DISBURSEMENT from the organization to the borrower, overriding its GL account with the loan product's
 * receivable, then set to PMNT_SENT so that OFBiz posts it (OFB-06).
 */
public final class OfbizLedger implements Ledger {

    private final LoansPortContext context;

    public OfbizLedger(LoansPortContext context) {
        this.context = context;
    }

    @Override
    public boolean postable(String currencyCode, String organizationPartyId) {
        try {
            GenericValue preference = EntityQuery.use(context.delegator()).from("PartyAcctgPreference")
                    .where("partyId", organizationPartyId).queryOne();
            if (preference == null || preference.getString("baseCurrencyUomId") == null) {
                return false;
            }
            String base = preference.getString("baseCurrencyUomId");
            if (base.equals(currencyCode)) {
                return true;
            }
            // a rate valid on the business date, as OFBiz converts each posting on its own date
            return EntityQuery.use(context.delegator()).from("UomConversionDated").where("uomId", currencyCode, "uomIdTo", base)
                    .filterByDate(Timestamp.valueOf(context.today().atStartOfDay())).queryFirst() != null;
        } catch (GenericEntityException e) {
            throw new IllegalStateException(e.getMessage(), e);
        }
    }

    @Override
    public String disbursementPayment(DisbursementPayment payment) {
        Map<String, Object> in = new HashMap<>();
        in.put("paymentTypeId", "DISBURSEMENT");
        in.put("paymentMethodId", payment.paymentMethodId());
        in.put("partyIdFrom", payment.fromPartyId());
        in.put("partyIdTo", payment.toPartyId());
        in.put("amount", payment.amount());
        in.put("currencyUomId", payment.currencyCode());
        in.put("effectiveDate", Timestamp.valueOf(payment.date().atStartOfDay()));
        in.put("statusId", "PMNT_NOT_PAID");
        in.put("overrideGlAccountId", product(payment.loanProductId()).getString("receivableGlAccountId"));
        in.put("comments", "Disbursement of loan " + payment.loanId());
        String paymentId = (String) call("createPayment", in).get("paymentId");
        call("setPaymentStatus", Map.of("paymentId", paymentId, "statusId", "PMNT_SENT"));
        return paymentId;
    }

    private GenericValue product(String loanProductId) {
        try {
            GenericValue product = EntityQuery.use(context.delegator()).from("LoanProduct").where("loanProductId", loanProductId).queryOne();
            if (product == null) {
                throw new IllegalStateException("unknown loan product " + loanProductId);
            }
            return product;
        } catch (GenericEntityException e) {
            throw new IllegalStateException(e.getMessage(), e);
        }
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
