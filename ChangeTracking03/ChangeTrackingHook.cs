using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Microsoft.Azure.Services.AppAuthentication;
using Microsoft.Azure.KeyVault;
using System.Net.Http;
using Microsoft.Azure.Documents.Client;
using Microsoft.Azure.Documents;

namespace ChangeTracking03
{
    public static class ChangeTrackingHook
    {
        private static HttpClient httpClient = new HttpClient();

        [FunctionName("ChangeTrackingHook")]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", Route = null)] HttpRequest req,
            ILogger log)
        {
            log.LogInformation("C# HTTP trigger function processed a request.");

            string name = req.Query["name"];

            string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            dynamic data = JsonConvert.DeserializeObject(requestBody);
            name = name ?? data?.name;

            var azureServiceTokenProvider = new AzureServiceTokenProvider();
            var kvClient = new KeyVaultClient(new KeyVaultClient.AuthenticationCallback(azureServiceTokenProvider.KeyVaultTokenCallback), httpClient);
            var keyVaultName = Environment.GetEnvironmentVariable("KEY_VAULT_NAME");
            string dbConnectionString = (await
            kvClient.GetSecretAsync("https://appchnagetrackkv.vault.azure.net/", Environment.GetEnvironmentVariable("DB_CONNECTION_STRING"))).Value;
            string dbAccessKey = (await
            kvClient.GetSecretAsync("https://appchnagetrackkv.vault.azure.net/", Environment.GetEnvironmentVariable("DB_ACCESS_KEY"))).Value;

            var client = new DocumentClient(new Uri(dbConnectionString), dbAccessKey);
            Document doc = await client.UpsertDocumentAsync(UriFactory.CreateDocumentCollectionUri("ActivityLogDb", "AppChangeTracking"), 
                new { SomeProperty = "A Value", deploymentkey = "alibabaCloud1" });

            return name != null
                ? (ActionResult)new OkObjectResult($"Hello, {name} {dbConnectionString}")
                : new BadRequestObjectResult("Please pass a name on the query string or in the request body");
        }
    }
}
