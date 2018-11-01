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
using System.Data.SqlClient;
using System.Data;
using System.Net.Http.Headers;

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
            var keyVaultName = Environment.GetEnvironmentVariable("KEY_VAULT_NAME");
            var keyVaultUrl = $"https://{keyVaultName}.vault.azure.net/";

            string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            dynamic data = JsonConvert.DeserializeObject(requestBody);

            string name = data?.name;
            log.LogInformation($"BodyName. [{name}]");
            log.LogInformation($"Body. [{requestBody}]");
            name = name ?? req.Query["name"];

            var azureServiceTokenProvider = new AzureServiceTokenProvider();
            var kvClient = new KeyVaultClient(new KeyVaultClient.AuthenticationCallback(azureServiceTokenProvider.KeyVaultTokenCallback), httpClient);


            // Read Application configuration from database
//            string sqlConnectionString = (await kvClient.GetSecretAsync(keyVaultUrl, Environment.GetEnvironmentVariable("SQLMGT_CONNECTION_STRING"))).Value;
            string sqlConnectionString = (await kvClient.GetSecretAsync(keyVaultUrl, Environment.GetEnvironmentVariable("LOCALSQL_CONNECTION_STRING"))).Value;
            log.LogInformation($"SQL connection string. [{sqlConnectionString}]");
            string appChangeWebHookURL = "No appChangeWebHookURL";
            using (SqlConnection connection = new SqlConnection(sqlConnectionString))
            {
                SqlCommand cmd = new SqlCommand();
                SqlDataReader reader;

                cmd.CommandText = "SELECT [WebHookUrl] FROM[dbo].[GoCLoudApplicationWithHooks] where clientapplicationname = 'EIC'";
                cmd.CommandType = CommandType.Text;
                cmd.Connection = connection;

                connection.Open();
                reader = cmd.ExecuteReader();
                if (reader.HasRows)
                {
                    while (reader.Read())
                    {
                        appChangeWebHookURL = reader.GetString(0);
                    }
                }
                reader.Close();

                connection.Close();
            }

            string dbConnectionString = (await kvClient.GetSecretAsync(keyVaultUrl, Environment.GetEnvironmentVariable("DB_CONNECTION_STRING"))).Value;
            string dbAccessKey = (await kvClient.GetSecretAsync(keyVaultUrl, Environment.GetEnvironmentVariable("DB_ACCESS_KEY"))).Value;
            log.LogInformation($"DB connection string. [{dbConnectionString}]");

            //////////////
            // CosmosDB
            //////////////
            var client = new DocumentClient(new Uri(dbConnectionString), dbAccessKey);
            Document doc = await client.UpsertDocumentAsync(UriFactory.CreateDocumentCollectionUri("ActivityLogDb", "AppChangeTracking"), 
                new { Date = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ssK"), StartedBy = $"{data?.StartedBy}", TargetCloud = $"{data?.TargetCloud}",
                    DeploymentKey = $"{data?.DeploymentKey}", Reason = $"{data?.Reason}", Status = $"{data?.Status}",
                    appChangeWebHookURL = $"{appChangeWebHookURL}", sqlConn=$"{sqlConnectionString}" });
            //////////////
            //////////////

            //////////////
            // SQL
            //////////////
            string sqlMgmtConnectionString = (await kvClient.GetSecretAsync(keyVaultUrl, Environment.GetEnvironmentVariable("SQLMGT_CONNECTION_STRING"))).Value;
            using (SqlConnection connection = new SqlConnection(sqlMgmtConnectionString))
            {
                SqlCommand cmd = new SqlCommand("INSERT INTO GoCloudChanges VALUES (@ChangeID, @ChangeTargetType, @TargetCloud, @Summary, @Description, @State, @Status, @StatusReason, @RiskLevel, @OutageMinutes, @ProjectID, @Class, @AuthorUpn, @ApproverUpn)");
                cmd.Parameters.AddWithValue("@ChangeID", data?.ChangeID);
                cmd.Parameters.AddWithValue("@ChangeTargetType", data?.ChangeTargetType);
                cmd.Parameters.AddWithValue("@TargetCloud", data?.TargetCloud);
                cmd.Parameters.AddWithValue("@Summary", data?.Summary);
                cmd.Parameters.AddWithValue("@Description", data?.Description);
                cmd.Parameters.AddWithValue("@State", data?.State);
                cmd.Parameters.AddWithValue("@StatusReason", data?.StatusReason);
                cmd.Parameters.AddWithValue("@RiskLevel", data?.RiskLevel);
                cmd.Parameters.AddWithValue("@OutageMinutes", data?.OutageMinutes);
                cmd.Parameters.AddWithValue("@ProjectID", data?.ProjectID);
                cmd.Parameters.AddWithValue("@Class", data?.Class);
                cmd.Parameters.AddWithValue("@AuthorUpn", data?.AuthorUpn);
                cmd.Parameters.AddWithValue("@ApproverUpn", data?.ApproverUpn);
                connection.Open();
                cmd.ExecuteNonQuery();
            }
            //////////////
            //////////////
            return name != null
                ? (ActionResult)new OkObjectResult($"Hello, {name} {dbConnectionString} {requestBody}")
                : new BadRequestObjectResult("Please pass a name on the query string or in the request body");
        }
    }
}
