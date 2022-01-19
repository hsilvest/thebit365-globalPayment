using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using GlobalPayments.Api;
using GlobalPayments.Api.Services;
using Xrm.Tools.WebAPI.Requests;
using GlobalPayments.Api.Entities;
using Newtonsoft.Json.Linq;
using System.Dynamic;
using Microsoft.IdentityModel.Clients.ActiveDirectory;
using Xrm.Tools.WebAPI;

namespace RealexIntegration
{
    public static class RealexIntegration
    {
        [FunctionName("Request")]
        public static async Task<IActionResult> RealexRequest(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", Route = null)] HttpRequest req,
            ILogger log)
        {

            var productId = req.Query["productId"].ToString();

            //Sandbox
            var service = new HostedService(new GpEcomConfig
            {
                MerchantId = "MerchantId",
                AccountId = "internet",
                SharedSecret = "secret",
                ServiceUrl = "https://pay.sandbox.realexpayments.com/pay",
                HostedPaymentConfig = new HostedPaymentConfig { Version = "2" }
            });

            try
            {
                var api = InitDataVerseAPI(log);

                dynamic product = await api.Get("bit365_products", new Guid(productId),
                    new CRMGetListOptions { Select = new[] { "bit365_price" } });

                var amount = (double)((dynamic)product).bit365_price;

                // Add 3D Secure 2 Mandatory and Recommended Fields
                var hostedPaymentData = new HostedPaymentData
                {
                    CustomerEmail = "test@mail.com",
                    CustomerPhoneMobile = "353|08300000000" ,
                };

                var billingAddress = new Address
                {
                    StreetAddress1 = "sample address 1",
                    StreetAddress2 = "sample address 2",
                    StreetAddress3 = "sample address 3",
                    City = "sample city",
                    PostalCode = "00 000",
                    Country = "372" // ISO 3166-1
                };

                var hppJson = service.Charge(Decimal.Parse(amount.ToString()))
                    .WithCurrency("EUR")
                    .WithAddress(billingAddress, AddressType.Billing)
                    .WithHostedPaymentData(hostedPaymentData)
                    .Serialize();

                var orderId = ((JValue)((JObject)(JsonConvert.DeserializeObject(hppJson))).GetValue("ORDER_ID")).Value;

                dynamic productRef = new ExpandoObject();
                product.bit365_productid = productId;

                dynamic payment = new ExpandoObject();
                payment.bit365_orderid = orderId;
                payment.bit365_Product = productRef;

                await api.Create("bit365_payments", payment);

                return new OkObjectResult(hppJson);

            }
            catch (ApiException exce)
            {
                return new JsonResult($"Error {exce.Message}");
            }

        }

        [FunctionName("Response")]
        public static IActionResult RealexResponse(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = null)] HttpRequest req,
            ILogger log)
        {

            var service = new HostedService(new GpEcomConfig
            {
                MerchantId = "MerchantId",
                AccountId = "internet",
                SharedSecret = "secret",
                ServiceUrl = "https://pay.sandbox.realexpayments.com/pay",
                HostedPaymentConfig = new HostedPaymentConfig { Version = "2" }
            });

            var responseJson = req.Form["hppResponse"];

            try
            {
                Transaction response = service.ParseResponse(responseJson, true);
                var orderId = response.OrderId; // GTI5Yxb0SumL_TkDMCAxQA
                var responseCode = response.ResponseCode; // 00
                var responseMessage = response.ResponseMessage; // [ test system ] Authorised
                var responseValues = response.ResponseValues; // get values accessible by key
                var passRef = responseValues["PASREF"]; // PASS
                var authCode = responseValues["AUTHCODE"]; // AuthCode

                var api = InitDataVerseAPI(log);

                dynamic paymentUpdated = new ExpandoObject();
                paymentUpdated.bit365_responsecode = responseCode;

                dynamic payment = api.Get<ExpandoObject>("bit365_payments", $"bit365_orderid='{response.OrderId}'", new CRMGetListOptions
                {
                    Select = new[] { "bit365_returnurl", "bit365_paymentid" }
                }).Result;

                api.Update("bit365_payments", $"bit365_orderid='{response.OrderId}'", paymentUpdated);

                return new RedirectResult(payment.bit365_returnurl, true);
            }
            catch (ApiException exce)
            {
                return new JsonResult($"Error {exce.Message}");
            }
        }

        private static CRMWebAPI InitDataVerseAPI(ILogger log)
        {
            try
            {
                string serviceUrl = "https://yourorg.crm4.dynamics.com/";
                string clientId = "clientId";
                string secret = "secret";

                AuthenticationContext authContext = new AuthenticationContext
                    ("https://login.microsoftonline.com/yourTenantId");
                ClientCredential credential = new ClientCredential(clientId, secret);

                AuthenticationResult result = authContext.AcquireTokenAsync(serviceUrl, credential).Result;

                string accessToken = result.AccessToken;

                return new CRMWebAPI($"{serviceUrl}/api/data/v9.2/", accessToken);
            }
            catch
            {
                throw;
            }
        }
    }
}
