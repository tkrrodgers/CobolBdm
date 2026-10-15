# OFBiz facade: the services and entities the plugin may use

Generated from the pinned OFBiz source. Call services with `dispatcher.runSync(name, context)` and check the result with `ServiceUtil.isError`; read entities with `EntityQuery.use(delegator).from(entity).where(...).queryOne()/queryList()`.

## Services

### createFinAccount (simple, applications/accounting/servicedef/services_finaccount.xml)
Create a new Financial Account. If no finAccountId is provided, an auto-sequenced one will be used.
- auto-attributes: entity FinAccount, include pk, mode INOUT, optional true
- auto-attributes: entity FinAccount, include nonpk, mode IN, optional true, exclude actualBalance availableBalance

### createFinAccountTrans (simple, applications/accounting/servicedef/services_finaccount.xml)
Create a new Financial Account Transaction. Will use current timestamp for entryDate and trasanctionDate if none is provided.
- auto-attributes: entity FinAccountTrans, include pk, mode OUT, optional false
- auto-attributes: entity FinAccountTrans, include nonpk, mode IN, optional true, exclude performedByPartyId
- IN glAccountId: String, optional

### createPayment (groovy, applications/accounting/servicedef/services_payment.xml)
Create a Payment. If a paymentMethodId is supplied, paymentMethodTypeId is gotten from paymentMethod. Otherwise, it must be supplied. If no paymentMethodTypeId and no paymentMethodId is supplied, then an error will be returned.
- auth: true
- auto-attributes: entity Payment, include nonpk, mode IN, optional true
- auto-attributes: entity Payment, include pk, mode INOUT, optional true
- override paymentTypeId: optional=false
- override partyIdFrom: optional=false
- override partyIdTo: optional=false
- override amount: optional=false

### setPaymentStatus (groovy, applications/accounting/servicedef/services_payment.xml)
Change the status of a Payment
- auth: true
- permission: acctgPaymentPermissionCheck UPDATE
- auto-attributes: entity Payment, include pk, mode IN, optional false
- IN statusId: String
- OUT oldStatusId: String, optional

### createInvoice (groovy, applications/accounting/servicedef/services_invoice.xml)
Create Invoice Record
- permission: acctgInvoicePermissionCheck CREATE
- auto-attributes: entity Invoice, include pk, mode INOUT, optional true
- auto-attributes: entity Invoice, include nonpk, mode IN, optional true
- override invoiceTypeId: mode=IN
- override partyIdFrom: mode=IN
- override partyId: mode=IN
- override description: allow-html=safe
- override invoiceMessage: allow-html=safe
- override invoiceDate: default-value=${date:nowTimestamp()}
- override statusId: default-value=INVOICE_IN_PROCESS

### createInvoiceItem (groovy, applications/accounting/servicedef/services_invoice.xml)
Create a new Invoice Item Record
- permission: checkInvoiceStatusInProgress 
- auto-attributes: entity InvoiceItem, include pk, mode IN, optional false
- auto-attributes: entity InvoiceItem, include nonpk, mode IN, optional true
- override invoiceItemSeqId: mode=INOUT optional=true
- override description: allow-html=safe

### setInvoiceStatus (groovy, applications/accounting/servicedef/services_invoice.xml)
Set the Invoice Status
- permission: acctgInvoicePermissionCheck UPDATE
- IN invoiceId: String
- IN statusId: String
- IN statusDate: Timestamp, default ${date:nowTimestamp()}
- IN paidDate: Timestamp, optional
- OUT invoiceTypeId: String, optional
- OUT oldStatusId: String, optional

### setFinAccountTransStatus (simple, applications/accounting/servicedef/services_finaccount.xml)
Set financial account transaction status
- IN finAccountTransId: String, optional
- IN statusId: String, optional
- OUT oldStatusId: String, optional

### getPartyAccountingPreferences (groovy, applications/accounting/servicedef/services_admin.xml)
Get accounting preferences for a party (organization)
- auth: true
- permission: acctgPrefPermissionCheck VIEW
- IN organizationPartyId: String
- OUT partyAccountingPreference: org.apache.ofbiz.entity.GenericValue, optional

## Entities

### FinAccount (applications/datamodel/entitydef/accounting-entitymodel.xml)
- fields: finAccountId (id), finAccountTypeId (id), statusId (id), finAccountName (name), finAccountCode (long-varchar), finAccountPin (long-varchar), currencyUomId (id), organizationPartyId (id), ownerPartyId (id), postToGlAccountId (id), fromDate (date-time), thruDate (date-time), isRefundable (indicator), replenishPaymentId (id), replenishLevel (currency-amount), actualBalance (currency-amount), availableBalance (currency-amount)
- primary key: finAccountId
- relation one FinAccountType: finAccountTypeId
- relation one CurrencyUom: currencyUomId=uomId
- relation one OrganizationParty: organizationPartyId=partyId
- relation one OwnerParty: ownerPartyId=partyId
- relation one PostToGlAccount: postToGlAccountId=glAccountId
- relation one ReplenishPaymentMethod: replenishPaymentId=paymentMethodId
- relation many FinAccountTypeAttr: finAccountTypeId

### FinAccountTrans (applications/datamodel/entitydef/accounting-entitymodel.xml)
- fields: finAccountTransId (id), finAccountTransTypeId (id), finAccountId (id), partyId (id), glReconciliationId (id), transactionDate (date-time), entryDate (date-time), amount (currency-amount), paymentId (id), orderId (id), orderItemSeqId (id), performedByPartyId (id), reasonEnumId (id), comments (comment), statusId (id)
- primary key: finAccountTransId
- relation one FinAccountTransType: finAccountTransTypeId
- relation many FinAccountTransTypeAttr: finAccountTransTypeId
- relation one FinAccount: finAccountId
- relation one Party: partyId
- relation one Payment: paymentId
- relation one OrderItem: orderId, orderItemSeqId
- relation one PerformedByParty: performedByPartyId=partyId
- relation one ReasonEnumeration: reasonEnumId=enumId
- relation one StatusItem: statusId
- relation one GlReconciliation: glReconciliationId

### Payment (applications/datamodel/entitydef/accounting-entitymodel.xml)
- fields: paymentId (id), paymentTypeId (id), paymentMethodTypeId (id), paymentMethodId (id), paymentGatewayResponseId (id), paymentPreferenceId (id), partyIdFrom (id), partyIdTo (id), roleTypeIdTo (id), statusId (id), effectiveDate (date-time), paymentRefNum (short-varchar), amount (currency-amount), currencyUomId (id), comments (comment), finAccountTransId (id), overrideGlAccountId (id), actualCurrencyAmount (currency-amount), actualCurrencyUomId (id)
- primary key: paymentId
- relation one PaymentType: paymentTypeId
- relation many PaymentTypeAttr: paymentTypeId
- relation one PaymentMethodType: paymentMethodTypeId
- relation one PaymentMethod: paymentMethodId
- relation one CurrencyUom: currencyUomId=uomId
- relation one ActualCurrencyUom: actualCurrencyUomId=uomId
- relation one-nofk CreditCard: paymentMethodId
- relation one-nofk EftAccount: paymentMethodId
- relation one-nofk GiftCard: paymentMethodId
- relation one OrderPaymentPreference: paymentPreferenceId=orderPaymentPreferenceId
- relation one PaymentGatewayResponse: paymentGatewayResponseId
- relation one FromParty: partyIdFrom=partyId
- relation one ToParty: partyIdTo=partyId
- relation one ToRoleType: roleTypeIdTo=roleTypeId
- relation one-nofk ToPartyRole: partyIdTo=partyId, roleTypeIdTo=roleTypeId
- relation one StatusItem: statusId
- relation one FinAccountTrans: finAccountTransId
- relation one GlAccount: overrideGlAccountId=glAccountId

### Invoice (applications/datamodel/entitydef/accounting-entitymodel.xml)
- fields: invoiceId (id), invoiceTypeId (id), partyIdFrom (id), partyId (id), roleTypeId (id), statusId (id), billingAccountId (id), contactMechId (id), invoiceDate (date-time), dueDate (date-time), paidDate (date-time), invoiceMessage (long-varchar), referenceNumber (short-varchar), description (description), currencyUomId (id), recurrenceInfoId (id)
- primary key: invoiceId
- relation one InvoiceType: invoiceTypeId
- relation many InvoiceTypeAttr: invoiceTypeId
- relation one FromParty: partyIdFrom=partyId
- relation one Party: partyId
- relation one RoleType: roleTypeId
- relation one-nofk PartyRole: partyId, roleTypeId
- relation one StatusItem: statusId
- relation one BillingAccount: billingAccountId
- relation one ContactMech: contactMechId
- relation one CurrencyUom: currencyUomId=uomId
- relation one RecurrenceInfo: recurrenceInfoId

### InvoiceItem (applications/datamodel/entitydef/accounting-entitymodel.xml)
- fields: invoiceId (id), invoiceItemSeqId (id), invoiceItemTypeId (id), overrideGlAccountId (id), overrideOrgPartyId (id), inventoryItemId (id), productId (id), productFeatureId (id), parentInvoiceId (id), parentInvoiceItemSeqId (id), uomId (id), taxableFlag (indicator), quantity (fixed-point), amount (currency-precise), description (description), taxAuthPartyId (id), taxAuthGeoId (id), taxAuthorityRateSeqId (id), salesOpportunityId (id)
- primary key: invoiceId, invoiceItemSeqId
- relation one InvoiceItemType: invoiceItemTypeId
- relation many InvoiceItemTypeAttr: invoiceItemTypeId
- relation one Invoice: invoiceId
- relation one InventoryItem: inventoryItemId
- relation one Product: productId
- relation one ProductFeature: productFeatureId
- relation one InvoiceItem: parentInvoiceId=invoiceId, parentInvoiceItemSeqId=invoiceItemSeqId
- relation many ChildrenInvoiceItem: invoiceId=parentInvoiceId, invoiceItemSeqId=parentInvoiceItemSeqId
- relation one Uom: uomId
- relation one OverrideGlAccount: overrideGlAccountId=glAccountId
- relation one TaxAuthorityParty: taxAuthPartyId=partyId
- relation one TaxGeo: taxAuthGeoId=geoId
- relation one TaxAuthorityRateProduct: taxAuthorityRateSeqId
- relation one OverrideOrgParty: overrideOrgPartyId=partyId
- relation one SalesOpportunity: salesOpportunityId

### PartyAcctgPreference (applications/datamodel/entitydef/accounting-entitymodel.xml)
- fields: partyId (id), fiscalYearStartMonth (numeric), fiscalYearStartDay (numeric), taxFormId (id), cogsMethodId (id), baseCurrencyUomId (id), invoiceSeqCustMethId (id), invoiceIdPrefix (very-short), lastInvoiceNumber (numeric), lastInvoiceRestartDate (date-time), useInvoiceIdForReturns (indicator), quoteSeqCustMethId (id), quoteIdPrefix (very-short), lastQuoteNumber (numeric), orderSeqCustMethId (id), orderIdPrefix (very-short), lastOrderNumber (numeric), refundPaymentMethodId (id), errorGlJournalId (id), enableAccounting (indicator)
- primary key: partyId
- relation one Party: partyId
- relation one TaxFormEnumeration: taxFormId=enumId
- relation one CogsEnumeration: cogsMethodId=enumId
- relation one Uom: baseCurrencyUomId=uomId
- relation one InvoiceCustomMethod: invoiceSeqCustMethId=customMethodId
- relation one QuoteCustomMethod: quoteSeqCustMethId=customMethodId
- relation one OrderCustomMethod: orderSeqCustMethId=customMethodId
- relation one PaymentMethod: refundPaymentMethodId=paymentMethodId
- relation one GlJournal: errorGlJournalId=glJournalId

### UomConversionDated (framework/common/entitydef/entitymodel.xml)
- fields: uomId (id), uomIdTo (id), fromDate (date-time), thruDate (date-time), conversionFactor (floating-point), customMethodId (id), decimalScale (numeric), roundingMode (id), purposeEnumId (id)
- primary key: uomId, uomIdTo, fromDate
- relation one DatedMainUom: uomId
- relation one DatedConvToUom: uomIdTo=uomId
- relation one uomCustomMethodCustomMethod: customMethodId=customMethodId
- relation one PurposeEnumeration: purposeEnumId=enumId

### LoanProduct (the plugin's own entity: A loan product, with the GL accounts its invoices and disbursements post to (OFB-05, OFB-06))
- fields: loanProductId (id), description (description), receivableGlAccountId (id), interestIncomeGlAccountId (id)
- primary key: loanProductId
- relation one ReceivableGlAccount: receivableGlAccountId=glAccountId
- relation one InterestIncomeGlAccount: interestIncomeGlAccountId=glAccountId

## Java classes: their imports and the methods the plugin may use

### LocalDispatcher
`import org.apache.ofbiz.service.LocalDispatcher;` (framework/service/src/main/java/org/apache/ofbiz/service/LocalDispatcher.java)
- `Map<String, Object> runSync(String serviceName, Map<String, ? extends Object> context)`
- `Map<String, Object> runSync(String serviceName, Map<String, ? extends Object> context, int transactionTimeout, boolean requireNewTransaction)`
- `Map<String, Object> runSync(String serviceName, int transactionTimeout, boolean requireNewTransaction, Object... context)`

### ServiceUtil
`import org.apache.ofbiz.service.ServiceUtil;` (framework/service/src/main/java/org/apache/ofbiz/service/ServiceUtil.java)
- `public static boolean isError(Map<String, ? extends Object> results)`
- `public static boolean isFailure(Map<String, ? extends Object> results)`
- `public static String getErrorMessage(Map<String, ? extends Object> result)`

### GenericServiceException
`import org.apache.ofbiz.service.GenericServiceException;` (framework/service/src/main/java/org/apache/ofbiz/service/GenericServiceException.java)

### Delegator
`import org.apache.ofbiz.entity.Delegator;` (framework/entity/src/main/java/org/apache/ofbiz/entity/Delegator.java)

### EntityQuery
`import org.apache.ofbiz.entity.util.EntityQuery;` (framework/entity/src/main/java/org/apache/ofbiz/entity/util/EntityQuery.java)
- `public static EntityQuery use(DelegatorProvider delegatorProvider)`
- `public EntityQuery from(String entityName)`
- `public EntityQuery from(DynamicViewEntity dynamicViewEntity)`
- `public EntityQuery where(EntityCondition entityCondition)`
- `public EntityQuery where(Map<String, Object> fieldMap)`
- `public EntityQuery where(Object... fields)`
- `public EntityQuery where(EntityCondition... entityCondition)`
- `public <T extends EntityCondition> EntityQuery where(List<T> andConditions)`
- `public EntityQuery filterByDate()`
- `public EntityQuery filterByDate(Timestamp moment)`
- `public EntityQuery filterByDate(Date moment)`
- `public EntityQuery filterByDate(String... filterByFieldName)`
- `public EntityQuery filterByDate(Timestamp moment, String... filterByFieldName)`
- `public EntityQuery orderBy(List<String> orderBy)`
- `public EntityQuery orderBy(String... fields)`
- `public GenericValue queryOne()`
- `public GenericValue queryFirst()`
- `public List<GenericValue> queryList()`

### GenericValue
`import org.apache.ofbiz.entity.GenericValue;` (framework/entity/src/main/java/org/apache/ofbiz/entity/GenericValue.java)
- `public String getString(String name)` (inherited from GenericEntity)
- `public BigDecimal getBigDecimal(String name)` (inherited from GenericEntity)
- `public java.sql.Timestamp getTimestamp(String name)` (inherited from GenericEntity)

### GenericEntityException
`import org.apache.ofbiz.entity.GenericEntityException;` (framework/entity/src/main/java/org/apache/ofbiz/entity/GenericEntityException.java)

## Canonical code from OFBiz

### A Java service: DispatchContext, the user login and ServiceUtil (applications/accounting/src/main/java/org/apache/ofbiz/accounting/finaccount/FinAccountServices.java:59)
```java
    public static Map<String, Object> createAccountAndCredit(DispatchContext dctx, Map<String, Object> context) {
        Delegator delegator = dctx.getDelegator();
        LocalDispatcher dispatcher = dctx.getDispatcher();
        String finAccountTypeId = (String) context.get("finAccountTypeId");
        String accountName = (String) context.get("accountName");
        String finAccountId = (String) context.get("finAccountId");
        Locale locale = (Locale) context.get("locale");

        // check the type
        if (finAccountTypeId == null) {
            finAccountTypeId = "SVCCRED_ACCOUNT";
        }
        if (accountName == null) {
            if ("SVCCRED_ACCOUNT".equals(finAccountTypeId)) {
                accountName = "Customer Service Credit Account";
            } else {
                accountName = "Financial Account";
            }
        }

        GenericValue userLogin = (GenericValue) context.get("userLogin");
        try {
            // find the most recent (active) service credit account for the specified party
            String partyId = (String) context.get("partyId");
            Map<String, String> lookupMap = UtilMisc.toMap("finAccountTypeId", finAccountTypeId, "ownerPartyId",
                    partyId);

            // if a productStoreId is present, restrict the accounts returned using the
            // store's payToPartyId
            String productStoreId = (String) context.get("productStoreId");
            if (UtilValidate.isNotEmpty(productStoreId)) {
                String payToPartyId = ProductStoreWorker.getProductStorePayToPartyId(productStoreId, delegator);
                if (UtilValidate.isNotEmpty(payToPartyId)) {
                    lookupMap.put("organizationPartyId", payToPartyId);
                }
            }

            // if a currencyUomId is present, use it to restrict the accounts returned
            String currencyUomId = (String) context.get("currencyUomId");
            if (UtilValidate.isNotEmpty(currencyUomId)) {
```

### Reading with EntityQuery (applications/accounting/src/main/java/org/apache/ofbiz/accounting/finaccount/FinAccountServices.java:105)
```java
                creditAccount = EntityQuery.use(delegator).from("FinAccount").where("finAccountId", finAccountId)
                        .queryOne();
            } else {
                creditAccount = EntityQuery.use(delegator).from("FinAccount").where(lookupMap).orderBy("-fromDate")
                        .filterByDate().queryFirst();
            }

            if (creditAccount == null) {
```
