package org.apache.ofbiz.loans;

import com.mlcpu.loans.api.ports.Billing;
import com.mlcpu.loans.api.ports.LoanInvoice;
import java.math.BigDecimal;
import java.sql.Timestamp;
import java.util.HashMap;
import java.util.Map;

import org.apache.ofbiz.entity.GenericEntityException;
import org.apache.ofbiz.entity.GenericValue;
import org.apache.ofbiz.entity.util.EntityQuery;
import org.apache.ofbiz.service.GenericServiceException;
import org.apache.ofbiz.service.ServiceUtil;

/**
 * The SME's reference for the Billing hole (OFB-05): a loan line's invoice is a SALES_INVOICE from the organization to the
 * borrower, dated on the line's due date, with a LOAN_PRINCIPAL item for the principal and an INV_INTRST_CHRG item for the
 * interest (none for an amount of 0), each overriding its GL account with the loan product's (receivable, interest income), then
 * set to INVOICE_READY so that OFBiz posts it.
 */
public final class OfbizBilling implements Billing {

    private final LoansPortContext context;

    public OfbizBilling(LoansPortContext context) {
        this.context = context;
    }

    @Override
    public String invoice(LoanInvoice invoice) {
        GenericValue product = product(invoice.loanProductId());
        Timestamp date = Timestamp.valueOf(invoice.invoiceDate().atStartOfDay());
        Map<String, Object> in = new HashMap<>();
        in.put("invoiceTypeId", "SALES_INVOICE");
        in.put("partyIdFrom", invoice.fromPartyId());
        in.put("partyId", invoice.toPartyId());
        in.put("currencyUomId", invoice.currencyCode());
        in.put("invoiceDate", date);
        in.put("dueDate", date);
        in.put("statusId", "INVOICE_IN_PROCESS");
        in.put("referenceNumber", invoice.loanId());
        in.put("description", "Loan " + invoice.loanId() + " line " + invoice.lineNumber());
        String invoiceId = (String) call("createInvoice", in).get("invoiceId");
        item(invoiceId, "LOAN_PRINCIPAL", invoice.principal(), product.getString("receivableGlAccountId"), "Principal");
        item(invoiceId, "INV_INTRST_CHRG", invoice.interest(), product.getString("interestIncomeGlAccountId"), "Interest");
        call("setInvoiceStatus", Map.of("invoiceId", invoiceId, "statusId", "INVOICE_READY"));
        return invoiceId;
    }

    private void item(String invoiceId, String type, BigDecimal amount, String glAccountId, String description) {
        if (amount == null || amount.signum() == 0) {
            return;
        }
        Map<String, Object> in = new HashMap<>();
        in.put("invoiceId", invoiceId);
        in.put("invoiceItemTypeId", type);
        in.put("quantity", BigDecimal.ONE);
        in.put("amount", amount);
        in.put("overrideGlAccountId", glAccountId);
        in.put("description", description);
        call("createInvoiceItem", in);
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
